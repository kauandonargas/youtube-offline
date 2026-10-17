from pathlib import Path


def replace_once(path, old, new):
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    if old not in text:
        raise SystemExit(f'Trecho esperado nao encontrado em {path}: {old[:120]!r}')
    p.write_text(text.replace(old, new, 1), encoding='utf-8', newline='\n')


service = 'src/LocalTube.App/DownloadService.cs'

replace_once(
    service,
    r'''    private static readonly Regex ProgressRegex = new(@"download:\s*(?<pct>[0-9.]+)%\|(?<speed>[^|]*)\|(?<eta>[^|]*)\|(?<downloaded>[^|]*)\|(?<total>[^|]*)\|(?<estimated>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);''',
    r'''    private static readonly Regex ProgressRegex = new(@"download:\s*(?<pct>[0-9.]+)%\|(?<speed>[^|]*)\|(?<eta>[^|]*)\|(?<downloaded>[^|]*)\|(?<total>[^|]*)\|(?<estimated>[^|]*)\|(?<playlistIndex>[^|]*)\|(?<playlistTotal>[^|]*)\|(?<title>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);'''
)

replace_once(
    service,
    '''        if (!_analysisCache.TryRemove(key, out var analysis))\n            analysis = await AnalyzeSourceAsync(req.Url, browser);\n\n        var isPlaylist = analysis.IsPlaylist || req.Playlist;''',
    '''        // Se a tela ja analisou o link, reaproveita o resultado. Caso contrario,\n        // inicia o download diretamente sem fazer uma segunda consulta ao YouTube.\n        if (!_analysisCache.TryRemove(key, out var analysis))\n            analysis = CreateDirectAnalysis(req.Url, req.Playlist);\n\n        var isPlaylist = analysis.IsPlaylist || req.Playlist || IsLikelyPlaylistUrl(req.Url);'''
)

replace_once(
    service,
    '''            // Nao existe mais fallback automatico para Chrome/Edge. Cookies so sao\n            // usados quando o usuario escolhe explicitamente um navegador.\n            if (attempt.ExitCode != 0 && IsBotChallenge(attempt.Error) && IsExplicitBrowser(entry.Request.CookieBrowser))\n            {\n                var withCookies = WithBrowserCookies(args, entry.Request.CookieBrowser);\n                attempt = await RunDownloadAttemptAsync(tool, withCookies, item, entry.IsPlaylist, entry.PlaylistCount, cts.Token);\n            }\n\n''',
    '''            // Uma unica tentativa anonima, como no fluxo antigo que funcionava.\n            // Nao acessa bancos de cookies do Chrome/Edge automaticamente.\n\n'''
)

replace_once(
    service,
    '''            "--progress-template", "download:%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s",''',
    '''            "--progress-template", "download:%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(info.playlist_index)s|%(info.playlist_count)s|%(info.title)s",'''
)

replace_once(
    service,
    '''                else if (estimatedTotal > 0)\n                {\n                    item.TotalBytes = estimatedTotal;\n                    item.TotalBytesEstimated = true;\n                }''',
    '''                else if (estimatedTotal > 0)\n                {\n                    item.TotalBytes = estimatedTotal;\n                    item.TotalBytesEstimated = true;\n                }\n\n                if (isPlaylist)\n                {\n                    if (int.TryParse(progress.Groups["playlistIndex"].Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pi) && pi > 0)\n                        item.PlaylistIndex = pi;\n                    if (int.TryParse(progress.Groups["playlistTotal"].Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pt) && pt > 0)\n                        item.PlaylistTotal = pt;\n                }\n\n                var currentTitle = progress.Groups["title"].Value.Trim();\n                if (!string.IsNullOrWhiteSpace(currentTitle) && !currentTitle.Equals("NA", StringComparison.OrdinalIgnoreCase))\n                    item.Title = currentTitle;'''
)

replace_once(
    service,
    '''    private static string NormalizeCookieMode(string? requested)\n    {\n        var mode = (requested ?? "none").Trim().ToLowerInvariant();\n        // Compatibilidade com instalacoes anteriores: "auto" agora significa anonimo.\n        // Isso impede acesso automatico aos bancos de cookies de Chrome/Edge.\n        return mode is "edge" or "chrome" or "firefox" or "brave" ? mode : "none";\n    }''',
    '''    private static string NormalizeCookieMode(string? requested)\n    {\n        // 0.5.10: volta ao comportamento anonimo simples. Nenhuma sessao do navegador\n        // e lida automaticamente pelo aplicativo.\n        return "none";\n    }'''
)

helper_anchor = '''    private List<string> BuildArgs(DownloadRequest req, string? ffmpeg, string url, bool isPlaylist, string playlistTitle)\n'''
helper = r'''    private static SourceAnalysis CreateDirectAnalysis(string url, bool requestedPlaylist)
    {
        var isPlaylist = requestedPlaylist || IsLikelyPlaylistUrl(url);
        var sourceId = isPlaylist
            ? TryGetQueryValue(url, "list") ?? NormalizeUrl(url)
            : TryExtractYoutubeVideoId(url) ?? NormalizeUrl(url);

        return new SourceAnalysis
        {
            IsPlaylist = isPlaylist,
            Title = isPlaylist ? "Playlist" : "Video",
            SourceId = sourceId,
            Entries = new List<SourceEntry>()
        };
    }

    private static bool IsLikelyPlaylistUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.AbsolutePath.Contains("/playlist", StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrWhiteSpace(TryGetQueryValue(url, "list"));
    }

    private static string? TryExtractYoutubeVideoId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        var fromQuery = TryGetQueryValue(url, "v");
        if (!string.IsNullOrWhiteSpace(fromQuery)) return fromQuery;

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("shorts", StringComparison.OrdinalIgnoreCase)) return parts[1];
        if (parts.Length >= 2 && parts[0].Equals("live", StringComparison.OrdinalIgnoreCase)) return parts[1];
        if (parts.Length >= 2 && parts[0].Equals("embed", StringComparison.OrdinalIgnoreCase)) return parts[1];
        return null;
    }

    private static string? TryGetQueryValue(string url, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 0 || !pair[0].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (pair.Length == 1) return "";
            return Uri.UnescapeDataString(pair[1].Replace('+', ' '));
        }
        return null;
    }

'''
replace_once(service, helper_anchor, helper + helper_anchor)

queue_path = 'src/LocalTube.App/wwwroot/queue-fixes.js'
replace_once(
    queue_path,
    '''    .download-task-progress {\n      margin: 13px 0 8px;\n    }''',
    '''    .download-task-progress {\n      margin: 13px 0 8px;\n      overflow: hidden;\n    }\n    .download-task-progress.pending span {\n      width: 34% !important;\n      animation: download-pulse 1.15s ease-in-out infinite alternate;\n    }\n    @keyframes download-pulse {\n      from { transform: translateX(-25%); opacity: .45; }\n      to { transform: translateX(225%); opacity: 1; }\n    }'''
)
replace_once(
    queue_path,
    "if (item.status === 'preparing') return 'Verificando ferramentas e iniciando a conexão com o YouTube…';",
    "if (item.status === 'preparing') return 'Preparando o download e iniciando o yt-dlp…';"
)
replace_once(
    queue_path,
    "if (item.status === 'downloading') {\n      if (job.isPlaylist) {",
    "if (item.status === 'downloading') {\n      if (Number(item.progress || 0) <= 0 && !Number(item.downloadedBytes || 0)) return 'Conectado ao yt-dlp • aguardando os primeiros dados do YouTube…';\n      if (job.isPlaylist) {"
)
replace_once(
    queue_path,
    '''              <div class="bar download-task-progress"><span style="width:${progress}%"></span></div>''',
    '''              <div class="bar download-task-progress${(item.status === 'preparing' || (item.status === 'downloading' && progress <= 0)) ? ' pending' : ''}"><span style="width:${progress}%"></span></div>'''
)

queue = Path(queue_path)
q = queue.read_text(encoding='utf-8')
insert = r'''

  // A tela acompanha o motor atual: playlist inteira = uma tarefa.
  // A opcao antiga de cookies deixa de ser mostrada porque o motor volta a ser anonimo.
  const originalRenderDownloads0510 = renderDownloads;
  renderDownloads = function () {
    state.downloadCookieBrowser = 'none';
    localStorage.setItem('youtubeOfflineCookieBrowser', 'none');
    originalRenderDownloads0510();

    $('.download-auth-field')?.remove();

    const hint = $('.download-hint');
    if (hint) hint.textContent = 'O app evita downloads repetidos pelo ID original. Uma playlist inteira ocupa uma unica tarefa na fila.';

    if (state.downloadAnalysis?.isPlaylist) {
      const detected = $('.playlist-detected');
      if (detected) {
        const strong = $('strong', detected);
        const text = $('span', detected);
        if (strong) strong.textContent = 'Playlist detectada.';
        if (text) text.textContent = 'A playlist inteira sera baixada por uma unica execucao do yt-dlp.';
      }
      const start = $('#startDownload');
      if (start) start.textContent = 'Baixar playlist';
    }
  };
'''
ending = '})();\n'
if not q.endswith(ending):
    raise SystemExit('Final inesperado em queue-fixes.js')
q = q[:-len(ending)] + insert + '\n' + ending
queue.write_text(q, encoding='utf-8', newline='\n')

replace_once('src/LocalTube.App/wwwroot/index.html', '/queue-fixes.js?v=0.5.9', '/queue-fixes.js?v=0.5.10')
replace_once('src/LocalTube.App/LocalTube.App.csproj', '<Version>0.5.9</Version>', '<Version>0.5.10</Version>')
Path('VERSION').write_text('0.5.10\n', encoding='utf-8')
Path('RELEASE_NOTES.md').write_text(
    '''# YouTube Offline 0.5.10\n\n- Corrige o motor de download e a interface ao mesmo tempo.\n- O download nao faz uma segunda analise do link antes de iniciar; se a analise da tela ja existe, ela e reaproveitada.\n- Playlists continuam como uma unica tarefa e uma unica execucao do yt-dlp.\n- Desativa totalmente o acesso automatico a cookies de navegadores.\n- A fila mostra claramente Preparando, Baixando agora, porcentagem, MB/GB, tamanho, velocidade e tempo restante.\n- Enquanto ainda nao chegaram bytes, a barra fica animada e informa que o yt-dlp esta conectado e aguardando dados do YouTube.\n- Em playlists, o titulo e o numero do video atual passam a ser atualizados pelo proprio progresso do yt-dlp.\n''',
    encoding='utf-8',
    newline='\n'
)
