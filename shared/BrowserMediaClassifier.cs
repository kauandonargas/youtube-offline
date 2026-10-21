namespace MediaCore;

// Classificador independente da interface e do navegador.
// Só reconhece mídia que o navegador recebeu; não lê cookies ou chaves DRM.
public sealed record BrowserMediaCandidate(string Url, string Kind, string SourcePage, string ContentType);

public static class BrowserMediaClassifier
{
    public static BrowserMediaCandidate? Examine(string? resourceUrl, string? contentType, string? pageUrl, int status)
    {
        if (status is < 200 or >= 400) return null;
        if (!Uri.TryCreate(resourceUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https")) return null;

        string mediaType = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        string ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        string kind = mediaType switch
        {
            "application/vnd.apple.mpegurl" or "application/x-mpegurl" or
            "audio/mpegurl" or "audio/x-mpegurl" => "HLS",
            "application/dash+xml" => "DASH",
            _ when mediaType.StartsWith("video/") => "Vídeo",
            _ when mediaType.StartsWith("audio/") => "Áudio",
            _ => ""
        };

        if (kind.Length == 0)
        {
            kind = ext switch
            {
                ".m3u8" => "HLS",
                ".mpd" => "DASH",
                ".mp4" or ".webm" or ".m4v" or ".mov" => "Vídeo",
                ".mp3" or ".m4a" or ".aac" or ".ogg" or ".opus" => "Áudio",
                _ => ""
            };
        }

        if (kind.Length == 0) return null;
        // Não confundir imagens, publicidade de pagina ou licenças DRM com streams.
        if (mediaType.StartsWith("image/") || mediaType.Contains("widevine") ||
            mediaType.Contains("playready") || mediaType.Contains("drm"))
            return null;

        string page = Uri.TryCreate(pageUrl, UriKind.Absolute, out var origin) &&
            origin.Scheme is "http" or "https" ? origin.AbsoluteUri : "";
        return new BrowserMediaCandidate(uri.AbsoluteUri, kind, page, mediaType);
    }

    public static bool IsWebPage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host);
}
