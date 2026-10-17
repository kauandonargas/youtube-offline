using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalTube;

public sealed class MediaScanner
{
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".webm", ".m4v", ".mov", ".mkv" };

    private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".flac" };

    private readonly LocalTubeStore _store;
    private readonly MediaTooling _tooling;
    private readonly object _gate = new();
    private List<MediaItem> _items = new();

    public MediaScanner(LocalTubeStore store, MediaTooling tooling)
    {
        _store = store;
        _tooling = tooling;
    }

    public IReadOnlyList<MediaItem> Items
    {
        get { lock (_gate) return _items.ToArray(); }
    }

    public List<MediaItem> Scan()
    {
        var root = _store.Settings.LibraryFolder;
        Directory.CreateDirectory(root);
        var result = new List<MediaItem>();

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            var type = VideoExt.Contains(ext) ? "video" : AudioExt.Contains(ext) ? "audio" : null;
            if (type is null) continue;

            try
            {
                var info = new FileInfo(file);
                var relative = Path.GetRelativePath(root, file);
                var id = StableId(relative);
                var item = new MediaItem
                {
                    Id = id,
                    Title = Path.GetFileNameWithoutExtension(file),
                    Channel = new DirectoryInfo(Path.GetDirectoryName(file) ?? root).Name,
                    MediaType = type,
                    FileName = info.Name,
                    FullPath = info.FullName,
                    RelativePath = relative,
                    SizeBytes = info.Length,
                    ModifiedAt = info.LastWriteTimeUtc
                };

                EnrichFromYtDlpSidecar(item);
                if (item.DurationSeconds is null || item.SourceUrl is null)
                    _tooling.EnrichFromFileMetadata(item);
                if (!item.HasThumbnail && type == "video" && _tooling.CanGenerateThumbnails)
                    item.HasThumbnail = true;
                item.Description ??= $"Arquivo local: {relative}";
                item.PublishedAt ??= item.ModifiedAt;

                if (_store.State.VideoStates.TryGetValue(id, out var state))
                {
                    item.Liked = state.Liked;
                    item.WatchLater = state.WatchLater;
                    item.ProgressSeconds = state.ProgressSeconds;
                    item.LastWatchedAt = state.LastWatchedAt;
                    if (state.DurationSeconds > 0 && item.DurationSeconds is null)
                        item.DurationSeconds = state.DurationSeconds;
                }

                result.Add(item);
            }
            catch { /* arquivo em uso ou removido durante scan */ }
        }

        result = result.OrderByDescending(x => x.ModifiedAt).ToList();
        lock (_gate) _items = result;
        return result;
    }

    public MediaItem? Find(string id)
    {
        lock (_gate) return _items.FirstOrDefault(x => x.Id == id);
    }

    public string? EnsureThumbnail(string id)
    {
        var item = Find(id);
        return item is null ? null : _tooling.EnsureThumbnail(item);
    }

    private static string StableId(string relativePath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/').ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..20].ToLowerInvariant();
    }

    private static void EnrichFromYtDlpSidecar(MediaItem item)
    {
        var dir = Path.GetDirectoryName(item.FullPath)!;
        var stem = Path.GetFileNameWithoutExtension(item.FullPath);
        var infoPath = Path.Combine(dir, stem + ".info.json");

        if (File.Exists(infoPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(infoPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                    item.Title = title.GetString() ?? item.Title;
                if (root.TryGetProperty("uploader", out var uploader) && uploader.ValueKind == JsonValueKind.String)
                    item.Channel = uploader.GetString() ?? item.Channel;
                else if (root.TryGetProperty("channel", out var channel) && channel.ValueKind == JsonValueKind.String)
                    item.Channel = channel.GetString() ?? item.Channel;
                if (root.TryGetProperty("duration", out var duration) && duration.TryGetDouble(out var seconds))
                    item.DurationSeconds = seconds;
                if (root.TryGetProperty("webpage_url", out var source) && source.ValueKind == JsonValueKind.String)
                    item.SourceUrl = source.GetString();
                if (root.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
                    item.Description = description.GetString();
                if (root.TryGetProperty("view_count", out var views) && views.TryGetInt64(out var viewCount))
                    item.ViewCount = viewCount;
                if (root.TryGetProperty("id", out var originalId) && originalId.ValueKind == JsonValueKind.String)
                    item.OriginalId = originalId.GetString();
                if (root.TryGetProperty("channel_url", out var channelUrl) && channelUrl.ValueKind == JsonValueKind.String)
                    item.ChannelUrl = channelUrl.GetString();
                else if (root.TryGetProperty("uploader_url", out var uploaderUrl) && uploaderUrl.ValueKind == JsonValueKind.String)
                    item.ChannelUrl = uploaderUrl.GetString();
                if (root.TryGetProperty("extractor_key", out var extractor) && extractor.ValueKind == JsonValueKind.String)
                    item.Extractor = extractor.GetString();
                if (root.TryGetProperty("upload_date", out var uploadDate) && uploadDate.ValueKind == JsonValueKind.String)
                {
                    var raw = uploadDate.GetString();
                    if (raw?.Length == 8 && DateTime.TryParseExact(raw, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                        item.PublishedAt = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
                }
            }
            catch { }
        }

        foreach (var thumbExt in new[] { ".jpg", ".jpeg", ".webp", ".png" })
        {
            var candidate = Path.Combine(dir, stem + thumbExt);
            if (File.Exists(candidate))
            {
                item.ThumbnailPath = candidate;
                item.HasThumbnail = true;
                break;
            }
        }
    }
}
