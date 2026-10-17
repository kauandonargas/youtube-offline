using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalTube;

// Downloader restaurado a partir do fluxo usado no YouTube Offline v0.4.0.
// A regra principal e a mesma daquela versao: uma playlist inteira e entregue
// ao yt-dlp em uma unica execucao, em vez de abrir um processo por video.
public sealed class YtDlpService
{
    private readonly LocalTubeStore _store;
    private readonly MediaScanner _scanner;
    private readonly ToolManager _tools;
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCancels = new();
    private readonly ConcurrentDictionary<string, Process> _activeProcesses = new();

    private static readonly Regex ProgressRegex = new(@"download:\s*(?<pct>[0-9.]+)%\|(?<speed>[^|]*)\|(?<eta>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PlaylistItemRegex = new(@"Downloading item\s+(?<current>\d+)\s+of\s+(?<total>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public YtDlpService(LocalTubeStore store, MediaScanner scanner, ToolManager tools)
    {
        _store = store;
        _scanner = scanner;
        _tools = tools;
    }

    public IReadOnlyCollection<DownloadJob> Jobs => _jobs.Values.OrderByDescending(x => x.CreatedAt).ToArray();
    public object GetToolStatus() => _tools.GetStatus();

    // cookieBrowser existe apenas para manter compatibilidade com a interface atual.
    // O fluxo restaurado da v0.4.0 nao injeta cookies nem faz retentativas por navegador.
    public async Task<object> AnalyzeAsync(string url, string cookieBrowser = "none")
    {
        ValidateUrl(url);
        var tool = await _tools.EnsureYtDlpAsync();

        // Fluxo original da v0.4.0: primeiro uma leitura flat para descobrir playlist.
        var flat = await RunCaptureAsync(tool, new[]
        {
            "--dump-single-json", "--flat-playlist", "--no-warnings", "--no-colors", url
        }, TimeSpan.FromSeconds(120));
        if (flat.ExitCode != 0)
            throw new InvalidOperationException(CleanError(flat.Error));

        using var flatDoc = JsonDocument.Parse(flat.Output);
        var flatRoot = flatDoc.RootElement;
        if (IsPlaylist(flatRoot))
            return BuildPlaylistAnalysis(flatRoot);

        // Video individual: inspecao completa para listar qualidades reais.
        var result = await RunCaptureAsync(tool, new[]
        {
            "-J", "--no-playlist", "--no-warnings", "--no-colors", url
        }, TimeSpan.FromSeconds(120));
        if (result.ExitCode != 0)
            throw new InvalidOperationException(CleanError(result.Error));

        using var doc = JsonDocument.Parse(result.Output);
        var r = doc.RootElement;
        var heights = ExtractHeights(r);
        return new
        {
            isPlaylist = false,
            title = GetString(r, "title") ?? "Video",
            channel = GetString(r, "uploader") ?? GetString(r, "channel") ?? "",
            thumbnail = GetThumbnail(r),
            duration = GetDouble(r, "duration"),
            qualities = heights,
            itemCount = 1,
            entries = Array.Empty<object>()
        };
    }

    // Mantem a assinatura async usada pela interface atual, mas inicia exatamente
    // o modelo de uma execucao por acao que existia na v0.4.0.
    public Task<DownloadJob> StartAsync(DownloadRequest req)
    {
        return Task.FromResult(Start(req));
    }

    private DownloadJob Start(DownloadRequest req)
    {
        ValidateUrl(req.Url);

        var job = new DownloadJob
        {
            Url = req.Url,
            Status = "queued",
            Title = req.Playlist ? "Playlist" : req.Url,
            IsPlaylist = req.Playlist,
            TotalItems = 1
        };

        // A interface atual espera itens filhos. Para nao mudar o motor restaurado,
        // existe apenas um item visual representando a execucao inteira do yt-dlp.
        var item = new DownloadItemJob
        {
            BatchId = job.Id,
            SourceId = req.Url,
            Title = req.Playlist ? "Playlist" : req.Url,
            PlaylistIndex = 0,
            PlaylistTotal = 1,
            Status = "queued"
        };
        job.Items.Add(item);

        _jobs[job.Id] = job;
        _ = Task.Run(() => RunDownloadAsync(job, item, req));
        return job;
    }

    public bool CancelItem(string itemId)
    {
        var pair = _jobs.Values
            .SelectMany(job => job.Items.Select(item => (job, item)))
            .FirstOrDefault(x => x.item.Id == itemId);

        if (pair.item is null) return false;
        if (pair.item.Status is "completed" or "cancelled" or "failed") return true;

        pair.item.Status = "cancelling";
        pair.item.Error = "Cancelando...";
        pair.job.Status = "cancelling";

        if (_activeCancels.TryGetValue(itemId, out var cts))
            cts.Cancel();

        if (_activeProcesses.TryGetValue(itemId, out var process))
            TryKill(process);

        return true;
    }

    private async Task RunDownloadAsync(DownloadJob job, DownloadItemJob item, DownloadRequest req)
    {
        using var cts = new CancellationTokenSource();
        _activeCancels[item.Id] = cts;

        try
        {
            item.Status = "preparing";
            job.Status = "preparing";
            var tool = await _tools.EnsureYtDlpAsync(cts.Token);

            // Igual a v0.4.0: merge de video e conversoes de audio usam FFmpeg.
            string? ffmpeg = null;
            if (!string.Equals(req.Mode, "audio", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(req.AudioFormat, "original", StringComparison.OrdinalIgnoreCase))
            {
                ffmpeg = (await _tools.EnsureFfmpegAsync(cts.Token)).Ffmpeg;
            }

            cts.Token.ThrowIfCancellationRequested();
            item.Status = "downloading";
            item.Error = null;
            job.Status = "downloading";

            var args = BuildArgs(req, ffmpeg);
            var psi = new ProcessStartInfo(tool)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _store.Settings.LibraryFolder
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();
            _activeProcesses[item.Id] = process;

            var stdoutTask = ConsumeAsync(process.StandardOutput, job, item);
            var stderr = new StringBuilder();
            var stderrTask = ConsumeAsync(process.StandardError, job, item, stderr);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                try { await process.WaitForExitAsync(); } catch { }
                throw;
            }

            await Task.WhenAll(stdoutTask, stderrTask);

            if (process.ExitCode != 0)
                throw new InvalidOperationException(CleanError(stderr.ToString()));

            item.Progress = 100;
            item.Status = "completed";
            item.Speed = "";
            item.Eta = "";
            item.Error = null;

            job.Progress = 100;
            job.Status = "completed";
            job.CompletedItems = Math.Max(1, job.TotalItems);
            job.FailedItems = 0;
            job.CancelledItems = 0;
            job.SkippedItems = 0;

            _scanner.Scan();
        }
        catch (OperationCanceledException)
        {
            item.Status = "cancelled";
            item.Progress = 0;
            item.Speed = "";
            item.Eta = "";
            item.Error = "Cancelado.";

            job.Status = "cancelled";
            job.Progress = 0;
            job.CompletedItems = 0;
            job.CancelledItems = 1;
        }
        catch (Exception ex)
        {
            item.Status = "failed";
            item.Speed = "";
            item.Eta = "";
            item.Error = ex.Message;

            job.Status = "failed";
            job.FailedItems = 1;
        }
        finally
        {
            _activeProcesses.TryRemove(item.Id, out _);
            _activeCancels.TryRemove(item.Id, out _);
        }
    }

    // Esta funcao replica a construcao de argumentos da v0.4.0.
    private List<string> BuildArgs(DownloadRequest req, string? ffmpeg)
    {
        var output = req.Playlist
            ? Path.Combine(_store.Settings.LibraryFolder, "%(playlist_title)s", "%(playlist_index)03d - %(title)s [%(id)s].%(ext)s")
            : Path.Combine(_store.Settings.LibraryFolder, "%(title)s [%(id)s].%(ext)s");

        var args = new List<string>
        {
            "--no-colors", "--newline", "--windows-filenames",
            "--write-info-json", "--write-thumbnail",
            "--progress-template", "download:%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
            "-o", output
        };

        if (!string.IsNullOrWhiteSpace(ffmpeg))
        {
            args.Add("--ffmpeg-location");
            args.Add(Path.GetDirectoryName(ffmpeg)!);
        }

        if (req.Playlist)
        {
            args.Add("--yes-playlist");
            // Um video privado/removido nao deve abortar a playlist inteira.
            args.Add("--ignore-errors");
            args.Add("--continue");
        }
        else
        {
            args.Add("--no-playlist");
        }

        if (string.Equals(req.Mode, "audio", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-f");
            args.Add("ba/b");
            var fmt = (req.AudioFormat ?? "original").ToLowerInvariant();
            if (fmt != "original")
            {
                args.Add("-x");
                args.Add("--audio-format");
                args.Add(fmt is "mp3" or "m4a" or "opus" ? fmt : "mp3");
                if (fmt == "mp3")
                {
                    args.Add("--audio-quality");
                    args.Add("0");
                }
            }
        }
        else
        {
            var selector = "bv[ext=mp4]+ba[ext=m4a]/bv+ba/b[ext=mp4]/b";
            if (int.TryParse(req.Quality, out var h) && h > 0)
                selector = $"bv[height<={h}][ext=mp4]+ba[ext=m4a]/bv[height<={h}]+ba/b[height<={h}][ext=mp4]/b[height<={h}]";
            args.Add("-f");
            args.Add(selector);
            args.Add("--merge-output-format");
            args.Add("mp4");
        }

        // Uma unica URL original. Para playlist, o proprio yt-dlp percorre todos os itens.
        args.Add(req.Url);
        return args;
    }

    private static async Task ConsumeAsync(StreamReader reader, DownloadJob job, DownloadItemJob item, StringBuilder? capture = null)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            capture?.AppendLine(line);
            var trimmed = line.Trim();

            var playlistMatch = PlaylistItemRegex.Match(trimmed);
            if (playlistMatch.Success &&
                int.TryParse(playlistMatch.Groups["current"].Value, out var current) &&
                int.TryParse(playlistMatch.Groups["total"].Value, out var total))
            {
                item.PlaylistIndex = current;
                item.PlaylistTotal = total;
                job.TotalItems = total;
                continue;
            }

            var match = ProgressRegex.Match(trimmed);
            if (match.Success)
            {
                if (double.TryParse(match.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                {
                    item.Progress = Math.Clamp(pct, 0, 100);
                    if (item.PlaylistTotal > 0 && item.PlaylistIndex > 0)
                        job.Progress = Math.Clamp(((item.PlaylistIndex - 1) + pct / 100d) / item.PlaylistTotal * 100d, 0, 100);
                    else
                        job.Progress = item.Progress;
                }
                item.Speed = match.Groups["speed"].Value.Trim();
                item.Eta = match.Groups["eta"].Value.Trim();
            }
            else if (trimmed.StartsWith("[download] Destination:", StringComparison.OrdinalIgnoreCase))
            {
                var title = Path.GetFileName(trimmed[(trimmed.IndexOf(':') + 1)..].Trim());
                if (!string.IsNullOrWhiteSpace(title))
                    item.Title = title;
            }
        }
    }

    private static bool IsPlaylist(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (root.TryGetProperty("_type", out var type) && type.ValueKind == JsonValueKind.String &&
            string.Equals(type.GetString(), "playlist", StringComparison.OrdinalIgnoreCase))
            return true;
        return root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array;
    }

    private static object BuildPlaylistAnalysis(JsonElement root)
    {
        var previews = new List<object>();
        var itemCount = 0;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                itemCount++;
                if (previews.Count >= 8) continue;
                previews.Add(new
                {
                    title = GetString(entry, "title") ?? "Video",
                    id = GetString(entry, "id") ?? "",
                    duration = GetDouble(entry, "duration"),
                    index = itemCount
                });
            }
        }

        var thumbnail = GetThumbnail(root);
        return new
        {
            isPlaylist = true,
            title = GetString(root, "title") ?? "Playlist",
            channel = GetString(root, "uploader") ?? GetString(root, "channel") ?? "",
            thumbnail,
            duration = (double?)null,
            qualities = new[] { 4320, 2160, 1440, 1080, 720, 480, 360 },
            itemCount,
            entries = previews
        };
    }

    private static int[] ExtractHeights(JsonElement root)
    {
        var heights = new SortedSet<int>();
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formats.EnumerateArray())
            {
                if (f.ValueKind == JsonValueKind.Object &&
                    f.TryGetProperty("height", out var h) &&
                    h.ValueKind == JsonValueKind.Number &&
                    h.TryGetInt32(out var hi) && hi > 0)
                {
                    heights.Add(hi);
                }
            }
        }
        return heights.Reverse().ToArray();
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        return root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
    }

    private static double? GetDouble(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out var p) ||
            p.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return p.TryGetDouble(out var value) ? value : null;
    }

    private static string? GetThumbnail(JsonElement root)
    {
        var direct = GetString(root, "thumbnail");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in thumbs.EnumerateArray().Reverse())
            {
                var url = GetString(t, "url");
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }
        }
        return null;
    }

    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ArgumentException("Cole um link http/https valido.");
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCaptureAsync(string file, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Nao foi possivel iniciar yt-dlp.");
        using var cts = new CancellationTokenSource(timeout);
        var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
        var errTask = p.StandardError.ReadToEndAsync(cts.Token);
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, await outTask, await errTask);
    }

    private static string CleanError(string error)
    {
        var lines = error.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var useful = lines.LastOrDefault(x => x.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
                  ?? lines.LastOrDefault(x => !x.Contains("WARNING:", StringComparison.OrdinalIgnoreCase))
                  ?? lines.LastOrDefault();
        return useful ?? "Falha ao processar o link.";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }
}
