using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalTube;

public sealed class YtDlpService
{
    private readonly LocalTubeStore _store;
    private readonly MediaScanner _scanner;
    private readonly ToolManager _tools;
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCancels = new();
    private readonly ConcurrentQueue<QueueEntry> _queue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly object _jobGate = new();
    private readonly string _archivePath;

    private static readonly Regex ProgressRegex = new(@"download:\s*(?<pct>[0-9.]+)%\|(?<speed>[^|]*)\|(?<eta>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private sealed record QueueEntry(
        string BatchId,
        string ItemId,
        DownloadRequest Request,
        string ItemUrl,
        bool IsPlaylist,
        int PlaylistIndex,
        string PlaylistTitle);

    private sealed class SourceAnalysis
    {
        public bool IsPlaylist { get; init; }
        public string Title { get; init; } = "Vídeo";
        public string Channel { get; init; } = "";
        public string? Thumbnail { get; init; }
        public double? Duration { get; init; }
        public int[] Qualities { get; init; } = Array.Empty<int>();
        public List<SourceEntry> Entries { get; init; } = new();
        public string SourceId { get; init; } = "";
    }

    private sealed class SourceEntry
    {
        public string Id { get; init; } = "";
        public string Url { get; init; } = "";
        public string Title { get; init; } = "Vídeo";
        public double? Duration { get; init; }
        public int Index { get; init; }
    }

    public YtDlpService(LocalTubeStore store, MediaScanner scanner, ToolManager tools)
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

    public async Task<object> AnalyzeAsync(string url, string cookieBrowser = "auto")
    {
        var analysis = await AnalyzeSourceAsync(url, cookieBrowser);
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
        var analysis = await AnalyzeSourceAsync(req.Url, req.CookieBrowser);
        var isPlaylist = analysis.IsPlaylist || req.Playlist;

        var job = new DownloadJob
        {
            Url = req.Url,
            Title = analysis.Title,
            Status = "queued",
            IsPlaylist = isPlaylist
        };

        var entries = isPlaylist && analysis.Entries.Count > 0
            ? analysis.Entries
            : new List<SourceEntry>
            {
                new() { Id = analysis.SourceId, Url = req.Url, Title = analysis.Title, Duration = analysis.Duration, Index = 1 }
            };

        var knownIds = BuildKnownSourceIds();
        var queuedIds = BuildQueuedSourceIds();
        var archiveIds = ReadArchiveIds();

        var pendingEntries = new List<QueueEntry>();
        foreach (var source in entries)
        {
            var sourceKey = NormalizeSourceId(source.Id);
            var duplicate = !string.IsNullOrWhiteSpace(sourceKey) &&
                            (knownIds.Contains(sourceKey) || queuedIds.Contains(sourceKey) || archiveIds.Contains(sourceKey));

            var item = new DownloadItemJob
            {
                BatchId = job.Id,
                SourceId = source.Id,
                Title = source.Title,
                PlaylistIndex = isPlaylist ? source.Index : 0,
                PlaylistTotal = isPlaylist ? entries.Count : 1,
                Status = duplicate ? "skipped" : "queued",
                Duplicate = duplicate,
                Error = duplicate ? "Já está na biblioteca ou já foi baixado." : null
            };
            job.Items.Add(item);

            if (!duplicate)
            {
                if (!string.IsNullOrWhiteSpace(sourceKey)) queuedIds.Add(sourceKey);
                pendingEntries.Add(new QueueEntry(
                    job.Id,
                    item.Id,
                    CloneRequest(req),
                    string.IsNullOrWhiteSpace(source.Url) ? req.Url : source.Url,
                    isPlaylist,
                    source.Index,
                    analysis.Title));
            }
        }

        job.TotalItems = job.Items.Count;
        RefreshBatch(job);
        _jobs[job.Id] = job;

        // Publish the batch before waking the queue worker. Otherwise a very fast
        // worker could observe the queue entry before the parent job exists.
        foreach (var pending in pendingEntries)
        {
            _queue.Enqueue(pending);
            _queueSignal.Release();
        }

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
            item.Error = "Cancelando…";
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

            await RunItemAsync(batch!, item!, entry);

            // A short pause between playlist items reduces bursts of extractor requests.
            // The old 0.4.1 implementation reopened the whole playlist for every child item,
            // which could trigger YouTube's anti-bot challenge after many downloads.
            if (entry.IsPlaylist)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(1400)); } catch { }
            }
        }
    }

    private async Task RunItemAsync(DownloadJob batch, DownloadItemJob item, QueueEntry entry)
    {
        using var cts = new CancellationTokenSource();
        _activeCancels[item.Id] = cts;

        lock (_jobGate)
        {
            if (item.Status == "cancelling") cts.Cancel();
        }

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
                if (item.Status == "cancelling" || cts.IsCancellationRequested)
                    throw new OperationCanceledException(cts.Token);
                item.Status = "downloading";
                item.Error = null;
                RefreshBatch(batch);
            }

            var baseArgs = BuildArgs(
                entry.Request,
                ffmpeg,
                entry.ItemUrl,
                entry.IsPlaylist,
                entry.PlaylistIndex,
                entry.PlaylistTitle);

            var attempt = await RunDownloadAttemptAsync(tool, baseArgs, item, cts.Token);

            if (attempt.ExitCode != 0 && IsBotChallenge(attempt.Error) &&
                !string.Equals(entry.Request.CookieBrowser, "none", StringComparison.OrdinalIgnoreCase))
            {
                var last = attempt;
                var candidates = GetBrowserCandidates(entry.Request.CookieBrowser).ToArray();

                foreach (var browser in candidates)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    lock (_jobGate)
                    {
                        item.Error = $"O YouTube pediu confirmação. Tentando sua sessão local do {BrowserDisplayName(browser)}…";
                        RefreshBatch(batch);
                    }

                    var withCookies = WithBrowserCookies(baseArgs, browser);
                    last = await RunDownloadAttemptAsync(tool, withCookies, item, cts.Token);
                    if (last.ExitCode == 0)
                    {
                        attempt = last;
                        break;
                    }
                }

                if (attempt.ExitCode != 0)
                    attempt = last;
            }

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
            CleanupPartialFiles(item.SourceId);
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

        var stdoutTask = ConsumeAsync(process.StandardOutput, item, null, token);
        var stderr = new StringBuilder();
        var stderrTask = ConsumeAsync(process.StandardError, item, stderr, token);

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
        ValidateUrl(url);
        var tool = await _tools.EnsureYtDlpAsync();

        var flat = await RunCaptureWithAuthFallbackAsync(tool, new[]
        {
            "--dump-single-json", "--flat-playlist", "--no-warnings", "--no-colors", url
        }, TimeSpan.FromSeconds(180), cookieBrowser);
        if (flat.ExitCode != 0)
            throw new InvalidOperationException(CleanError(flat.Error));

        using var flatDoc = JsonDocument.Parse(flat.Output);
        var flatRoot = flatDoc.RootElement;
        if (IsPlaylist(flatRoot))
        {
            var entries = new List<SourceEntry>();
            if (flatRoot.TryGetProperty("entries", out var rawEntries) && rawEntries.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var entry in rawEntries.EnumerateArray())
                {
                    index++;
                    var sourceId = GetString(entry, "id") ?? GetString(entry, "url") ?? $"playlist-item-{index}";
                    entries.Add(new SourceEntry
                    {
                        Id = sourceId,
                        Url = ResolveEntryUrl(entry, sourceId),
                        Title = GetString(entry, "title") ?? $"Vídeo {index}",
                        Duration = GetDouble(entry, "duration"),
                        Index = index
                    });
                }
            }

            return new SourceAnalysis
            {
                IsPlaylist = true,
                Title = GetString(flatRoot, "title") ?? "Playlist",
                Channel = GetString(flatRoot, "uploader") ?? GetString(flatRoot, "channel") ?? "",
                Thumbnail = GetThumbnail(flatRoot),
                Qualities = new[] { 4320, 2160, 1440, 1080, 720, 480, 360 },
                Entries = entries,
                SourceId = GetString(flatRoot, "id") ?? ""
            };
        }

        var result = await RunCaptureWithAuthFallbackAsync(tool, new[]
        {
            "-J", "--no-playlist", "--no-warnings", "--no-colors", url
        }, TimeSpan.FromSeconds(180), cookieBrowser);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(CleanError(result.Error));

        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;
        return new SourceAnalysis
        {
            IsPlaylist = false,
            Title = GetString(root, "title") ?? "Vídeo",
            Channel = GetString(root, "uploader") ?? GetString(root, "channel") ?? "",
            Thumbnail = GetThumbnail(root),
            Duration = GetDouble(root, "duration"),
            Qualities = ExtractHeights(root),
            SourceId = GetString(root, "id") ?? NormalizeUrl(url)
        };
    }

    private List<string> BuildArgs(
        DownloadRequest req,
        string? ffmpeg,
        string itemUrl,
        bool isPlaylist,
        int playlistIndex,
        string playlistTitle)
    {
        var output = isPlaylist
            ? Path.Combine(
                _store.Settings.LibraryFolder,
                SanitizePathSegment(playlistTitle),
                $"{Math.Max(1, playlistIndex):000} - %(title)s [%(id)s].%(ext)s")
            : Path.Combine(_store.Settings.LibraryFolder, "%(title)s [%(id)s].%(ext)s");

        var args = new List<string>
        {
            "--no-colors", "--newline", "--windows-filenames",
            "--write-info-json", "--write-thumbnail",
            "--download-archive", _archivePath,
            "--no-overwrites",
            "--no-playlist",
            "--progress-template", "download:%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
            "-o", output
        };

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

        args.Add(itemUrl);
        return args;
    }

    private static async Task ConsumeAsync(StreamReader reader, DownloadItemJob item, StringBuilder? capture, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line is null) break;
                capture?.AppendLine(line);
                var trimmed = line.Trim();

                var match = ProgressRegex.Match(trimmed);
                if (match.Success)
                {
                    if (double.TryParse(match.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                        item.Progress = Math.Clamp(pct, 0, 100);
                    item.Speed = match.Groups["speed"].Value.Trim();
                    item.Eta = match.Groups["eta"].Value.Trim();
                }
                else if (trimmed.StartsWith("[download] Destination:", StringComparison.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(trimmed[(trimmed.IndexOf(':') + 1)..].Trim());
                    if (!string.IsNullOrWhiteSpace(name)) item.Title = Path.GetFileNameWithoutExtension(name);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private HashSet<string> BuildKnownSourceIds()
    {
        return _scanner.Items
            .Where(x => !string.IsNullOrWhiteSpace(x.OriginalId))
            .Select(x => NormalizeSourceId(x.OriginalId!))
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private HashSet<string> BuildQueuedSourceIds()
    {
        lock (_jobGate)
        {
            return _jobs.Values
                .SelectMany(x => x.Items)
                .Where(x => x.Status is "queued" or "preparing" or "downloading" or "cancelling" or "completed")
                .Where(x => !string.IsNullOrWhiteSpace(x.SourceId))
                .Select(x => NormalizeSourceId(x.SourceId))
                .Where(x => x.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    private HashSet<string> ReadArchiveIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_archivePath)) return set;
            foreach (var line in File.ReadLines(_archivePath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                var space = trimmed.LastIndexOf(' ');
                var id = space >= 0 ? trimmed[(space + 1)..] : trimmed;
                id = NormalizeSourceId(id);
                if (id.Length > 0) set.Add(id);
            }
        }
        catch { }
        return set;
    }

    private static string NormalizeSourceId(string value) => (value ?? "").Trim().ToLowerInvariant();

    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url.Trim();
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant() + uri.Query;
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
        else if (job.FailedItems > 0 && job.CompletedItems == 0 && job.CancelledItems + job.SkippedItems + job.FailedItems == job.TotalItems)
            job.Status = "failed";
        else if (job.CancelledItems == job.TotalItems)
            job.Status = "cancelled";
        else
            job.Status = "completed";
    }

    private void CleanupPartialFiles(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_store.Settings.LibraryFolder, $"*[{sourceId}]*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".part", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".ytdl", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".temp", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
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

    private static int[] ExtractHeights(JsonElement root)
    {
        var heights = new SortedSet<int>();
        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formats.EnumerateArray())
                if (f.TryGetProperty("height", out var h) && h.TryGetInt32(out var hi) && hi > 0)
                    heights.Add(hi);
        }
        return heights.Reverse().ToArray();
    }


    private static string ResolveEntryUrl(JsonElement entry, string sourceId)
    {
        foreach (var property in new[] { "webpage_url", "original_url", "url" })
        {
            var value = GetString(entry, property);
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return value!;
        }

        var extractor = GetString(entry, "ie_key") ?? GetString(entry, "extractor_key") ?? "";
        if (extractor.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
            (sourceId.Length == 11 && !sourceId.Contains('/')))
            return $"https://www.youtube.com/watch?v={Uri.EscapeDataString(sourceId)}";

        // Most non-YouTube flat playlists expose an absolute URL. If they do not,
        // keep the extractor value; yt-dlp may still understand it.
        return GetString(entry, "url") ?? sourceId;
    }

    private static string SanitizePathSegment(string value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "Playlist" : value.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0) name = "Playlist";
        return name.Length > 120 ? name[..120] : name;
    }

    private static bool IsBotChallenge(string error) =>
        error.Contains("Sign in to confirm you’re not a bot", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Sign in to confirm you're not a bot", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("cookies-from-browser", StringComparison.OrdinalIgnoreCase) &&
        error.Contains("authentication", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> GetBrowserCandidates(string? requested)
    {
        var mode = (requested ?? "auto").Trim().ToLowerInvariant();
        if (mode == "none") yield break;
        if (mode is "edge" or "chrome" or "firefox" or "brave")
        {
            yield return mode;
            yield break;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new (string Name, string Path)[]
        {
            ("edge", Path.Combine(local, "Microsoft", "Edge", "User Data")),
            ("chrome", Path.Combine(local, "Google", "Chrome", "User Data")),
            ("brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
            ("firefox", Path.Combine(roaming, "Mozilla", "Firefox", "Profiles"))
        };

        var any = false;
        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate.Path)) continue;
            any = true;
            yield return candidate.Name;
        }

        // Edge is part of current Windows installations. Keeping it as a last
        // candidate also covers non-standard profile locations.
        if (!any)
            yield return "edge";
    }

    private static string BrowserDisplayName(string browser) => browser.ToLowerInvariant() switch
    {
        "edge" => "Edge",
        "chrome" => "Chrome",
        "firefox" => "Firefox",
        "brave" => "Brave",
        _ => browser
    };

    private static List<string> WithBrowserCookies(IEnumerable<string> sourceArgs, string browser)
    {
        var args = sourceArgs.ToList();
        var insertAt = Math.Max(0, args.Count - 1); // keep the URL as the last argument
        args.Insert(insertAt, "--cookies-from-browser");
        args.Insert(insertAt + 1, browser);
        return args;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCaptureWithAuthFallbackAsync(
        string file,
        IEnumerable<string> sourceArgs,
        TimeSpan timeout,
        string? cookieBrowser)
    {
        var args = sourceArgs.ToArray();
        var first = await RunCaptureAsync(file, args, timeout);
        if (first.ExitCode == 0 || !IsBotChallenge(first.Error) ||
            string.Equals(cookieBrowser, "none", StringComparison.OrdinalIgnoreCase))
            return first;

        foreach (var browser in GetBrowserCandidates(cookieBrowser))
        {
            var retry = await RunCaptureAsync(file, WithBrowserCookies(args, browser), timeout);
            if (retry.ExitCode == 0) return retry;
        }

        return first;
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
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ArgumentException("Cole um link http/https válido.");
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

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar yt-dlp.");
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

    private static string CleanError(string error)
    {
        if (IsBotChallenge(error))
            return "O YouTube pediu uma confirmação anti-bot. O YouTube Offline tentou reduzir as requisições e, quando permitido, reutilizar sua sessão local do navegador. Se continuar, abra o YouTube no Edge/Chrome, confirme que a página funciona normalmente e tente de novo; ou aguarde alguns minutos. O aplicativo não pede nem armazena sua senha.";

        var lines = error.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var useful = lines.LastOrDefault(x => x.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
                  ?? lines.LastOrDefault(x => !x.Contains("WARNING:", StringComparison.OrdinalIgnoreCase))
                  ?? lines.LastOrDefault();
        return useful ?? "Falha ao processar o link.";
    }
}
