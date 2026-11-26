using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MtConductor.Api;
using MtConductor.Cards;
using MtConductor.Github;

namespace MtConductor;

/// <summary>
/// Host window: a single WebView2 that renders the project cards from wwwroot, fed by a polling
/// loop over MT's REST API. MT Conductor only reads that API; it never starts, stops or kills
/// MultiTerminal.exe and never reads MT's database.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly TimeSpan OnlineInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] OfflineBackoff =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)];

    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly CancellationTokenSource _closing = new();
    private readonly ConductorPoller _poller = new(
        MtApiClient.CreateDefault(),
        new GhPrClient(cacheDuration: TimeSpan.FromMinutes(2)),
        new GitRemoteResolver(cacheDuration: TimeSpan.FromMinutes(10)));

    private readonly ConductorSettings _settings = ConductorSettings.CreateDefault();
    private CardSortOrder _cardOrder;
    private bool _cardOrderUnsaved;   // the last save failed: shown as a warning until one works
    private const string CardOrderUnsavedWarning = "card order not saved";

    private string? _lastViewJson;
    private ConductorView? _lastView;
    private bool _pageReady;

    // Projects with an Open request in flight (UI thread only).
    private readonly HashSet<string> _opening = new(StringComparer.Ordinal);

    public MainForm()
    {
        Text = "MT Conductor";
        ClientSize = new Size(960, 760);
        MinimumSize = new Size(420, 300);
        StartPosition = FormStartPosition.CenterScreen;
        _cardOrder = _settings.LoadCardOrder();
        Controls.Add(_webView);
        Load += async (_, _) => await StartAsync();
        FormClosing += (_, _) => _closing.Cancel();
    }

    private async Task StartAsync()
    {
        try
        {
            await InitializeWebViewAsync();
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            MessageBox.Show(this,
                "MT Conductor needs the Microsoft Edge WebView2 Runtime.\n\n" + ex.Message,
                "MT Conductor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        await PollLoopAsync(_closing.Token);
    }

    private async Task InitializeWebViewAsync()
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MtConductor", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        await _webView.EnsureCoreWebView2Async(env);

        var core = _webView.CoreWebView2;
        core.Settings.AreDevToolsEnabled = System.Diagnostics.Debugger.IsAttached;
        core.Settings.IsStatusBarEnabled = false;
        core.WebMessageReceived += OnWebMessage;
        core.NewWindowRequested += OnNewWindowRequested;
        core.NavigationStarting += (_, e) =>
        {
            // The page must never leave the app's own origin (it would inherit chrome.webview).
            if (!IsAppOrigin(e.Uri)) e.Cancel = true;
        };

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping("conductor.local", wwwroot, CoreWebView2HostResourceAccessKind.Deny);
        core.Navigate(AppOrigin + "index.html");
    }

    // Runs on the UI thread: awaits resume on the WinForms SynchronizationContext.
    private async Task PollLoopAsync(CancellationToken ct)
    {
        var offlineStreak = 0;
        while (!ct.IsCancellationRequested)
        {
            ConductorView view;
            try
            {
                view = await _poller.RefreshAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let one bad refresh stop polling. This is our fault, not MT being down:
                // keep the last cards and say so, rather than claiming "MT offline".
                System.Diagnostics.Trace.TraceError($"MT Conductor refresh failed: {ex}");
                view = ProjectCardBuilder.RefreshFailed(_lastView, $"{ex.GetType().Name} (see trace log)", DateTime.Now);
            }

            _lastView = view;
            Publish(view);

            offlineStreak = view.MtOnline ? 0 : offlineStreak + 1;
            var delay = view.MtOnline
                ? OnlineInterval
                : OfflineBackoff[Math.Min(offlineStreak - 1, OfflineBackoff.Length - 1)];

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // Sorts for the Owner's chosen order on the way out; _lastView keeps the builder's order.
    private void Publish(ConductorView view)
    {
        var sorted = view with { Cards = CardOrder.Apply(view.Cards, _cardOrder) };
        if (_cardOrderUnsaved)
            sorted = sorted with { Warnings = [.. view.Warnings ?? [], CardOrderUnsavedWarning] };
        _lastViewJson = JsonSerializer.Serialize(
            new { type = "view", view = sorted, cardOrder = CardOrder.Key(_cardOrder) }, MtJson.Options);
        if (_pageReady) _webView.CoreWebView2?.PostWebMessageAsJson(_lastViewJson);
    }

    /// <summary>
    /// The header drop-down. Unknown keys are ignored. The cards re-sort first, then the choice is
    /// saved; if saving fails the status pill says so, and picking any order again retries.
    /// </summary>
    private void SetCardOrder(string? key)
    {
        if (CardOrder.Parse(key) is not { } order || (order == _cardOrder && !_cardOrderUnsaved)) return;
        _cardOrder = order;
        if (_lastView is not null) Publish(_lastView);
        var unsaved = !_settings.SaveCardOrder(order);
        if (unsaved == _cardOrderUnsaved) return;
        _cardOrderUnsaved = unsaved;
        if (_lastView is not null) Publish(_lastView);   // add or clear the warning
    }

    private const string AppOrigin = "https://conductor.local/";

    private static bool IsAppOrigin(string? uri) =>
        uri is not null && uri.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase);

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsAppOrigin(e.Source)) return;

        string? type;
        string? url = null;
        string? projectId = null;
        string? order = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String) url = u.GetString();
            if (root.TryGetProperty("projectId", out var p) && p.ValueKind == JsonValueKind.String) projectId = p.GetString();
            if (root.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.String) order = o.GetString();
        }
        catch (JsonException)
        {
            return;
        }

        switch (type)
        {
            case "ready":
                _pageReady = true;
                if (_lastViewJson is not null) _webView.CoreWebView2.PostWebMessageAsJson(_lastViewJson);
                break;
            case "openUrl":
                OpenExternal(url);
                break;
            case "openProject":
                _ = OpenProjectAsync(projectId);
                break;
            case "setOrder":
                SetCardOrder(order);
                break;
        }
    }

    /// <summary>
    /// The Open button. Only ids of cards currently on screen are accepted, one request per project
    /// at a time. MT's process is allowed to take the foreground first, or Windows would only flash
    /// its taskbar button. The outcome goes back to the page as an "openResult" message.
    /// </summary>
    private async Task OpenProjectAsync(string? projectId)
    {
        if (string.IsNullOrEmpty(projectId)) return;

        // Every path answers the page, so its "Asking MT…" state always ends.
        if (_lastView is not { MtOnline: true } view)
        {
            PostOpenResult(projectId, new MtOpenResult(MtOpenKind.Offline));
            return;
        }
        if (!view.Cards.Any(c => c.Id == projectId))
        {
            PostOpenResult(projectId, new MtOpenResult(MtOpenKind.Failed, Detail: "That project isn't on screen any more."));
            return;
        }
        if (!_opening.Add(projectId)) return;   // already asking; that request will answer

        MtOpenResult result;
        try
        {
            AllowMtToTakeForeground(await _poller.RefreshMtPidAsync(_closing.Token));
            result = await _poller.OpenProjectAsync(projectId, _closing.Token);
        }
        catch (OperationCanceledException)
        {
            return;   // closing
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"MT Conductor open failed: {ex}");
            result = new MtOpenResult(MtOpenKind.Failed, Detail: "Conductor error (see trace log).");
        }
        finally
        {
            _opening.Remove(projectId);
        }

        PostOpenResult(projectId, result);
    }

    /// <summary>
    /// Windows only lets the foreground process hand focus to another. The pid comes from an
    /// unauthenticated local health check, so it is only granted if that process really is
    /// MultiTerminal (read-only lookup; never starts or stops anything).
    /// </summary>
    private static void AllowMtToTakeForeground(int? mtPid)
    {
        if (mtPid is not { } pid) return;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (!process.ProcessName.Equals("MultiTerminal", StringComparison.OrdinalIgnoreCase)) return;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return;   // gone, or not ours to look at
        }
        NativeMethods.AllowSetForegroundWindow(pid);
    }

    // The page's switch on "kind" pins these lower-cased MtOpenKind names (focused, launching,
    // notfound, failed, unsupported, offline, noanswer).
    private void PostOpenResult(string projectId, MtOpenResult result)
    {
        if (!_pageReady) return;
        var message = new
        {
            type = "openResult",
            projectId,
            kind = result.Kind.ToString().ToLowerInvariant(),
            paneTitle = Clip(result.PaneTitle),
            result.Floating,
            result.OtherPanes,
            detail = Clip(result.Detail),
        };
        _webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, MtJson.Options));
    }

    private static string? Clip(string? text) => text is { Length: > 200 } ? text[..200] + "…" : text;

    private static class NativeMethods
    {
        // Lets MT (a different process) bring its own window to the front when it handles Open.
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AllowSetForegroundWindow(int processId);
    }

    // Links (PRs) open in the default browser, never inside the app.
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternal(e.Uri);
    }

    private static void OpenExternal(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Couldn't open {uri}: {ex.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing.Cancel();
            _closing.Dispose();
            _webView.Dispose();
        }
        base.Dispose(disposing);
    }
}
