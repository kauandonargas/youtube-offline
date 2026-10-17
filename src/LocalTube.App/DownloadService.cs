using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalTube;

// Downloader simplificado: cada acao do usuario ocupa exatamente um lugar na fila.
// Uma playlist inteira e processada por UMA unica execucao do yt-dlp, como no
// primeiro downloader do aplicativo. Isso evita reabrir a mesma playlist para
// cada video e reduz drasticamente as requisicoes repetidas ao YouTube.
public sealed class DownloadService
{
    private readonly LocalTubeStore _store;
    private readonly MediaScanner _scanner;
    private readonly ToolManager _tools;
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCancels = new();
    private readonly ConcurrentDictionary<string, SourceAnalysis> _analysisCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<QueueEntry> _queue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly object _jobGate = new();
    private readonly string _archivePath;

    private static readonly Regex ProgressRegex = new(@"download:\s*(?<pct>[0-9.]+)%\|(?<speed>[^|]*)\|(?<eta>[^|]*)\|(?<downloaded>[^|]*)\|(?<total>[^|]*)\|(?<estimated>[^|]*)\|(?<playlistIndex>[^|]*)\|(?<playlistTotal>[^|]*)\|(?<title>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PlaylistItemRegex = new(@"\[download\].*Downloading item\s+(?<index>\d+)\s+of\s+(?<total>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private sealed record QueueEntry(
        string BatchId,
        string ItemId,
        DownloadRequest Request,
        string Url,
        bool IsPlaylist,
        string PlaylistTitle,
        int PlaylistCount);

    private sealed class SourceAnalysis
    {
        public bool IsPlaylist { get; init; }
        public string Title { get; init; } = "Video";
        public string Channel { get; init; } = "";
        public string? Thumbnail { get; init; }
        public double? Duration { get; init; }
        public int[] Qualities { get; init; } = new[] { 4320, 2160, 1440, 1080, 720, 480, 360 };
        public List<SourceEntry> Entries { get; init; } = new();
        public string SourceId { get; init; } = "";
    }

    private sealed class SourceEntry
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "Video";
        public double? Duration { get; init; }
        public int Index { get; init; }
    }

    public DownloadService(LocalTubeStore store, MediaScanner scanner, ToolManager tools)
    {
        _store = store;
        _scanner = scanner;
        _tools = tools;
        _archivePath = Path.Combine(_store.DataDirectory, "download-archive.txt");
        _ = Task.Run(WorkerLoopAsync);
    }

    public IReadOnlyCollection<DownloadJob> Jobs
    {
        get
        {
            lock (_jobGate)
            {
                foreach (var job in _jobs.Values) RefreshBatch(job);
                return _jobs.Values.OrderByDescending(x => x.CreatedAt).ToArray();
            }
        }
    }

    public object GetToolStatus() => _tools.GetStatus();

    public async Task<object> AnalyzeAsync(string url, string cookieBrowser = "none")
    {
        ValidateUrl(url);
        PurgeTerminalJobs();

        var browser = NormalizeCookieMode(cookieBrowser);
        var analysis = await AnalyzeSourceAsync(url, browser);
        _analysisCache[NormalizeUrl(url)] = analysis;

        return new
        {
            isPlaylist = analysis.IsPlaylist,
            title = analysis.Title,
            channel = analysis.Channel,
            thumbnail = analysis.Thumbnail,
            duration = analysis.Duration,
            qualities = analysis.Qualities,
            itemCount = analysis.IsPlaylist ? analysis.Entries.Count : 1,
            entries = analysis.Entries.Take(12).Select(x => new
            {
                title = x.Title,
                id = x.Id,
                duration = x.Duration,
                index = x.Index
            }).ToArray()
        };
    }

    public async Task<DownloadJob> StartAsync(DownloadRequest req)
    {
        ValidateUrl(req.Url);
        var key = NormalizeUrl(req.Url);
        var browser = NormalizeCookieMode(req.CookieBrowser);

        // Se a tela ja analisou o link, reaproveita o resultado. Caso contrario,
        // inicia o download diretamente sem fazer uma segunda consulta ao YouTube.
        if (!_analysisCache.TryRemove(key, out var analysis))
            analysis = CreateDirectAnalysis(req.Url, req.Playlist);

        var isPlaylist = analysis.IsPlaylist || req.Playlist || IsLikelyPlaylistUrl(req.Url);

        if (isPlaylist)
        {
            lock (_jobGate)
            {
                var active = _jobs.Values.FirstOrDefault(x =>
                    x.IsPlaylist &&
                    NormalizeUrl(x.Url).Equals(key, StringComparison.OrdinalIgnoreCase) &&
                    x.Items.Any(i => i.Status is "queued" or "preparing" or "downloading" or "cancelling"));
                if (active is not null) return active;
            }
        }

        var job = new DownloadJob
        {
            Url = req.Url,
            Title = analysis.Title,
            Status = "queued",
            IsPlaylist = isPlaylist
        };

        var sourceId = string.IsNullOrWhiteSpace(analysis.SourceId) ? key : analysis.SourceId;
        var item = new DownloadItemJob
        {
            BatchId = job.Id,
            SourceId = isPlaylist ? $"playlist:{sourceId}" : sourceId,
            Title = analysis.Title,
            PlaylistIndex = 0,
            PlaylistTotal = isPlaylist ? analysis.Entries.Count : 1,
            Status = "queued"
        };

        if (!isPlaylist && IsKnownSingleVideo(sourceId))
        {
            item.Status = "skipped";
            item.Duplicate = true;
            item.Error = "Ja esta na biblioteca ou ja foi baixado.";
            job.Items.Add(item);
            RefreshBatch(job);
            _jobs[job.Id] = job;
            return job;
        }

        job.Items.Add(item);
        RefreshBatch(job);
        _jobs[job.Id] = job;

        var request = CloneRequest(req);
        request.CookieBrowser = browser;

        _queue.Enqueue(new QueueEntry(
            job.Id,
            item.Id,
            request,
            req.Url,
            isPlaylist,
            analysis.Title,
            Math.Max(analysis.Entries.Count, isPlaylist ? 1 : 0)));
        _queueSignal.Release();

        return job;
    }

    public bool CancelItem(string itemId)
    {
        lock (_jobGate)
        {
            var item = FindItem(itemId, out var batch);
            if (item is null || batch is null) return false;

            if (item.Status is "completed" or "cancelled" or "skipped" or "failed")
                return true;

            if (item.Status == "queued")
            {
                item.Status = "cancelled";
                item.Error = "Cancelado antes de iniciar.";
                item.Progress = 0;
                RefreshBatch(batch);
                return true;
            }

            item.Status = "cancelling";
            item.Error = "Cancelando...";
            if (_activeCancels.TryGetValue(item.Id, out var cts))
                cts.Cancel();
            RefreshBatch(batch);
            return true;
        }
    }

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            await _queueSignal.WaitAsync();
            if (!_queue.TryDequeue(out var entry)) continue;

            DownloadJob? batch;
            DownloadItemJob? item;
            lock (_jobGate)
            {
                if (!_jobs.TryGetValue(entry.BatchId, out batch)) continue;
                item = batch.Items.FirstOrDefault(x => x.Id == entry.ItemId);
                if (item is null || item.Status != "queued") continue;
                item.Status = "preparing";
                item.Error = null;
                RefreshBatch(batch);
            }

            await RunEntryAsync(batch!, item!, entry);
        }
    }

    private async Task RunEntryAsync(DownloadJob batch, DownloadItemJob item, QueueEntry entry)
    {
        using var cts = new CancellationTokenSource();
        _activeCancels[item.Id] = cts;

        try
        {
            cts.Token.ThrowIfCancellationRequested();
            var tool = await _tools.EnsureYtDlpAsync(cts.Token);

            string? ffmpeg = null;
            if (!string.Equals(entry.Request.Mode, "audio", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entry.Request.AudioFormat, "original", StringComparison.OrdinalIgnoreCase))
            {
                ffmpeg = (await _tools.EnsureFfmpegAsync(cts.Token)).Ffmpeg;
            }

            lock (_jobGate)
            {
                if (item.Status == "cancelling") throw new OperationCanceledException(cts.Token);
                item.Status = "downloading";
                item.Error = null;
                RefreshBatch(batch);
            }

            var args = BuildArgs(entry.Request, ffmpeg, entry.Url, entry.IsPlaylist, entry.PlaylistTitle);
            var attempt = await RunDownloadAttemptAsync(tool, args, item, entry.IsPlaylist, entry.PlaylistCount, cts.Token);

            // Uma unica tentativa anonima, como no fluxo antigo que funcionava.
            // Nao acessa bancos de cookies do Chrome/Edge automaticamente.

            if (attempt.ExitCode != 0)
                throw new InvalidOperationException(CleanError(attempt.Error));

            lock (_jobGate)
            {
                item.Progress = 100;
                item.Status = "completed";
                item.Speed = "";
                item.Eta = "";
                item.Error = null;
                RefreshBatch(batch);
            }

            _scanner.Scan();
        }
        catch (OperationCanceledException)
        {
            CleanupPartialFiles(entry.IsPlaylist, entry.PlaylistTitle, item.SourceId);
            lock (_jobGate)
            {
                item.Status = "cancelled";
                item.Progress = 0;
                item.Speed = "";
                item.Eta = "";
                item.Error = "Cancelado.";
                RefreshBatch(batch);
            }
        }
        catch (Exception ex)
        {
            lock (_jobGate)
            {
                item.Status = "failed";
                item.Speed = "";
                item.Eta = "";
                item.Error = ex.Message;
                RefreshBatch(batch);
            }
        }
        finally
        {
            _activeCancels.TryRemove(item.Id, out _);
        }
    }

    private async Task<(int ExitCode, string Error)> RunDownloadAttemptAsync(
        string tool,
        IReadOnlyCollection<string> args,
        DownloadItemJob item,
        bool isPlaylist,
        int playlistCount,
        CancellationToken token)
    {
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

        var stdoutTask = ConsumeAsync(process.StandardOutput, item, null, isPlaylist, playlistCount, token);
        var stderr = new StringBuilder();
        var stderrTask = ConsumeAsync(process.StandardError, item, stderr, isPlaylist, playlistCount, token);

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try { await process.WaitForExitAsync(); } catch { }
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        return (process.ExitCode, stderr.ToString());
    }

    private async Task<SourceAnalysis> AnalyzeSourceAsync(string url, string cookieBrowser)
    {
        var tool = await _tools.EnsureYtDlpAsync();
        var args = new[]
        {
            "--dump-single-json",
            "--flat-playlist",
            "--no-warnings",
            "--no-colors",
            url
        };

        var result = await RunCaptureAsync(tool, args, TimeSpan.FromSeconds(180));
        if (result.ExitCode != 0 && IsBotChallenge(result.Error) && IsExplicitBrowser(cookieBrowser))
            result = await RunCaptureAsync(tool, WithBrowserCookies(args, cookieBrowser), TimeSpan.FromSeconds(180));

        if (result.ExitCode != 0)
            throw new InvalidOperationException(CleanError(result.Error));

        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;
        var isPlaylist = IsPlaylist(root);
        var entries = new List<SourceEntry>();

        if (isPlaylist && root.TryGetProperty("entries", out var rawEntries) && rawEntries.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var raw in rawEntries.EnumerateArray())
            {
                index++;
                entries.Add(new SourceEntry
                {
                    Id = GetString(raw, "id") ?? GetString(raw, "url") ?? $"playlist-item-{index}",
                    Title = GetString(raw, "title") ?? $"Video {index}",
                    Duration = GetDouble(raw, "duration"),
                    Index = index
                });
            }
        }

        return new SourceAnalysis
        {
            IsPlaylist = isPlaylist,
            Title = GetString(root, "title") ?? (isPlaylist ? "Playlist" : "Video"),
            Channel = GetString(root, "uploader") ?? GetString(root, "channel") ?? "",
            Thumbnail = GetThumbnail(root),
            Duration = GetDouble(root, "duration"),
            Entries = entries,
            SourceId = GetString(root, "id") ?? NormalizeUrl(url)
        };
    }

    private static SourceAnalysis CreateDirectAnalysis(string url, bool requestedPlaylist)
    {
        var isPlaylist = requestedPlaylist || IsLikelyPlaylistUrl(url);
        var sourceId = isPlaylist
            ? TryGetQueryValue(url, "list") ?? NormalizeUrl(url)
            : TryExtractYoutubeVideoId(url) ?? NormalizeUrl(url);

        return new SourceAnalysis
        {
            IsPlaylist = isPlaylist,
            Title = isPlaylist ? "Playlist" : "Video",
            SourceId = sourceId,
            Entries = new List<SourceEntry>()
        };
    }

    private static bool IsLikelyPlaylistUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.AbsolutePath.Contains("/playlist", StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrWhiteSpace(TryGetQueryValue(url, "list"));
    }

    private static string? TryExtractYoutubeVideoId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        var fromQuery = TryGetQueryValue(url, "v");
        if (!string.IsNullOrWhiteSpace(fromQuery)) return fromQuery;

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("shorts", StringComparison.OrdinalIgnoreCase)) return parts[1];
        if (parts.Length >= 2 && parts[0].Equals("live", StringComparison.OrdinalIgnoreCase)) return parts[1];
        if (parts.Length >= 2 && parts[0].Equals("embed", StringComparison.OrdinalIgnoreCase)) return parts[1];
        return null;
    }

    private static string? TryGetQueryValue(string url, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 0 || !pair[0].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (pair.Length == 1) return "";
            return Uri.UnescapeDataString(pair[1].Replace('+', ' '));
        }
        return null;
    }

    private List<string> BuildArgs(DownloadRequest req, string? ffmpeg, string url, bool isPlaylist, string playlistTitle)
    {
        var output = isPlaylist
            ? Path.Combine(
                _store.Settings.LibraryFolder,
                SanitizePathSegment(playlistTitle),
                "%(playlist_index)03d - %(title)s [%(id)s].%(ext)s")
            : Path.Combine(_store.Settings.LibraryFolder, "%(title)s [%(id)s].%(ext)s");

        var args = new List<string>
        {
            "--no-colors",
            "--newline",
            "--windows-filenames",
            "--write-info-json",
            "--write-thumbnail",
            "--download-archive", _archivePath,
            "--no-overwrites",
            "--progress-template", "download:%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(info.playlist_index)s|%(info.playlist_count)s|%(info.title)s",
            "-o", output
        };

        if (isPlaylist)
        {
            // Mesmo modelo da primeira versao: uma unica execucao cuida da playlist toda.
            args.Add("--yes-playlist");
            args.Add("--ignore-errors");
            args.Add("--continue");
        }
        else
        {
            args.Add("--no-playlist");
        }

        if (!string.IsNullOrWhiteSpace(ffmpeg))
        {
            args.Add("--ffmpeg-location");
            args.Add(Path.GetDirectoryName(ffmpeg)!);
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

        args.Add(url);
        return args;
    }

    private static async Task ConsumeAsync(
        StreamReader reader,
        DownloadItemJob item,
        StringBuilder? capture,
        bool isPlaylist,
        int playlistCount,
        CancellationToken token)
    {
        var currentIndex = 1;
        var total = Math.Max(1, playlistCount);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line is null) break;
                capture?.AppendLine(line);
                var trimmed = line.Trim();

                if (isPlaylist)
                {
                    var itemMatch = PlaylistItemRegex.Match(trimmed);
                    if (itemMatch.Success)
                    {
                        if (int.TryParse(itemMatch.Groups["index"].Value, out var parsedIndex))
                            currentIndex = Math.Max(1, parsedIndex);
                        if (int.TryParse(itemMatch.Groups["total"].Value, out var parsedTotal) && parsedTotal > 0)
                            total = parsedTotal;
                    }
                }

                var progress = ProgressRegex.Match(trimmed);
                if (!progress.Success) continue;

                if (double.TryParse(progress.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                {
                    pct = Math.Clamp(pct, 0, 100);
                    item.Progress = isPlaylist && total > 1
                        ? Math.Clamp(((currentIndex - 1) + (pct / 100d)) / total * 100d, 0, 100)
                        : pct;
                }

                item.Speed = progress.Groups["speed"].Value.Trim();
                item.Eta = progress.Groups["eta"].Value.Trim();
                item.DownloadedBytes = ParseProgressBytes(progress.Groups["downloaded"].Value);

                var exactTotal = ParseProgressBytes(progress.Groups["total"].Value);
                var estimatedTotal = ParseProgressBytes(progress.Groups["estimated"].Value);
                if (exactTotal > 0)
                {
                    item.TotalBytes = exactTotal;
                    item.TotalBytesEstimated = false;
                }
                else if (estimatedTotal > 0)
                {
                    item.TotalBytes = estimatedTotal;
                    item.TotalBytesEstimated = true;
                }

                if (isPlaylist)
                {
                    if (int.TryParse(progress.Groups["playlistIndex"].Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pi) && pi > 0)
                        item.PlaylistIndex = pi;
                    if (int.TryParse(progress.Groups["playlistTotal"].Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pt) && pt > 0)
                        item.PlaylistTotal = pt;
                }

                var currentTitle = progress.Groups["title"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(currentTitle) && !currentTitle.Equals("NA", StringComparison.OrdinalIgnoreCase))
                    item.Title = currentTitle;
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private static long ParseProgressBytes(string value)
    {
        var text = (value ?? "").Trim();
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes > 0
            ? bytes
            : 0;
    }

    private bool IsKnownSingleVideo(string sourceId)
    {
        var key = NormalizeSourceId(sourceId);
        if (string.IsNullOrWhiteSpace(key)) return false;

        if (_scanner.Items.Any(x => !string.IsNullOrWhiteSpace(x.OriginalId) && NormalizeSourceId(x.OriginalId!).Equals(key, StringComparison.OrdinalIgnoreCase)))
            return true;

        lock (_jobGate)
        {
            if (_jobs.Values.SelectMany(x => x.Items).Any(x =>
                x.Status is "queued" or "preparing" or "downloading" or "cancelling" or "completed" &&
                NormalizeSourceId(x.SourceId).Equals(key, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        try
        {
            if (!File.Exists(_archivePath)) return false;
            foreach (var line in File.ReadLines(_archivePath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                var space = trimmed.LastIndexOf(' ');
                var id = space >= 0 ? trimmed[(space + 1)..] : trimmed;
                if (NormalizeSourceId(id).Equals(key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }

        return false;
    }

    private void PurgeTerminalJobs()
    {
        lock (_jobGate)
        {
            foreach (var pair in _jobs.ToArray())
            {
                if (pair.Value.Items.Count > 0 && pair.Value.Items.All(x => x.Status is "completed" or "cancelled" or "skipped" or "failed"))
                    _jobs.TryRemove(pair.Key, out _);
            }
        }
    }

    private DownloadItemJob? FindItem(string itemId, out DownloadJob? batch)
    {
        foreach (var candidate in _jobs.Values)
        {
            var item = candidate.Items.FirstOrDefault(x => x.Id == itemId);
            if (item is null) continue;
            batch = candidate;
            return item;
        }
        batch = null;
        return null;
    }

    private static void RefreshBatch(DownloadJob job)
    {
        job.TotalItems = job.Items.Count;
        job.CompletedItems = job.Items.Count(x => x.Status == "completed");
        job.FailedItems = job.Items.Count(x => x.Status == "failed");
        job.CancelledItems = job.Items.Count(x => x.Status == "cancelled");
        job.SkippedItems = job.Items.Count(x => x.Status == "skipped");
        job.Progress = job.Items.Count == 0 ? 0 : job.Items.Average(x =>
            x.Status is "completed" or "cancelled" or "failed" or "skipped" ? 100 : x.Progress);

        if (job.Items.Any(x => x.Status is "downloading" or "preparing" or "cancelling"))
            job.Status = "downloading";
        else if (job.Items.Any(x => x.Status == "queued"))
            job.Status = "queued";
        else if (job.FailedItems > 0 && job.CompletedItems == 0)
            job.Status = "failed";
        else if (job.CancelledItems == job.TotalItems)
            job.Status = "cancelled";
        else
            job.Status = "completed";
    }

    private void CleanupPartialFiles(bool isPlaylist, string playlistTitle, string sourceId)
    {
        try
        {
            IEnumerable<string> files;
            if (isPlaylist)
            {
                var folder = Path.Combine(_store.Settings.LibraryFolder, SanitizePathSegment(playlistTitle));
                files = Directory.Exists(folder)
                    ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    : Array.Empty<string>();
            }
            else
            {
                var cleanId = sourceId.Replace("playlist:", "", StringComparison.OrdinalIgnoreCase);
                files = Directory.EnumerateFiles(_store.Settings.LibraryFolder, $"*[{cleanId}]*", SearchOption.AllDirectories);
            }

            foreach (var file in files)
            {
                if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".temp", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }

    private static DownloadRequest CloneRequest(DownloadRequest req) => new()
    {
        Url = req.Url,
        Mode = req.Mode,
        Quality = req.Quality,
        AudioFormat = req.AudioFormat,
        Playlist = req.Playlist,
        CookieBrowser = req.CookieBrowser
    };

    private static bool IsPlaylist(JsonElement root)
    {
        if (root.TryGetProperty("_type", out var type) && type.ValueKind == JsonValueKind.String &&
            string.Equals(type.GetString(), "playlist", StringComparison.OrdinalIgnoreCase))
            return true;
        return root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array;
    }

    private static string NormalizeCookieMode(string? requested)
    {
        // 0.5.10: volta ao comportamento anonimo simples. Nenhuma sessao do navegador
        // e lida automaticamente pelo aplicativo.
        return "none";
    }

    private static bool IsExplicitBrowser(string? mode) =>
        (mode ?? "").Trim().ToLowerInvariant() is "edge" or "chrome" or "firefox" or "brave";

    private static List<string> WithBrowserCookies(IEnumerable<string> sourceArgs, string browser)
    {
        var args = sourceArgs.ToList();
        var insertAt = Math.Max(0, args.Count - 1);
        args.Insert(insertAt, "--cookies-from-browser");
        args.Insert(insertAt + 1, browser);
        return args;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCaptureAsync(
        string file,
        IEnumerable<string> args,
        TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Nao foi possivel iniciar yt-dlp.");
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            var outTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            return (process.ExitCode, await outTask, await errTask);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new TimeoutException("O YouTube demorou demais para responder. Tente novamente.");
        }
    }

    private static bool IsBotChallenge(string error) =>
        error.Contains("Sign in to confirm you’re not a bot", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Sign in to confirm you're not a bot", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("confirm you are not a bot", StringComparison.OrdinalIgnoreCase);

    private static string CleanError(string error)
    {
        if (IsBotChallenge(error))
            return "O YouTube recusou esta requisicao. O aplicativo nao acessa mais cookies do navegador automaticamente. Tente novamente mais tarde ou, se quiser, escolha manualmente um navegador na tela de Downloads.";

        var lines = error.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var useful = lines.LastOrDefault(x => x.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
                  ?? lines.LastOrDefault(x => !x.Contains("WARNING:", StringComparison.OrdinalIgnoreCase))
                  ?? lines.LastOrDefault();
        return useful ?? "Falha ao processar o link.";
    }

    private static string SanitizePathSegment(string value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "Playlist" : value.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0) name = "Playlist";
        return name.Length > 120 ? name[..120] : name;
    }

    private static string NormalizeSourceId(string value) => (value ?? "").Trim().ToLowerInvariant();

    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url.Trim().ToLowerInvariant();
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant() + uri.Query;
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static double? GetDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.TryGetDouble(out var value) ? value : null;

    private static string? GetThumbnail(JsonElement root)
    {
        var direct = GetString(root, "thumbnail");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        if (root.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
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
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Cole um link http/https valido.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }
}
