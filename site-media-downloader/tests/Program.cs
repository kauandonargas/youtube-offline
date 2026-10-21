using MediaCore;
using SiteMediaDownloader;

static void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}

Check(DownloadEngine.IsMediaUrl("https://example.com/watch?id=abc"), "Página HTTPS deve ser aceita");
Check(!DownloadEngine.IsMediaUrl("javascript:alert(1)"), "Rejeitar esquemas não HTTP");
Check(!DownloadEngine.IsMediaUrl("file:///C:/private.txt"), "Rejeitar arquivos locais");
Check(DownloadEngine.IsCloudflare("ERROR: [generic] Got HTTP Error 403 caused by Cloudflare anti-bot challenge; try again with --extractor-args \"generic:impersonate\""), "Reconhecer erro real de Cloudflare");
Check(DownloadEngine.IsAccessRestricted("This video is private"), "Reconhecer acesso restrito");
Check(DownloadEngine.IsRateLimited("HTTP Error 429: Too Many Requests"), "Reconhecer limite de taxa");
foreach (bool audio in new[] { false, true })
{
    var profiles = DownloadEngine.Profiles(audio);
    Check(profiles.Count >= 10, "Faltam perfis");
    Check(profiles.Select(p => p.Name).Distinct().Count() == profiles.Count, "Perfis duplicados");
    Check(profiles.Any(p => p.Options.Contains("generic:impersonate")), "Falta opção de compatibilidade genérica");
    Check(profiles.Any(p => p.Options.Contains("--force-generic-extractor")), "Falta extrator de página genérico");
    Check(profiles.All(p => !p.Options.Contains("--cookies-from-browser")), "Cookies não devem ser importados");
    foreach (var p in profiles)
    {
        var settings = new DownloadEngine.DownloadSettings(
            "https://example.com/page", @"C:\Temp\Media", audio, "720", audio ? "mp3" : "original");
        var arguments = DownloadEngine.BuildArguments(settings, p, @"C:\Temp\Media", settings.Url);
        Check(arguments[^1] == settings.Url, "Página precisa ser repassada ao extrator");
        Check(arguments.Contains("--no-playlist"), "Download unitário é o padrão");
        Check(arguments.Contains("-f"), "Seletor de formato ausente");
        Check(DownloadEngine.FormatSelector(audio, "720", p.Format).Length > 0, "Seletor vazio");
        if (!audio) Check(DownloadEngine.FormatSelector(false, "720", p.Format).Contains("height<=720"), "Respeitar qualidade");
        if (audio) Check(arguments.Contains("--audio-format") && arguments.Contains("mp3"), "Conversão MP3");
    }
}

var page = new Uri("https://example.com/articles/watch.html");
var found = PageMediaDiscovery.FindInHtml(page,
    "<video controls src='/media/lecture.mp4'></video>" +
    "<source src='https://cdn.example.com/movie.m3u8'>" +
    "<meta property='og:video' content='/preview.mp4'>" +
    "<script type='application/ld+json'>{\"contentUrl\":\"https://cdn.example.com/master.mp4\"}</script>" +
    "<iframe src='https://player.example.com/embed/123'></iframe>" +
    "<video src='http://127.0.0.1/private.mp4'></video>");
Check(found.Contains("https://example.com/media/lecture.mp4"), "Detectar tag video");
Check(found.Contains("https://cdn.example.com/movie.m3u8"), "Detectar tag source");
Check(found.Contains("https://example.com/preview.mp4"), "Detectar OpenGraph");
Check(found.Contains("https://cdn.example.com/master.mp4"), "Detectar JSON-LD");
Check(found.Contains("https://player.example.com/embed/123"), "Detectar iframe");
Check(!found.Any(x => x.Contains("127.0.0.1")), "Não seguir referências para rede local");
Check(MediaDiagnostics.Classify("ERROR: [generic] Got HTTP Error 403 caused by Cloudflare anti-bot challenge; try again with --extractor-args generic:impersonate").Code == "cloudflare", "Detectar Cloudflare e sugerir navegador.");
Check(MediaDiagnostics.Classify("ERROR: Sign in to confirm you're not a bot").Stop, "Desafio humano não deve fazer retry automático.");
Check(MediaDiagnostics.Classify("ERROR: HTTP Error 429: Too Many Requests").Code == "rate_limited", "429 não deve disparar dezenas de métodos.");
Check(MediaDiagnostics.Classify("DRM protected").Code == "drm", "Identificar DRM.");
Check(MediaDiagnostics.Classify("ERROR: Requested format is not available").Code == "format", "Detectar falha que justifica tentar outro formato.");
Check(MediaDiagnostics.Classify("Connection reset by peer").Code == "network", "Detectar falha transitória de rede.");
Check(MediaDiagnostics.Classify("HTTP Error 403: Forbidden").Code == "forbidden", "403 sem Cloudflare tem causa diferente.");
Check(MediaDiagnostics.NormalizeBrowser("ChRoMe") == "chrome", "Aceitar somente navegador autorizado.");
Check(MediaDiagnostics.NormalizeBrowser("--exec-malicious") == "none", "Bloquear injeção nas opções de navegador.");
var sessionSettings = new DownloadEngine.DownloadSettings("https://example.com/movie", @"C:\Temp\Media", false, "720", "original", "chrome");
var sessionArguments = DownloadEngine.BuildArguments(sessionSettings, DownloadEngine.Profiles(false)[0], @"C:\Tools", sessionSettings.Url);
Check(sessionArguments.Contains("--cookies-from-browser"), "A sessão escolhida deve ser enviada.");
Check(sessionArguments.Contains("chrome"), "Selecionar o navegador autorizado.");
Check(sessionArguments.Contains("after_move:FILE_DONE:%(filepath)s"), "Confirmar o arquivo no fim do download.");
var defaultSettings = sessionSettings with { Browser = "none" };
var defaultArguments = DownloadEngine.BuildArguments(defaultSettings, DownloadEngine.Profiles(false)[0], @"C:\Tools", defaultSettings.Url);
Check(!defaultArguments.Contains("--cookies-from-browser"), "Não acessar sessões sem permissão explícita.");
var capturedVideo = MediaCore.BrowserMediaClassifier.Examine("https://cdn.example.org/stream?token=abc", "video/mp4", "https://example.org/watch/123", 206);
Check(capturedVideo is not null && capturedVideo.Kind == "Vídeo", "A captura deve reconhecer streams sem extensão por tipo MIME.");
Check(MediaCore.BrowserMediaClassifier.Examine("https://cdn.example.org/segment.m4a", "audio/mp4", "https://example.org/watch", 200)?.Kind == "Áudio", "A captura deve reconhecer audio.");
Check(MediaCore.BrowserMediaClassifier.Examine("https://cdn.example.org/master.m3u8", "application/vnd.apple.mpegurl", "https://example.org/watch", 200)?.Kind == "HLS", "HLS deve ser detectado.");
Check(MediaCore.BrowserMediaClassifier.Examine("https://cdn.example.org/stream.mpd", "application/dash+xml", "https://example.org/watch", 200)?.Kind == "DASH", "DASH deve ser detectado.");
Check(MediaCore.BrowserMediaClassifier.Examine("https://cdn.example.org/image.jpg", "image/jpeg", "https://example.org/watch", 200) is null, "Imagens não são mídia de download.");
Check(MediaCore.BrowserMediaClassifier.Examine("https://cdn.example.org/movie.mp4", "video/mp4", "https://example.org/watch", 403) is null, "Respostas bloqueadas não devem aparecer como mídia acessível.");
Check(MediaCore.BrowserMediaClassifier.Examine("file:///C:/secret.mp4", "video/mp4", "", 200) is null, "Não usar arquivos locais como recursos remotos.");
Check(capturedVideo is not null && MediaCore.DirectMediaTransport.IsSupported(capturedVideo), "Transporte independente deve aceitar fonte selecionada.");
Check(MediaCore.DirectMediaTransport.GuessExtension(capturedVideo!) == ".mp4", "Formato deve ser identificado a partir do MIME.");

Console.WriteLine("Site Media Downloader: testes de perfis, páginas e segurança passaram.");
