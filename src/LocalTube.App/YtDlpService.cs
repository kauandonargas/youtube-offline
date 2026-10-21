using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaCore;

namespace LocalTube;

// Orquestra o yt-dlp mantendo uma única execução por ação do usuário.
// Playlists são percorridas pelo próprio yt-dlp, o que reduz extrações repetidas
// e mantém o estado da sessão dentro do mesmo processo.
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

    // Importação de sessão somente quando o usuário escolhe explicitamente um navegador.
    public async Task<object> AnalyzeAsync(string url, string cookieBrowser = "none")
    {
        ValidateUrl(url);
        var tool = await _tools.EnsureYtDlpAsync();
        var deno = await _tools.EnsureDenoAsync();

        var flat = await RunCaptureAsync(tool, new[]
        {
            "--ignore-config",
            "--js-runtimes", $"deno:{deno}",
            "--dump-single-json", "--flat-playlist", "--no-warnings", "--no-colors"
        }.Concat(BrowserArgs(cookieBrowser)).Append(url), TimeSpan.FromSeconds(120));
        if (flat.ExitCode != 0)
            throw new InvalidOperationException(ExplainError(flat.Error));

        using var flatDoc = JsonDocument.Parse(flat.Output);
        var flatRoot = flatDoc.RootElement;
        if (IsPlaylist(flatRoot))
            return BuildPlaylistAnalysis(flatRoot);

        var result = await RunCaptureAsync(tool, new[]
        {
            "--ignore-config",
            "--js-runtimes", $"deno:{deno}",
            "-J", "--no-playlist", "--no-warnings", "--no-colors"
        }.Concat(BrowserArgs(cookieBrowser)).Append(url), TimeSpan.FromSeconds(120));
        if (result.ExitCode != 0)
            throw new InvalidOperationException(ExplainError(result.Error));

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

    private static IEnumerable<string> BrowserArgs(string? browser)
    {
        string selected = MediaDiagnostics.NormalizeBrowser(browser);
        return selected == "none" ? Array.Empty<string>() : ["--cookies-from-browser", selected];
    }

    private static string ExplainError(string error)
    {
        var diagnosis = MediaDiagnostics.Classify(error);
        return diagnosis.Code == "unknown" ? CleanError(error)
            : diagnosis.Message + " Detalhe técnico: " + CleanError(error);
    }

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
            var deno = await _tools.EnsureDenoAsync(cts.Token);

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

            // Cada tentativa reextrai o URL; playlists reutilizam um arquivo de
            // concluídos, sem rebaixar arquivos já finalizados na mesma sessão.
            IReadOnlyList<DownloadProfile> profiles = req.CapturedMedia
                ? [
                    new("media-direta", "Fonte selecionada no navegador", "captured", []),
                    new("media-direta-ipv4", "Fonte selecionada: conexão IPv4", "captured", ["--force-ipv4"])
                  ]
                : DownloadRecovery.Profiles(string.Equals(req.Mode, "audio", StringComparison.OrdinalIgnoreCase));
            var archiveDir = Path.Combine(_store.DataDirectory, "download-archives");
            Directory.CreateDirectory(archiveDir);
            var archivePath = req.Playlist ? Path.Combine(archiveDir, job.Id + ".txt") : null;
            item.AttemptTotal = profiles.Count;
            bool succeeded = false;
            string lastError = "";
            int archived = 0;

            for (var attempt = 0; attempt < profiles.Count; attempt++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var profile = profiles[attempt];
                item.Attempt = attempt + 1;
                item.Strategy = profile.Label;
                item.Progress = 0;
                item.Speed = "";
                item.Eta = "";
                item.Error = attempt == 0 ? null : $"Tentativa {attempt + 1}/{profiles.Count}: {profile.Label}";
                item.Status = "downloading";
                job.Status = "downloading";

                var args = BuildArgs(req, ffmpeg, deno, profile, archivePath);
                var result = await RunDownloadProcessAsync(tool, args, job, item, cts.Token);
                archived = CountArchiveEntries(archivePath);
                var reportedErrors = DownloadRecovery.CountErrors(result.Error);
                var diagnosis = MediaDiagnostics.Classify(result.Error);
                item.FailureCode = diagnosis.Code;
                bool missingFromPlaylist = req.Playlist && job.TotalItems > 0 && archived < job.TotalItems;
                lastError = CleanError(result.Error);

                if (result.ExitCode == 0 && reportedErrors == 0 && !missingFromPlaylist &&
                    (result.ConfirmedFiles > 0 || (req.Playlist && archived > 0)))
                {
                    item.FailureCode = "";
                    succeeded = true;
                    break;
                }

                // Decisões de acesso, CAPTCHA, DRM, 429 e indisponibilidade
                // são compartilhadas com o aplicativo de outros sites.
                if (diagnosis.Stop)
                {
                    if (req.Playlist && archived > 0) break;
                    throw new InvalidOperationException(diagnosis.Message);
                }
                if (diagnosis.Code == "cloudflare" && attempt > 0)
                {
                    if (req.Playlist && archived > 0) break;
                    throw new InvalidOperationException(diagnosis.Message);
                }

                // Reinstalar o extrator apenas uma vez, depois de terminar o
                // processo ativo, evita atualizações em loop e arquivos em uso.
                if (attempt == 0 && IsHttp403(result.Error))
                {
                    item.Status = "preparing";
                    job.Status = "preparing";
                    item.Error = "HTTP 403. Atualizando o extrator e tentando outro método...";
                    tool = await _tools.RefreshYtDlpAsync(cts.Token);
                }

                if (attempt + 1 < profiles.Count)
                {
                    item.Error = $"{diagnosis.Message} Próximo método: {profiles[attempt + 1].Label}. {lastError}";
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 + attempt, 5)), cts.Token);
                }
            }

            // A opção --ignore-errors do yt-dlp pode devolver código zero mesmo
            // com vídeos indisponíveis na playlist. Não reportar sucesso falso.
            if (!succeeded)
            {
                if (req.Playlist && archived > 0)
                {
                    job.CompletedItems = archived;
                    job.FailedItems = Math.Max(1, job.TotalItems - archived);
                    job.Status = "partial";
                    item.Status = "partial";
                    item.Error = $"Baixados {archived} de {job.TotalItems} itens. {MediaDiagnostics.Classify(lastError).Message} {lastError}";
                    item.Speed = "";
                    item.Eta = "";
                    job.Progress = job.TotalItems > 0 ? Math.Min(100, archived * 100d / job.TotalItems) : 0;
                    _scanner.Scan();
                    return;
                }
                throw new InvalidOperationException(
                    $"Todas as {profiles.Count} estratégias falharam. {MediaDiagnostics.Classify(lastError).Message} Último erro: {lastError}");
            }

            item.Progress = 100;
            item.Status = "completed";
            item.Speed = "";
            item.Eta = "";
            item.Error = null;

            job.Progress = 100;
            job.Status = "completed";
            job.CompletedItems = req.Playlist ? Math.Max(archived, job.TotalItems) : 1;
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
            item.FailureCode = MediaDiagnostics.Classify(ex.Message).Code;

            job.Status = "failed";
            job.FailedItems = 1;
        }
        finally
        {
            _activeProcesses.TryRemove(item.Id, out _);
            _activeCancels.TryRemove(item.Id, out _);
        }
    }

    private async Task<(int ExitCode, string Error, int ConfirmedFiles)> RunDownloadProcessAsync(
        string tool,
        IReadOnlyList<string> args,
        DownloadJob job,
        DownloadItemJob item,
        CancellationToken cancellationToken)
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
        _activeProcesses[item.Id] = process;

        var confirmed = new ConcurrentBag<string>();
        var stdoutTask = ConsumeAsync(process.StandardOutput, job, item, null, confirmed, _store.Settings.LibraryFolder);
        var stderr = new StringBuilder();
        var stderrTask = ConsumeAsync(process.StandardError, job, item, stderr, confirmed, _store.Settings.LibraryFolder);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try { await process.WaitForExitAsync(); } catch { }
            throw;
        }
        finally
        {
            _activeProcesses.TryRemove(item.Id, out _);
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        return (process.ExitCode, stderr.ToString(), confirmed.Count);
    }

    private static int CountArchiveEntries(string? file)
    {
        if (file is null || !File.Exists(file)) return 0;
        try { return File.ReadLines(file).Count(line => !string.IsNullOrWhiteSpace(line)); }
        catch (IOException) { return 0; }
    }

    private List<string> BuildArgs(DownloadRequest req, string? ffmpeg, string deno, DownloadProfile profile, string? archivePath)
    {
        var output = req.Playlist
            ? Path.Combine(_store.Settings.LibraryFolder, "%(playlist_title)s", "%(playlist_index)03d - %(title)s [%(id)s].%(ext)s")
            : Path.Combine(_store.Settings.LibraryFolder, "%(title)s [%(id)s].%(ext)s");

        var args = new List<string>
        {
            "--ignore-config",
            "--no-colors", "--newline", "--windows-filenames",
            "--js-runtimes", $"deno:{deno}",
            "--retries", "10", "--fragment-retries", "10",
            "--socket-timeout", "30",
            "--continue",
            "--write-info-json", "--write-thumbnail",
            "--print", "after_move:FILE_DONE:%(filepath)s",
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
            args.Add("--ignore-errors");
            if (archivePath is not null)
            {
                args.Add("--download-archive");
                args.Add(archivePath);
            }
        }
        else
        {
            args.Add("--no-playlist");
        }

        bool audio = string.Equals(req.Mode, "audio", StringComparison.OrdinalIgnoreCase);
        args.Add("-f");
        args.Add(req.CapturedMedia ? "best/b" : DownloadRecovery.FormatSelector(audio, req.Quality, profile.FormatKind));

        if (audio)
        {
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
            args.Add("--merge-output-format");
            args.Add("mp4");
        }

        if (req.CapturedMedia && MediaCore.BrowserMediaClassifier.IsWebPage(req.SourcePage))
        {
            args.Add("--add-header");
            args.Add("Referer:" + req.SourcePage);
        }
        args.AddRange(profile.ExtraArgs);
        args.AddRange(BrowserArgs(req.CookieBrowser));
        args.Add(req.Url);
        return args;
    }

    private static async Task ConsumeAsync(StreamReader reader, DownloadJob job, DownloadItemJob item,
        StringBuilder? capture = null, ConcurrentBag<string>? confirmed = null, string? library = null)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            capture?.AppendLine(line);
            var trimmed = line.Trim();
            if (trimmed.StartsWith("FILE_DONE:", StringComparison.Ordinal))
            {
                TryConfirm(trimmed["FILE_DONE:".Length..].Trim(), confirmed, library);
                continue;
            }
            if (trimmed.StartsWith("[download] ", StringComparison.OrdinalIgnoreCase) &&
                trimmed.Contains(" has already been downloaded", StringComparison.OrdinalIgnoreCase))
            {
                var actual = trimmed["[download] ".Length..];
                var end = actual.IndexOf(" has already been downloaded", StringComparison.OrdinalIgnoreCase);
                if (end > 0) TryConfirm(actual[..end], confirmed, library);
            }

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

    private static void TryConfirm(string path, ConcurrentBag<string>? confirmed, string? library)
    {
        if (confirmed is null || string.IsNullOrWhiteSpace(library)) return;
        try
        {
            string root = Path.GetFullPath(library).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string file = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(library, path));
            if (file.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(file) && new FileInfo(file).Length > 0)
                confirmed.Add(file);
        }
        catch (IOException) { }
        catch (ArgumentException) { }
    }

    private static bool IsHttp403(string error) =>
        error.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("403: Forbidden", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase);

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
