using System.Diagnostics;
using System.Text.Json;

namespace LocalTube;

public sealed class MediaTooling
{
    private readonly string _thumbnailDir;
    private readonly object _gate = new();
    private readonly ToolManager _tools;

    public MediaTooling(LocalTubeStore store, ToolManager tools)
    {
        _tools = tools;
        _thumbnailDir = Path.Combine(store.DataDirectory, "thumbnails");
        Directory.CreateDirectory(_thumbnailDir);
    }

    public bool CanGenerateThumbnails => _tools.LocateFfmpeg() is not null;

    public void EnrichFromFileMetadata(MediaItem item)
    {
        var ffprobe = _tools.LocateFfprobe();
        if (ffprobe is null) return;

        try
        {
            var psi = new ProcessStartInfo(ffprobe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in new[]
            {
                "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", item.FullPath
            }) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null) return;
            var json = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3500) || process.ExitCode != 0 || string.IsNullOrWhiteSpace(json)) return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("format", out var format))
            {
                if (item.DurationSeconds is null && format.TryGetProperty("duration", out var duration))
                {
                    if (duration.ValueKind == JsonValueKind.String && double.TryParse(duration.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                        item.DurationSeconds = seconds;
                    else if (duration.TryGetDouble(out var seconds2))
                        item.DurationSeconds = seconds2;
                }

                if (format.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                {
                    var title = GetTag(tags, "title");
                    var artist = GetTag(tags, "artist") ?? GetTag(tags, "album_artist");
                    var description = GetTag(tags, "description") ?? GetTag(tags, "comment");
                    if (!string.IsNullOrWhiteSpace(title)) item.Title = title!;
                    if (!string.IsNullOrWhiteSpace(artist)) item.Channel = artist!;
                    if (string.IsNullOrWhiteSpace(item.Description) && !string.IsNullOrWhiteSpace(description)) item.Description = description;
                }
            }
        }
        catch { }
    }

    public string? EnsureThumbnail(MediaItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ThumbnailPath) && File.Exists(item.ThumbnailPath))
            return item.ThumbnailPath;
        if (!string.Equals(item.MediaType, "video", StringComparison.OrdinalIgnoreCase))
            return null;

        var ffmpeg = _tools.LocateFfmpeg();
        if (ffmpeg is null) return null;

        var target = Path.Combine(_thumbnailDir, item.Id + ".jpg");
        try
        {
            lock (_gate)
            {
                if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(item.FullPath))
                {
                    item.ThumbnailPath = target;
                    item.HasThumbnail = true;
                    return target;
                }

                var seek = item.DurationSeconds is > 15 ? Math.Min(12, Math.Max(2, item.DurationSeconds.Value * 0.08)) : 1.0;
                var psi = new ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var arg in new[]
                {
                    "-loglevel", "error", "-y", "-ss", seek.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "-i", item.FullPath, "-frames:v", "1", "-vf", "scale=640:-2", "-q:v", "3", target
                }) psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process is null) return null;
                if (!process.WaitForExit(9000))
                {
                    try { process.Kill(true); } catch { }
                    return null;
                }
                if (process.ExitCode == 0 && File.Exists(target))
                {
                    item.ThumbnailPath = target;
                    item.HasThumbnail = true;
                    return target;
                }
            }
        }
        catch { }
        return null;
    }

    private static string? GetTag(JsonElement tags, string name)
    {
        foreach (var property in tags.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        return null;
    }

}
