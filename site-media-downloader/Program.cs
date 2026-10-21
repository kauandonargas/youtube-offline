using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SiteMediaDownloader;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainWindow());
    }
}

public sealed class MainWindow : Form
{
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill };
    private CancellationTokenSource? _active;
    private string _folder;

    public MainWindow()
    {
        Text = "Site Media Downloader";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1190;
        Height = 850;
        MinimumSize = new Size(850, 660);
        BackColor = Color.FromArgb(15, 17, 23);
        _folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Site Media Downloader");
        Directory.CreateDirectory(_folder);
        Controls.Add(_view);
        Shown += async (_, _) => await InitializeAsync();
        FormClosed += (_, _) => _active?.Cancel();
    }

    private async Task InitializeAsync()
    {
        try
        {
            string data = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Site Media Downloader", "WebView2");
            Directory.CreateDirectory(data);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: data);
            await _view.EnsureCoreWebView2Async(environment);
            var core = _view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
            core.WebMessageReceived += OnWebMessage;
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith("https://site-media.local/", StringComparison.OrdinalIgnoreCase))
                    e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;

            core.SetVirtualHostNameToFolderMapping(
                "site-media.local",
                Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.Navigate("https://site-media.local/index.html");
            core.NavigationCompleted += (_, _) =>
                Send("ready", new { folder = _folder, version = "0.2.0" });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "O aplicativo precisa do Microsoft Edge WebView2 Runtime. " +
                "Não foi possível abrir a interface.\n\n" + ex.Message,
                "Site Media Downloader", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (type == "choose-folder")
            {
                using var folderDialog = new FolderBrowserDialog
                {
                    Description = "Escolha onde salvar seus vídeos e áudios",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = true,
                    SelectedPath = _folder
                };
                if (folderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    _folder = folderDialog.SelectedPath;
                    Send("folder", new { folder = _folder });
                }
                return;
            }
            if (type == "open-folder")
            {
                Directory.CreateDirectory(_folder);
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                start.ArgumentList.Add(_folder);
                Process.Start(start);
                return;
            }
            if (type == "open-page")
            {
                string page = root.TryGetProperty("url", out var pageElement) ? pageElement.GetString() ?? "" : "";
                if (!DownloadEngine.IsMediaUrl(page))
                    throw new ArgumentException("Link da página inválido.");
                Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
                return;
            }
            if (type == "cancel")
            {
                _active?.Cancel();
                return;
            }
            if (type != "start" || _active is not null) return;

            string url = root.TryGetProperty("url", out var u) ? u.GetString()?.Trim() ?? "" : "";
            string mode = root.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "video";
            string quality = root.TryGetProperty("quality", out var q) ? q.GetString() ?? "best" : "best";
            string audioFormat = root.TryGetProperty("audioFormat", out var f) ? f.GetString() ?? "original" : "original";
            if (audioFormat is not ("original" or "mp3" or "m4a" or "opus")) audioFormat = "original";
            if (quality is not ("best" or "2160" or "1440" or "1080" or "720" or "480" or "360"))
                quality = "best";

            if (!DownloadEngine.IsMediaUrl(url))
            {
                Send("error", new { text = "Cole um link completo de uma página http:// ou https://." });
                return;
            }

            var browser = root.TryGetProperty("browser", out var b) ? b.GetString() : "none";
            var settings = new DownloadEngine.DownloadSettings(
                url, _folder, mode == "audio", quality, audioFormat, MediaCore.MediaDiagnostics.NormalizeBrowser(browser));
            _active = new CancellationTokenSource();
            var token = _active.Token;
            Send("started", new { url });
            _ = RunDownloadAsync(settings, token);
        }
        catch (Exception ex)
        {
            Send("error", new { text = "Não foi possível processar o pedido: " + ex.Message });
        }
    }

    private async Task RunDownloadAsync(DownloadEngine.DownloadSettings settings, CancellationToken token)
    {
        try
        {
            await DownloadEngine.DownloadAsync(settings, Send, token);
        }
        catch (OperationCanceledException)
        {
            Send("cancelled", new { text = "Download cancelado." });
        }
        catch (Exception ex)
        {
            Send("error", new { text = ex.Message });
        }
        finally
        {
            _active?.Dispose();
            _active = null;
            Send("idle", new { });
        }
    }

    private void Send(string type, object data)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => Send(type, data))); } catch (InvalidOperationException) { }
            return;
        }
        try
        {
            var message = JsonSerializer.Serialize(new { type, data });
            _ = _view.CoreWebView2?.ExecuteScriptAsync(
                "window.receiveNative?.(" + message + ");");
        }
        catch { }
    }
}
