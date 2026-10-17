using System.IO.Compression;
using System.Net.Http.Headers;

namespace LocalTube;

public sealed class ToolManager
{
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string FfmpegZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    private readonly string _userToolsDir;
    private readonly SemaphoreSlim _ytDlpGate = new(1, 1);
    private readonly SemaphoreSlim _ffmpegGate = new(1, 1);

    public ToolManager(LocalTubeStore store)
    {
        _userToolsDir = Path.Combine(store.DataDirectory, "tools");
        Directory.CreateDirectory(_userToolsDir);
    }

    public object GetStatus()
    {
        var ytdlp = Locate("yt-dlp.exe", "yt-dlp");
        var ffmpeg = Locate("ffmpeg.exe", "ffmpeg");
        var ffprobe = Locate("ffprobe.exe", "ffprobe");
        return new
        {
            ytDlp = new { available = ytdlp is not null, path = ytdlp },
            ffmpeg = new { available = ffmpeg is not null, path = ffmpeg },
            ffprobe = new { available = ffprobe is not null, path = ffprobe }
        };
    }

    public string? LocateYtDlp() => Locate("yt-dlp.exe", "yt-dlp");
    public string? LocateFfmpeg() => Locate("ffmpeg.exe", "ffmpeg");
    public string? LocateFfprobe() => Locate("ffprobe.exe", "ffprobe");

    public async Task<string> EnsureYtDlpAsync(CancellationToken cancellationToken = default)
    {
        var existing = LocateYtDlp();
        if (existing is not null) return existing;

        await _ytDlpGate.WaitAsync(cancellationToken);
        try
        {
            existing = LocateYtDlp();
            if (existing is not null) return existing;

            var target = Path.Combine(_userToolsDir, "yt-dlp.exe");
            var temp = target + ".download";
            await DownloadFileAsync(YtDlpUrl, temp, TimeSpan.FromMinutes(4), cancellationToken);
            File.Move(temp, target, true);
            return target;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Não foi possível preparar o yt-dlp automaticamente. Verifique sua internet e tente novamente. " + ex.Message, ex);
        }
        finally
        {
            _ytDlpGate.Release();
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
