using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LocalTube;

public sealed class MainForm : Form
{
    private readonly string _url;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };

    public MainForm(string url)
    {
        _url = url;
        Text = "YouTube Offline";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1480;
        Height = 920;
        MinimumSize = new Size(960, 640);
        WindowState = FormWindowState.Maximized;
        BackColor = Color.FromArgb(15, 15, 15);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Controls.Add(_webView);

        Shown += async (_, _) => await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouTube Offline", "WebView2");
            Directory.CreateDirectory(userData);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await _webView.EnsureCoreWebView2Async(environment);
            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
            core.WebMessageReceived += Core_WebMessageReceived;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
                }
            };
            core.Navigate(_url);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Não foi possível iniciar a janela do YouTube Offline. O Microsoft Edge WebView2 Runtime é necessário.\n\n" + ex.Message,
                "YouTube Offline",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
    }

    private void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (!doc.RootElement.TryGetProperty("type", out var type)) return;
            var command = type.GetString();

            if (command == "select-folder")
            {
                BeginInvoke(new Action(() =>
                {
                    using var dialog = new FolderBrowserDialog
                    {
                        Description = "Escolha a pasta da biblioteca do YouTube Offline",
                        UseDescriptionForTitle = true,
                        ShowNewFolderButton = true
                    };
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        var json = JsonSerializer.Serialize(dialog.SelectedPath);
                        _webView.CoreWebView2?.ExecuteScriptAsync($"window.localTubeNativeFolderSelected?.({json});");
                    }
                }));
            }
        }
        catch { }
    }
}
