using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace MediaCore;

// Transporte independente do yt-dlp para fontes que o navegador recebeu e o
// usuário selecionou explicitamente. Não descobre tokens nem remove DRM.
public static class DirectMediaTransport
{
    public static bool IsSupported(BrowserMediaCandidate media)
    {
        if (!BrowserMediaClassifier.IsWebPage(media.Url)) return false;
        return media.Kind is "Vídeo" or "Áudio" or "HLS" or "DASH";
    }

    public static async Task<string> DownloadAsync(
        BrowserMediaCandidate media, string directory, string ffmpeg,
        Action<double?> onProgress, CancellationToken token)
    {
        if (!IsSupported(media)) throw new InvalidOperationException("Fonte de mídia inválida.");
        Directory.CreateDirectory(directory);
        string basename = "Midia-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") +
                          "-" + Guid.NewGuid().ToString("N")[..8];
        bool segmented = media.Kind is "HLS" or "DASH";
        string extension = segmented ? ".mkv" : GuessExtension(media);
        string file = Path.Combine(directory, basename + extension);
        string part = file + ".part";

        try
        {
            if (segmented)
                await DownloadWithFfmpegAsync(media, ffmpeg, part, token);
            else
                await DownloadHttpAsync(media, part, onProgress, token);

            token.ThrowIfCancellationRequested();
            if (!File.Exists(part) || new FileInfo(part).Length == 0)
                throw new IOException("O servidor não entregou um arquivo de mídia válido.");
            File.Move(part, file);
            onProgress(100);
            return file;
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { }
        }
    }

    public static string GuessExtension(BrowserMediaCandidate media)
    {
        string ext = Path.GetExtension(new Uri(media.Url).AbsolutePath).ToLowerInvariant();
        if (ext is ".mp4" or ".webm" or ".m4v" or ".mov" or ".mp3" or
            ".m4a" or ".aac" or ".ogg" or ".opus") return ext;
        if (media.ContentType.Contains("webm", StringComparison.OrdinalIgnoreCase))
            return ".webm";
        if (media.ContentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase) ||
            media.ContentType.Contains("mp3", StringComparison.OrdinalIgnoreCase))
            return ".mp3";
        return media.Kind == "Áudio" ? ".m4a" : ".mp4";
    }

    private static async Task DownloadHttpAsync(BrowserMediaCandidate media, string destination,
        Action<double?> onProgress, CancellationToken token)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.None };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36");
        if (BrowserMediaClassifier.IsWebPage(media.SourcePage))
            client.DefaultRequestHeaders.Referrer = new Uri(media.SourcePage);

        using var response = await client.GetAsync(media.Url,
            HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        string kind = response.Content.Headers.ContentType?.MediaType ?? "";
        if (kind.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
            kind.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O endereço retornou uma página, não um arquivo de mídia.");

        long? length = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var target = new FileStream(destination, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 131072, useAsync: true);
        byte[] buffer = new byte[131072];
        long total = 0;
        while (true)
        {
            int count = await source.ReadAsync(buffer, token);
            if (count == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, count), token);
            total += count;
            if (length > 0) onProgress(Math.Clamp(total * 100d / length.Value, 0, 99));
        }
        await target.FlushAsync(token);
        if (length.HasValue && total != length.Value)
            throw new IOException("Transferência incompleta; o tamanho recebido não corresponde ao anunciado.");
    }

    private static async Task DownloadWithFfmpegAsync(
        BrowserMediaCandidate media, string executable, string destination, CancellationToken token)
    {
        if (!File.Exists(executable))
            throw new InvalidOperationException("FFmpeg não está instalado no pacote.");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
        if (BrowserMediaClassifier.IsWebPage(media.SourcePage))
            args.AddRange(["-referer", media.SourcePage]);
        args.AddRange(["-i", media.Url, "-map", "0:v?", "-map", "0:a?", "-c", "copy", "-f", "matroska", destination]);

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Não foi possível iniciar o FFmpeg.");
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(token);
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            await stdout;
            string errors = await stderr;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("FFmpeg não conseguiu finalizar a transmissão: " +
                    (errors.Length > 500 ? errors[^500..] : errors));
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(); } catch { }
            throw;
        }
    }
}
