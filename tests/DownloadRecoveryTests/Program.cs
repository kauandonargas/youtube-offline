using MediaCore;
using LocalTube;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

foreach (bool audio in new[] { false, true })
{
    var profiles = DownloadRecovery.Profiles(audio);
    Check(profiles.Count >= 9, "É necessário ter várias estratégias reais.");
    Check(profiles[0].Key == "padrao", "A primeira tentativa precisa respeitar o formato original.");
    Check(profiles.Select(p => p.Key).Distinct().Count() == profiles.Count, "Chaves repetidas.");
    Check(profiles.Select(p => p.FormatKind + string.Join(",", p.ExtraArgs)).Distinct().Count() == profiles.Count, "Estratégias idênticas.");
    Check(profiles.All(p => !p.ExtraArgs.Any(x => x.Contains("cookies", StringComparison.OrdinalIgnoreCase))), "Importação não solicitada de cookies.");
    Check(profiles.All(p => !p.ExtraArgs.Any(x => x.Contains("proxy", StringComparison.OrdinalIgnoreCase))), "Proxy não solicitado.");

    foreach (var profile in profiles)
    {
        var selector = DownloadRecovery.FormatSelector(audio, "720", profile.FormatKind);
        Check(!string.IsNullOrWhiteSpace(selector), "Formato vazio: " + profile.Key);
        if (!audio)
            Check(selector.Contains("height<=720"), "Qualidade escolhida não foi respeitada em: " + profile.Key);
    }
}

Check(DownloadRecovery.IsRestricted("ERROR: This video is private"), "Vídeo privado precisa parar.");
Check(DownloadRecovery.IsRestricted("Sign in to confirm you're not a bot"), "Autenticação deve parar.");
Check(DownloadRecovery.IsRestricted("DRM protected video"), "DRM deve parar.");
Check(!DownloadRecovery.IsRestricted("HTTP Error 403: Forbidden"), "403 pode ser transiente.");
Check(DownloadRecovery.IsRateLimited("HTTP Error 429: Too Many Requests"), "429 deve parar.");
Check(!DownloadRecovery.IsRateLimited("HTTP Error 503"), "503 não é um bloqueio de taxa.");
Check(DownloadRecovery.CountErrors("WARNING: something\nERROR: first\nERROR: second\n") == 2, "Contagem de falhas de playlist.");
Check(DownloadRecovery.CountErrors("WARNING: something") == 0, "Avisos não podem virar falhas.");
Check(MediaDiagnostics.Classify("ERROR: [generic] Got HTTP Error 403 caused by Cloudflare anti-bot challenge; try again with --extractor-args generic:impersonate").Code == "cloudflare", "Detectar Cloudflare e sugerir navegador.");
Check(MediaDiagnostics.Classify("ERROR: Sign in to confirm you're not a bot").Stop, "Desafio humano não deve fazer retry automático.");
Check(MediaDiagnostics.Classify("ERROR: HTTP Error 429: Too Many Requests").Code == "rate_limited", "429 não deve disparar dezenas de métodos.");
Check(MediaDiagnostics.Classify("DRM protected").Code == "drm", "Identificar DRM.");
Check(MediaDiagnostics.Classify("ERROR: Requested format is not available").Code == "format", "Detectar falha que justifica tentar outro formato.");
Check(MediaDiagnostics.Classify("Connection reset by peer").Code == "network", "Detectar falha transitória de rede.");
Check(MediaDiagnostics.Classify("HTTP Error 403: Forbidden").Code == "forbidden", "403 sem Cloudflare tem causa diferente.");
Check(MediaDiagnostics.NormalizeBrowser("ChRoMe") == "chrome", "Aceitar somente navegador autorizado.");
Check(MediaDiagnostics.NormalizeBrowser("--exec-malicious") == "none", "Bloquear injeção nas opções de navegador.");
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

Console.WriteLine("Download recovery: cenários determinísticos aprovados.");
