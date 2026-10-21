using System.Text.RegularExpressions;

namespace LocalTube;

// Estratégias independentes e limitadas para recuperar falhas transitórias de rede,
// extração ou formato. Não usa cookies, tokens, proxies nem contorna DRM/acesso restrito.
public sealed record DownloadProfile(string Key, string Label, string FormatKind, string[] ExtraArgs);

public static class DownloadRecovery
{
    private static readonly DownloadProfile[] VideoProfiles =
    [
        new("padrao", "Qualidade solicitada", "standard", []),
        new("sem-cache", "Extrair metadados novamente", "standard", ["--no-cache-dir"]),
        new("conexao-estavel", "Rede conservadora", "standard", ["--concurrent-fragments", "1", "--retries", "15", "--fragment-retries", "20", "--socket-timeout", "40"]),
        new("ipv4", "Conexão IPv4", "standard", ["--force-ipv4", "--no-cache-dir"]),
        new("formatos-flexiveis", "Formatos de vídeo alternativos", "flexible", ["--no-cache-dir"]),
        new("mp4-progressivo", "Arquivo progressivo", "progressive", ["--no-cache-dir"]),
        new("hls", "Transmissão HLS", "hls", ["--no-cache-dir", "--downloader", "m3u8:native"]),
        new("web-player", "Extrator web alternativo", "flexible", ["--no-cache-dir", "--extractor-args", "youtube:player_client=web"]),
        new("hls-ffmpeg", "HLS pelo FFmpeg", "hls", ["--no-cache-dir", "--downloader", "m3u8:ffmpeg"]),
        new("ultima-tentativa", "Conexão segura com formatos flexíveis", "flexible", ["--force-ipv4", "--no-cache-dir", "--concurrent-fragments", "1", "--retries", "15"])
    ];

    private static readonly DownloadProfile[] AudioProfiles =
    [
        new("padrao", "Áudio original", "audio-default", []),
        new("sem-cache", "Extrair áudio novamente", "audio-default", ["--no-cache-dir"]),
        new("conexao-estavel", "Rede conservadora", "audio-default", ["--concurrent-fragments", "1", "--retries", "15", "--fragment-retries", "20", "--socket-timeout", "40"]),
        new("ipv4", "Conexão IPv4", "audio-default", ["--force-ipv4", "--no-cache-dir"]),
        new("audio-m4a", "Faixa de áudio M4A", "audio-m4a", ["--no-cache-dir"]),
        new("audio-webm", "Faixa de áudio WebM", "audio-webm", ["--no-cache-dir"]),
        new("audio-flexivel", "Áudio de qualquer formato", "audio-flexible", ["--no-cache-dir"]),
        new("audio-hls", "Áudio HLS", "audio-hls", ["--no-cache-dir", "--downloader", "m3u8:native"]),
        new("web-player", "Extrator web alternativo", "audio-flexible", ["--no-cache-dir", "--extractor-args", "youtube:player_client=web"]),
        new("ultima-tentativa", "Áudio com rede conservadora", "audio-flexible", ["--force-ipv4", "--no-cache-dir", "--concurrent-fragments", "1", "--retries", "15"])
    ];

    public static IReadOnlyList<DownloadProfile> Profiles(bool audio) => audio ? AudioProfiles : VideoProfiles;

    // O extrator genérico recomenda este modo quando Cloudflare bloqueia a
    // negociação HTTP. A tentativa é única; não automatiza CAPTCHAs nem sessões.
    public static DownloadProfile CloudflareProfile(bool audio) =>
        new("generic-impersonate", "Compatibilidade HTTP do extrator genérico",
            audio ? "audio-default" : "standard",
            ["--no-cache-dir", "--extractor-args", "generic:impersonate"]);

    public static bool IsCloudflareChallenge(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        (error.Contains("Cloudflare anti-bot challenge", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("Cloudflare challenge", StringComparison.OrdinalIgnoreCase) ||
         (error.Contains("[generic]", StringComparison.OrdinalIgnoreCase) &&
          error.Contains("generic:impersonate", StringComparison.OrdinalIgnoreCase)));

    public static string CloudflareHelp(string? error)
    {
        var reason = string.IsNullOrWhiteSpace(error) ? "" : " Detalhe: " + error.Trim();
        return "O site exige uma verificação do Cloudflare. O programa tentou " +
               "o modo de compatibilidade indicado pelo yt-dlp, mas não obteve acesso. " +
               "Abra o endereço no navegador e utilize uma opção de download autorizada " +
               "pelo site. CAPTCHAs ou exigências de acesso não podem ser resolvidos automaticamente." + reason;
    }

    public static string FormatSelector(bool audio, string? quality, string formatKind)
    {
        if (audio)
            return formatKind switch
            {
                "audio-m4a" => "ba[ext=m4a]/ba/b",
                "audio-webm" => "ba[ext=webm]/ba/b",
                "audio-flexible" => "bestaudio*/best",
                "audio-hls" => "ba[protocol*=m3u8]/b[protocol*=m3u8]/ba/b",
                _ => "ba/b"
            };

        string cap = int.TryParse(quality, out var h) && h is > 0 and <= 4320
            ? $"[height<={h}]"
            : "";
        return formatKind switch
        {
            "flexible" => $"bv*{cap}+ba/b{cap}/best{cap}",
            "progressive" => $"b{cap}[ext=mp4]/b{cap}/best{cap}",
            "hls" => $"b{cap}[protocol*=m3u8]/bv{cap}[protocol*=m3u8]+ba[protocol*=m3u8]/b{cap}",
            _ => $"bv{cap}[ext=mp4]+ba[ext=m4a]/bv{cap}+ba/b{cap}[ext=mp4]/b{cap}"
        };
    }

    public static bool IsRestricted(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        return new[]
        {
            "private video", "this video is private", "members-only",
            "members only", "sign in to confirm", "confirm you're not a bot",
            "sign in to verify", "login required", "requires payment",
            "not available in your country", "not available in your region",
            "drm protected", "unsupported url", "video unavailable",
            "this video is unavailable", "age-restricted"
        }.Any(marker => error.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsRateLimited(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        (error.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("429: Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase));

    public static int CountErrors(string? error) =>
        string.IsNullOrWhiteSpace(error)
            ? 0
            : Regex.Matches(error, @"(?m)^\s*ERROR:", RegexOptions.IgnoreCase).Count;
}
