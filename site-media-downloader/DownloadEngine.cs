using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SiteMediaDownloader;

// Motor separado do YouTube Offline. Cada perfil reextrai o endereço da mídia
// com variações de protocolo e formato; não tenta decifrar DRM nem CAPTCHA.
public static class DownloadEngine
{
    public sealed record Profile(string Name, string Format, string[] Options);
    public sealed record DownloadSettings(string Url, string Folder, bool Audio, string Quality, string AudioFormat);

    private static readonly Profile[] VideoProfiles =
    [
        new("Extração automática", "best", []),
        new("Compatibilidade de páginas", "best", ["--extractor-args", "generic:impersonate", "--no-cache-dir"]),
        new("Extração sem cache", "best", ["--no-cache-dir"]),
        new("Conexão IPv4", "best", ["--force-ipv4", "--no-cache-dir"]),
        new("Vídeo e áudio separados", "separate", ["--no-cache-dir"]),
        new("Formato MP4", "mp4", ["--no-cache-dir"]),
        new("Vídeo progressivo", "progressive", ["--no-cache-dir"]),
        new("HLS nativo", "hls", ["--downloader", "m3u8:native", "--no-cache-dir"]),
        new("HLS FFmpeg", "hls", ["--downloader", "m3u8:ffmpeg", "--no-cache-dir"]),
        new("Extrator genérico", "best", ["--force-generic-extractor", "--no-cache-dir"]),
        new("Rede conservadora", "best", ["--concurrent-fragments", "1", "--force-ipv4", "--socket-timeout", "45", "--no-cache-dir"])
    ];
    private static readonly Profile[] AudioProfiles =
    [
        new("Áudio automático", "audio", []),
        new("Compatibilidade de páginas", "audio", ["--extractor-args", "generic:impersonate", "--no-cache-dir"]),
        new("Extração sem cache", "audio", ["--no-cache-dir"]),
        new("Conexão IPv4", "audio", ["--force-ipv4", "--no-cache-dir"]),
        new("Áudio M4A", "m4a", ["--no-cache-dir"]),
        new("Áudio WebM", "webm", ["--no-cache-dir"]),
        new("Qualquer faixa de áudio", "any-audio", ["--no-cache-dir"]),
        new("HLS nativo", "hls-audio", ["--downloader", "m3u8:native", "--no-cache-dir"]),
        new("Extrator genérico", "audio", ["--force-generic-extractor", "--no-cache-dir"]),
        new("Rede conservadora", "audio", ["--concurrent-fragments", "1", "--force-ipv4", "--socket-timeout", "45", "--no-cache-dir"])
    ];
    public static IReadOnlyList<Profile> Profiles(bool audio) => audio ? AudioProfiles : VideoProfiles;

    public static bool IsCloudflare(string text) =>
        text.Contains("Cloudflare anti-bot", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Cloudflare challenge", StringComparison.OrdinalIgnoreCase) ||
        (text.Contains("[generic]", StringComparison.OrdinalIgnoreCase) &&
         text.Contains("generic:impersonate", StringComparison.OrdinalIgnoreCase));

    public static bool IsAccessRestricted(string text) =>
        new[] { "drm protected", "protected by drm", "this video is private",
                "members only", "requires a subscription", "sign in to confirm",
                "not available in your country", "login required", "captcha",
                "access denied: login", "premium account required" }
        .Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

    public static bool IsRateLimited(string text) =>
        text.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase);

    public static bool IsMediaUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host);
    }

    public static string FormatSelector(bool audio, string quality, string kind)
    {
        if (audio)
            return kind switch
            {
                "m4a" => "ba[ext=m4a]/b[ext=m4a]",
                "webm" => "ba[ext=webm]/b[ext=webm]",
                "any-audio" => "bestaudio*/best",
                "hls-audio" => "ba[protocol*=m3u8]/b[protocol*=m3u8]",
                _ => "ba/b"
            };
        var cap = int.TryParse(quality, out int height) && height is > 0 and <= 4320
            ? "[height<=" + height + "]" : "";
        return kind switch
        {
            "separate" => "bv*" + cap + "+ba/b" + cap + "/best" + cap,
            "mp4" => "bv" + cap + "[ext=mp4]+ba[ext=m4a]/b" + cap + "[ext=mp4]/b" + cap,
            "progressive" => "b" + cap + "/best" + cap,
            "hls" => "b" + cap + "[protocol*=m3u8]/bv" + cap + "[protocol*=m3u8]+ba[protocol*=m3u8]",
            _ => "bv*" + cap + "+ba/b" + cap + "/best" + cap
        };
    }

    public static IReadOnlyList<string> BuildArguments(DownloadSettings settings, Profile profile,
        string ffmpegFolder, string pageUrl)
    {
        var output = Path.Combine(settings.Folder, "%(title)s [%(id)s].%(ext)s");
        var args = new List<string>
        {
            "--ignore-config", "--no-colors", "--newline", "--windows-filenames",
            "--no-playlist", "--continue", "--no-overwrites", "--no-mtime",
            "--retries", "3", "--fragment-retries", "3", "--extractor-retries", "2",
            "--socket-timeout", "25", "--concurrent-fragments", "3",
            "--print", "before_dl:TITLE:%(title)s",
            "--progress-template", "download:PROGRESS:%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
            "--ffmpeg-location", ffmpegFolder,
            "-o", output,
            "-f", FormatSelector(settings.Audio, settings.Quality, profile.Format)
        };
        if (settings.Audio)
        {
            if (settings.AudioFormat is "mp3" or "m4a" or "opus")
            {
                args.AddRange(["-x", "--audio-format", settings.AudioFormat]);
                if (settings.AudioFormat == "mp3") args.AddRange(["--audio-quality", "0"]);
            }
        }
        else
        {
            args.AddRange(["--merge-output-format", "mp4"]);
        }
        args.AddRange(profile.Options);
        args.Add(pageUrl);
        return args;
    }

    public static async Task DownloadAsync(
        DownloadSettings settings, Action<string, object> report, CancellationToken token)
    {
        if (!IsMediaUrl(settings.Url))
            throw new ArgumentException("Informe um link http/https válido.");
        if (string.IsNullOrWhiteSpace(settings.Folder))
            throw new ArgumentException("Escolha a pasta de destino.");

        Directory.CreateDirectory(settings.Folder);
        string ytdlp = Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe");
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        if (!File.Exists(ytdlp) || !File.Exists(ffmpeg))
            throw new InvalidOperationException("O instalador não contém yt-dlp/FFmpeg. Reinstale o programa.");

        var profiles = Profiles(settings.Audio);
        string lastError = "";
        bool cloudflareCompatTried = false;
        var urls = new List<string> { settings.Url };
        bool discoveryUsed = false;

        for (int source = 0; source < urls.Count; source++)
        {
            string mediaUrl = urls[source];
            for (int index = 0; index < profiles.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var profile = profiles[index];
                report("strategy", new
                {
                    name = profile.Name,
                    attempt = index + 1,
                    total = profiles.Count,
                    source = source == 0 ? "Página original" : "Mídia encontrada na página"
                });

                var args = BuildArguments(settings, profile, Path.GetDirectoryName(ffmpeg)!, mediaUrl);
                var result = await RunYtDlpAsync(ytdlp, settings.Folder, args, report, token);
                if (result.ExitCode == 0 && !result.HasErrors)
                {
                    report("completed", new { title = result.Title ?? "Download concluído", folder = settings.Folder });
                    return;
                }

                lastError = result.Error.Trim();
                if (lastError.Length > 1000) lastError = lastError[^1000..];
                bool cloudflare = IsCloudflare(result.Error);
                if (profile.Name == "Compatibilidade de páginas") cloudflareCompatTried = true;
                if (IsAccessRestricted(result.Error))
                    throw new InvalidOperationException(
                        "O site exige autenticação, CAPTCHA ou proteção de acesso. " +
                        "Não é possível resolver esse tipo de restrição automaticamente.");
                if (IsRateLimited(result.Error))
                    throw new InvalidOperationException(
                        "O site limitou temporariamente as solicitações (429). Aguarde antes de repetir o download.");
                if (cloudflare && cloudflareCompatTried)
                    throw new InvalidOperationException(
                        "O site bloqueou a extração com Cloudflare, inclusive após a tentativa de compatibilidade HTTP. " +
                        "É necessário usar um método disponibilizado pelo próprio site.");

                report("notice", new { text = "Método não funcionou; tentando alternativa. " +
                    (string.IsNullOrWhiteSpace(lastError) ? "" : lastError) });
                if (index + 1 < profiles.Count)
                    await Task.Delay(800, token);
            }

            // Se o extrator genérico falhou, procurar fontes HTML5 e metadados de
            // vídeo em uma página pública. A descoberta é limitada e não executa JS.
            if (!discoveryUsed)
            {
                discoveryUsed = true;
                report("notice", new { text = "Procurando uma fonte HTML5 incorporada na página..." });
                var found = await PageMediaDiscovery.FindAsync(settings.Url, token);
                foreach (var foundUrl in found.Take(3))
                {
                    if (!urls.Contains(foundUrl, StringComparer.OrdinalIgnoreCase))
                        urls.Add(foundUrl);
                }
            }
        }
        throw new InvalidOperationException(
            "Nenhuma estratégia conseguiu obter um arquivo neste endereço. " +
            "A página pode não expor um vídeo acessível ou usar uma proteção do site. " +
            (string.IsNullOrWhiteSpace(lastError) ? "" : "Último erro: " + lastError));
    }

    private sealed record ProcessResult(int ExitCode, string Error, bool HasErrors, string? Title);

    private static async Task<ProcessResult> RunYtDlpAsync(
        string executable, string folder, IReadOnlyList<string> arguments,
        Action<string, object> report, CancellationToken token)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = folder
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("Não foi possível iniciar o mecanismo de downloads.");

        var lines = new StringBuilder();
        string? title = null;
        bool hasErrors = false;

        async Task ReadAsync(StreamReader reader)
        {
            string? line;
            while ((line = await reader.ReadLineAsync(token)) is not null)
            {
                if (line.StartsWith("PROGRESS:", StringComparison.Ordinal))
                {
                    var parts = line["PROGRESS:".Length..].Split('|', 3);
                    if (parts.Length > 0)
                    {
                        string number = Regex.Match(parts[0], @"\d+(?:\.\d+)?").Value;
                        _ = double.TryParse(number, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var percent);
                        report("progress", new
                        {
                            percent = Math.Clamp(percent, 0, 100),
                            speed = parts.Length > 1 ? parts[1].Trim() : "",
                            eta = parts.Length > 2 ? parts[2].Trim() : ""
                        });
                    }
                }
                else if (line.StartsWith("TITLE:", StringComparison.Ordinal))
                {
                    title = line["TITLE:".Length..].Trim();
                    report("title", new { title });
                }
                else
                {
                    if (line.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
                        hasErrors = true;
                    lines.AppendLine(line);
                    if (lines.Length > 16_000) lines.Remove(0, lines.Length - 12_000);
                }
            }
        }

        try
        {
            var stdout = ReadAsync(process.StandardOutput);
            var stderr = ReadAsync(process.StandardError);
            await process.WaitForExitAsync(token);
            await Task.WhenAll(stdout, stderr);
            return new ProcessResult(process.ExitCode, lines.ToString(), hasErrors, title);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(); } catch { }
            throw;
        }
    }
}
