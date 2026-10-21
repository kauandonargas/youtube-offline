using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MediaCore;

// Navegador VISÍVEL e controlado pelo usuário; identifica respostas HTTP de mídia.
// Não tenta resolver CAPTCHA automaticamente, exportar cookies ou descriptografar DRM.
public sealed class BrowserMediaWindow : Form
{
    private readonly WebView2 _browser = new() { Dock = DockStyle.Fill };
    private readonly TextBox _address = new() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10) };
    private readonly ListView _sources = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        MultiSelect = false, HideSelection = false
    };
    private readonly Label _status = new()
    {
        AutoSize = false, Dock = DockStyle.Bottom, Height = 44,
        TextAlign = ContentAlignment.MiddleLeft,
        Text = "Reproduza o vídeo na página. Os recursos de mídia encontrados aparecerão abaixo."
    };
    private readonly Action<BrowserMediaCandidate> _onSelect;
    private readonly string _initialUrl;
    private readonly string _profileFolder;
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private string _pageUrl = "";

    public BrowserMediaWindow(string initialUrl, string profileFolder, Action<BrowserMediaCandidate> onSelect)
    {
        if (!BrowserMediaClassifier.IsWebPage(initialUrl))
            throw new ArgumentException("Insira um endereço HTTP ou HTTPS válido.");

        _initialUrl = initialUrl;
        _profileFolder = profileFolder;
        _onSelect = onSelect;
        Text = "Navegador de mídia · inspeção de fontes";
        Width = 1160;
        Height = 900;
        MinimumSize = new Size(780, 620);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(22, 27, 35);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 45, ColumnCount = 4,
            Padding = new Padding(6), BackColor = Color.FromArgb(31, 38, 48)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 166));

        var go = new Button { Text = "Ir", Dock = DockStyle.Fill };
        var reload = new Button { Text = "Atualizar", Dock = DockStyle.Fill };
        var scan = new Button { Text = "Detectar no HTML", Dock = DockStyle.Fill };
        header.Controls.Add(go, 0, 0);
        header.Controls.Add(_address, 1, 0);
        header.Controls.Add(reload, 2, 0);
        header.Controls.Add(scan, 3, 0);
        go.Click += (_, _) => Navigate(_address.Text);
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            Navigate(_address.Text);
        };
        reload.Click += (_, _) => _browser.CoreWebView2?.Reload();
        scan.Click += async (_, _) => await InspectHtmlAsync();

        var lower = new Panel { Dock = DockStyle.Bottom, Height = 225, Padding = new Padding(8) };
        var controls = new Panel { Dock = DockStyle.Bottom, Height = 42 };
        var select = new Button { Text = "Usar fonte selecionada", Width = 210, Dock = DockStyle.Right };
        var clear = new Button { Text = "Limpar lista", Width = 100, Dock = DockStyle.Right };
        var info = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Text = "Escolha uma fonte e depois inicie o download na janela principal."
        };
        controls.Controls.Add(info);
        controls.Controls.Add(select);
        controls.Controls.Add(clear);

        _sources.Columns.Add("Tipo", 92);
        _sources.Columns.Add("Origem", 180);
        _sources.Columns.Add("Endereço de mídia", 780);
        _sources.DoubleClick += (_, _) => UseSelection();
        select.Click += (_, _) => UseSelection();
        clear.Click += (_, _) =>
        {
            _seen.Clear();
            _sources.Items.Clear();
            _status.Text = "Lista limpa. Reproduza ou recarregue a página para detectar outras fontes.";
        };
        lower.Controls.Add(_sources);
        lower.Controls.Add(_status);
        lower.Controls.Add(controls);

        Controls.Add(_browser);
        Controls.Add(lower);
        Controls.Add(header);
        Shown += async (_, _) => await InitializeBrowserAsync();
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            Directory.CreateDirectory(_profileFolder);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profileFolder);
            await _browser.EnsureCoreWebView2Async(environment);
            var core = _browser.CoreWebView2;
            core.Settings.IsStatusBarEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (BrowserMediaClassifier.IsWebPage(e.Uri)) Navigate(e.Uri);
            };
            core.NavigationStarting += (_, e) =>
            {
                if (!BrowserMediaClassifier.IsWebPage(e.Uri))
                {
                    e.Cancel = true;
                    return;
                }
                _pageUrl = e.Uri;
                _address.Text = e.Uri;
                _status.Text = "Página carregando. Se existir uma verificação, resolva-a manualmente no próprio site.";
            };
            core.NavigationCompleted += (_, e) =>
            {
                _status.Text = e.IsSuccess
                    ? "Reproduza a mídia. As respostas de vídeo/áudio recebidas pelo navegador serão listadas."
                    : "A navegação falhou; confira o endereço ou a disponibilidade do site.";
            };
            core.WebResourceResponseReceived += (_, e) =>
            {
                try
                {
                    string mime = "";
                    try { mime = e.Response.Headers.GetHeader("Content-Type"); }
                    catch (Exception) { }
                    var candidate = BrowserMediaClassifier.Examine(
                        e.Request.Uri, mime, _pageUrl, e.Response.StatusCode);
                    if (candidate is not null) AddCandidate(candidate);
                }
                catch (Exception) { /* A inspeção nunca deve interromper o navegador. */ }
            };
            Navigate(_initialUrl);
        }
        catch (Exception ex)
        {
            _status.Text = "Falha ao inicializar o navegador: " + ex.Message;
        }
    }

    private void Navigate(string? url)
    {
        if (!BrowserMediaClassifier.IsWebPage(url))
        {
            MessageBox.Show(this, "Informe um link HTTP ou HTTPS válido.", "Navegação");
            return;
        }
        try { _browser.CoreWebView2?.Navigate(url!); }
        catch (Exception ex) { _status.Text = "Não foi possível abrir: " + ex.Message; }
    }

    private async Task InspectHtmlAsync()
    {
        if (_browser.CoreWebView2 is null) return;
        try
        {
            // Inspeciona somente fontes públicas que o documento expõe ao próprio navegador.
            const string script = @"Array.from(document.querySelectorAll('video,audio,source'))
              .flatMap(x => [x.currentSrc || '', x.src || ''])
              .filter(x => /^https?:\/\//i.test(x))
              .slice(0, 60)";
            string json = await _browser.CoreWebView2.ExecuteScriptAsync(script);
            var urls = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
            foreach (var url in urls)
            {
                var candidate = BrowserMediaClassifier.Examine(url, "", _pageUrl, 200);
                if (candidate is not null) AddCandidate(candidate);
            }
            _status.Text = urls.Length == 0
                ? "Nenhum endereço HTML5 explícito. Tente reproduzir o vídeo e observe a lista."
                : "Inspeção HTML5 concluída. Selecione uma fonte da lista.";
        }
        catch (Exception ex) { _status.Text = "Não foi possível inspecionar a página: " + ex.Message; }
    }

    private void AddCandidate(BrowserMediaCandidate candidate)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => AddCandidate(candidate))); }
            catch (InvalidOperationException) { }
            return;
        }
        if (!_seen.Add(candidate.Url) || _sources.Items.Count >= 150) return;
        string host = Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri) ? uri.Host : "Desconhecido";
        var item = new ListViewItem(new[] { candidate.Kind, host, candidate.Url }) { Tag = candidate };
        _sources.Items.Add(item);
        _status.Text = _sources.Items.Count + " fonte(s) detectada(s). Uma fonte pode conter apenas vídeo ou apenas áudio.";
    }

    private void UseSelection()
    {
        if (_sources.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "Selecione uma fonte de vídeo, áudio, HLS ou DASH da lista.", "Fonte de mídia");
            return;
        }
        if (_sources.SelectedItems[0].Tag is not BrowserMediaCandidate candidate) return;
        _onSelect(candidate);
        Close();
    }
}
