using Microsoft.Web.WebView2.WinForms;

namespace MtConductor;

/// <summary>
/// Host window: a single WebView2 that renders the project cards from wwwroot.
/// MT Conductor only reads MultiTerminal's REST API; it never starts, stops or kills MultiTerminal.exe.
/// </summary>
public sealed class MainForm : Form
{
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };

    public MainForm()
    {
        Text = "MT Conductor";
        ClientSize = new Size(900, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_webView);
        Load += async (_, _) => await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MtConductor", "WebView2");
        var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        await _webView.EnsureCoreWebView2Async(env);

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "conductor.local", wwwroot, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Deny);
        _webView.CoreWebView2.Navigate("https://conductor.local/index.html");
    }
}
