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
        var args = DownloadEngine.BuildArguments(settings, p, @"C:\Temp\Media", settings.Url);
        Check(args[^1] == settings.Url, "Página precisa ser repassada ao extrator");
        Check(args.Contains("--no-playlist"), "Download unitário é o padrão");
        Check(args.Contains("-f"), "Seletor de formato ausente");
        Check(DownloadEngine.FormatSelector(audio, "720", p.Format).Length > 0, "Seletor vazio");
        if (!audio) Check(DownloadEngine.FormatSelector(false, "720", p.Format).Contains("height<=720"), "Respeitar qualidade");
        if (audio) Check(args.Contains("--audio-format") && args.Contains("mp3"), "Conversão MP3");
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
Console.WriteLine("Site Media Downloader: testes de perfis, páginas e segurança passaram.");
