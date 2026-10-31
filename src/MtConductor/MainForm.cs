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

    private string? _lastViewJson;
    private ConductorView? _lastView;
    private bool _pageReady;

    public MainForm()
    {
        Text = "MT Conductor";
        ClientSize = new Size(960, 760);
        MinimumSize = new Size(420, 300);
        StartPosition = FormStartPosition.CenterScreen;
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
                var warning = $"conductor error: {ex.GetType().Name} (see trace log)";
                view = _lastView is { MtOnline: true } last
                    ? last with { Warnings = [.. last.Warnings ?? [], warning] }
                    : new ConductorView(true, DateTime.Now, null, [], 0, [warning]);
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

    private void Publish(ConductorView view)
    {
        _lastViewJson = JsonSerializer.Serialize(new { type = "view", view }, MtJson.Options);
        if (_pageReady) _webView.CoreWebView2?.PostWebMessageAsJson(_lastViewJson);
    }

    private const string AppOrigin = "https://conductor.local/";

    private static bool IsAppOrigin(string? uri) =>
        uri is not null && uri.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase);

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsAppOrigin(e.Source)) return;

        string? type;
        string? url = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (doc.RootElement.TryGetProperty("url", out var u)) url = u.GetString();
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
        }
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
