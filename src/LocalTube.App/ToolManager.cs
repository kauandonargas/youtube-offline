using System.IO.Compression;
using System.Net.Http.Headers;

namespace LocalTube;

public sealed class ToolManager
{
    // O canal nightly é o recomendado pelo próprio yt-dlp para uso normal e recebe
    // correções de compatibilidade com sites muito antes do canal stable.
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/latest/download/yt-dlp.exe";
    private const string DenoZipUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
    private const string FfmpegZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private static readonly TimeSpan YtDlpRefreshInterval = TimeSpan.FromHours(12);

    private readonly string _userToolsDir;
    private readonly string _ytDlpRefreshStamp;
    private readonly SemaphoreSlim _ytDlpGate = new(1, 1);
    private readonly SemaphoreSlim _denoGate = new(1, 1);
    private readonly SemaphoreSlim _ffmpegGate = new(1, 1);

    public ToolManager(LocalTubeStore store)
    {
        _userToolsDir = Path.Combine(store.DataDirectory, "tools");
        _ytDlpRefreshStamp = Path.Combine(_userToolsDir, ".yt-dlp-refresh");
        Directory.CreateDirectory(_userToolsDir);
        CleanupOldYtDlpRefreshFiles();
    }

    public object GetStatus()
    {
        var ytdlp = LocateYtDlp();
        var deno = LocateDeno();
        var ffmpeg = Locate("ffmpeg.exe", "ffmpeg");
        var ffprobe = Locate("ffprobe.exe", "ffprobe");
        return new
        {
            ytDlp = new { available = ytdlp is not null, path = ytdlp },
            deno = new { available = deno is not null, path = deno },
            ffmpeg = new { available = ffmpeg is not null, path = ffmpeg },
            ffprobe = new { available = ffprobe is not null, path = ffprobe }
        };
    }

    // A cópia gravável tem prioridade sobre a cópia empacotada. Assim o motor
    // pode ser atualizado entre releases do aplicativo.
    public string? LocateYtDlp()
    {
        try
        {
            var userCopy = Path.Combine(_userToolsDir, "yt-dlp.exe");
            if (File.Exists(userCopy)) return userCopy;
        }
        catch { }

        return Locate("yt-dlp.exe", "yt-dlp");
    }

    public string? LocateDeno() => Locate("deno.exe", "deno");
    public string? LocateFfmpeg() => Locate("ffmpeg.exe", "ffmpeg");
    public string? LocateFfprobe() => Locate("ffprobe.exe", "ffprobe");

    public async Task<string> EnsureYtDlpAsync(CancellationToken cancellationToken = default)
    {
        var existing = LocateYtDlp();
        if (existing is not null && !ShouldRefreshYtDlp())
            return existing;

        return await RefreshYtDlpCoreAsync(existing, force: false, cancellationToken);
    }

    // Um 403 pode ser causado por URLs de mídia expiradas em conjunto com uma
    // versão do extrator que já ficou para trás. A recuperação força uma cópia
    // atual antes de extrair novamente a URL do vídeo.
    public async Task<string> RefreshYtDlpAsync(CancellationToken cancellationToken = default)
    {
        return await RefreshYtDlpCoreAsync(LocateYtDlp(), force: true, cancellationToken);
    }

    private async Task<string> RefreshYtDlpCoreAsync(string? fallback, bool force, CancellationToken cancellationToken)
    {
        await _ytDlpGate.WaitAsync(cancellationToken);
        try
        {
            fallback = LocateYtDlp() ?? fallback;
            if (!force && fallback is not null && !ShouldRefreshYtDlp())
                return fallback;

            var target = Path.Combine(_userToolsDir, "yt-dlp.exe");
            var temp = Path.Combine(_userToolsDir, $"yt-dlp-refresh-{Guid.NewGuid():N}.exe");

            try
            {
                await DownloadFileAsync(YtDlpUrl, temp, TimeSpan.FromMinutes(4), cancellationToken);
                if (!File.Exists(temp) || new FileInfo(temp).Length < 1_000_000)
                    throw new InvalidOperationException("O arquivo recebido para o yt-dlp é inválido ou incompleto.");

                try
                {
                    File.Move(temp, target, true);
                    WriteYtDlpRefreshStamp();
                    return target;
                }
                catch (Exception ex) when (fallback is not null && ex is IOException or UnauthorizedAccessException)
                {
                    // Se outra tarefa estiver usando o executável atual, o Windows
                    // pode impedir a substituição. A cópia nova ainda serve para
                    // esta tentativa e será limpa em uma execução futura.
                    WriteYtDlpRefreshStamp();
                    return temp;
                }
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(temp);
                throw;
            }
            catch (Exception ex)
            {
                TryDeleteFile(temp);
                WriteYtDlpRefreshStamp();
                if (fallback is not null)
                    return fallback;

                throw new InvalidOperationException(
                    "Não foi possível preparar o yt-dlp automaticamente. Verifique sua internet e tente novamente. " + ex.Message, ex);
            }
        }
        finally
        {
            _ytDlpGate.Release();
        }
    }

    private bool ShouldRefreshYtDlp()
    {
        try
        {
            if (!File.Exists(_ytDlpRefreshStamp)) return true;
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(_ytDlpRefreshStamp) >= YtDlpRefreshInterval;
        }
        catch
        {
            return true;
        }
    }

    private void WriteYtDlpRefreshStamp()
    {
        try
        {
            File.WriteAllText(_ytDlpRefreshStamp, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch { }
    }

    private void CleanupOldYtDlpRefreshFiles()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_userToolsDir, "yt-dlp-refresh-*.exe"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(1))
                        File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    public async Task<string> EnsureDenoAsync(CancellationToken cancellationToken = default)
    {
        var existing = LocateDeno();
        if (existing is not null) return existing;

        await _denoGate.WaitAsync(cancellationToken);
        try
        {
            existing = LocateDeno();
            if (existing is not null) return existing;

            var zip = Path.Combine(_userToolsDir, "deno.zip.download");
            var extract = Path.Combine(_userToolsDir, "deno-extract-" + Guid.NewGuid().ToString("N"));
            try
            {
                await DownloadFileAsync(DenoZipUrl, zip, TimeSpan.FromMinutes(6), cancellationToken);
                Directory.CreateDirectory(extract);
                ZipFile.ExtractToDirectory(zip, extract, true);

                var deno = Directory.EnumerateFiles(extract, "deno.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (deno is null)
                    throw new InvalidOperationException("O pacote baixado não continha deno.exe.");

                var target = Path.Combine(_userToolsDir, "deno.exe");
                File.Copy(deno, target, true);
                return target;
            }
            finally
            {
                TryDeleteFile(zip);
                TryDeleteDirectory(extract);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Não foi possível preparar o runtime JavaScript necessário para o YouTube. " + ex.Message, ex);
        }
        finally
        {
            _denoGate.Release();
        }
    }

    public async Task<(string Ffmpeg, string? Ffprobe)> EnsureFfmpegAsync(CancellationToken cancellationToken = default)
    {
        var existing = LocateFfmpeg();
        if (existing is not null) return (existing, LocateFfprobe());

        await _ffmpegGate.WaitAsync(cancellationToken);
        try
        {
            existing = LocateFfmpeg();
            if (existing is not null) return (existing, LocateFfprobe());

            var zip = Path.Combine(_userToolsDir, "ffmpeg-essentials.zip.download");
            var extract = Path.Combine(_userToolsDir, "ffmpeg-extract-" + Guid.NewGuid().ToString("N"));
            try
            {
                await DownloadFileAsync(FfmpegZipUrl, zip, TimeSpan.FromMinutes(12), cancellationToken);
                Directory.CreateDirectory(extract);
                ZipFile.ExtractToDirectory(zip, extract, true);

                var ffmpeg = Directory.EnumerateFiles(extract, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                var ffprobe = Directory.EnumerateFiles(extract, "ffprobe.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (ffmpeg is null)
                    throw new InvalidOperationException("O pacote baixado não continha ffmpeg.exe.");

                var ffmpegTarget = Path.Combine(_userToolsDir, "ffmpeg.exe");
                File.Copy(ffmpeg, ffmpegTarget, true);
                if (ffprobe is not null)
                    File.Copy(ffprobe, Path.Combine(_userToolsDir, "ffprobe.exe"), true);

                return (ffmpegTarget, LocateFfprobe());
            }
            finally
            {
                TryDeleteFile(zip);
                TryDeleteDirectory(extract);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Não foi possível preparar o FFmpeg automaticamente. Verifique sua internet e tente novamente. " + ex.Message, ex);
        }
        finally
        {
            _ffmpegGate.Release();
        }
    }

    public async Task PrepareAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureYtDlpAsync(cancellationToken);
        await EnsureDenoAsync(cancellationToken);
        await EnsureFfmpegAsync(cancellationToken);
    }

    public string? Locate(string localName, string pathName)
    {
        foreach (var baseDir in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools"),
            _userToolsDir
        })
        {
            try
            {
                var candidate = Path.Combine(baseDir, localName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var baseDir = dir.Trim('"');
                var candidate = Path.Combine(baseDir, OperatingSystem.IsWindows() ? localName : pathName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private static async Task DownloadFileAsync(string url, string destination, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YouTubeOffline", UpdateService.CurrentVersion));

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
        await input.CopyToAsync(output, timeoutCts.Token);
        await output.FlushAsync(timeoutCts.Token);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
