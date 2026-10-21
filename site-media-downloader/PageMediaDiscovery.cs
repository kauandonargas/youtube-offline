using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace SiteMediaDownloader;

// Descobre fontes HTML5 em páginas públicas sem executar scripts externos,
// enviar cookies ou capturar sessões autenticadas.
public static class PageMediaDiscovery
{
    public static async Task<IReadOnlyList<string>> FindAsync(string page, CancellationToken token)
    {
        if (!Uri.TryCreate(page, UriKind.Absolute, out var uri) || !IsPublicHttp(uri))
            return Array.Empty<string>();
        try
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            client.Timeout = TimeSpan.FromSeconds(12);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SiteMediaDownloader/0.1");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) return Array.Empty<string>();
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
                return [page];
            if (type != "text/html" && type != "application/xhtml+xml") return Array.Empty<string>();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var memory = new MemoryStream();
            var buffer = new byte[32768];
            while (memory.Length < 2_000_000)
            {
                int n = await stream.ReadAsync(buffer, token);
                if (n == 0) break;
                memory.Write(buffer, 0, Math.Min(n, 2_000_000 - (int)memory.Length));
            }
            string html = Encoding.UTF8.GetString(memory.ToArray());
            return FindInHtml(uri, html);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            return Array.Empty<string>();
        }
    }

    public static IReadOnlyList<string> FindInHtml(Uri page, string html)
    {
        var found = new List<string>();

        void Add(string raw)
        {
            raw = WebUtility.HtmlDecode(raw).Replace("\\/", "/").Trim();
            if (!Uri.TryCreate(page, raw, out var uri) || !IsPublicHttp(uri)) return;
            string url = uri.AbsoluteUri;
            if (!found.Contains(url, StringComparer.OrdinalIgnoreCase)) found.Add(url);
        }

        // Fontes explícitas de <video> e <source>.
        foreach (Match match in Regex.Matches(html,
            @"<(?:video|source)\b[^>]*?\bsrc\s*=\s*[""'](?<url>[^""']+)[""']",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(400)))
            Add(match.Groups["url"].Value);

        // Metadados OpenGraph e Twitter.
        foreach (Match match in Regex.Matches(html, @"<meta\b[^>]*>",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(400)))
        {
            string tag = match.Value;
            if (!Regex.IsMatch(tag, @"(?:og:video|twitter:player:stream)",
                RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))) continue;
            var content = Regex.Match(tag, @"\bcontent\s*=\s*[""'](?<url>[^""']+)[""']",
                RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            if (content.Success) Add(content.Groups["url"].Value);
        }

        // Alguns sites expõem a URL no JSON-LD schema.org.
        foreach (Match match in Regex.Matches(html,
            @"""contentUrl""\s*:\s*""(?<url>https?[^""]+)""",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(400)))
            Add(match.Groups["url"].Value);

        // Iframes embedados podem ser processados pelo extrator dedicado.
        foreach (Match match in Regex.Matches(html,
            @"<iframe\b[^>]*?\bsrc\s*=\s*[""'](?<url>[^""']+)[""']",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(400)))
            Add(match.Groups["url"].Value);

        return found.Take(6).ToArray();
    }

    private static bool IsPublicHttp(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https")) return false;
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IPAddress.TryParse(uri.Host, out var ip)) return true;
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            return b[0] != 10 && b[0] != 127 && b[0] != 0 &&
                   !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
                   !(b[0] == 192 && b[1] == 168) &&
                   !(b[0] == 169 && b[1] == 254);
        }
        return !ip.IsIPv6LinkLocal && !ip.IsIPv6SiteLocal && !ip.IsIPv6Multicast &&
               !ip.IsIPv6UniqueLocal;
    }
}
