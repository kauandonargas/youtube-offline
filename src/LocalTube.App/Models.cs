using System.Text.Json.Serialization;

namespace LocalTube;

public sealed class AppSettings
{
    public string LibraryFolder { get; set; } = "";
    public int Port { get; set; } = 43127;
}

public sealed class LibraryState
{
    public Dictionary<string, UserVideoState> VideoStates { get; set; } = new();
    public List<LocalPlaylist> Playlists { get; set; } = new();
}

public sealed class UserVideoState
{
    public bool Liked { get; set; }
    public bool WatchLater { get; set; }
    public double ProgressSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public DateTimeOffset? LastWatchedAt { get; set; }
}

public sealed class LocalPlaylist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Nova playlist";
    public List<string> ItemIds { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MediaItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Channel { get; set; } = "Local";
    public string MediaType { get; set; } = "video";
    public string FileName { get; set; } = "";
    [JsonIgnore]
    public string FullPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public double? DurationSeconds { get; set; }
    public string? SourceUrl { get; set; }
    public string? Description { get; set; }
    public long? ViewCount { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? OriginalId { get; set; }
    public string? ChannelUrl { get; set; }
    public string? Extractor { get; set; }
    [JsonIgnore]
    public string? ThumbnailPath { get; set; }
    public bool HasThumbnail { get; set; }
    public bool Liked { get; set; }
    public bool WatchLater { get; set; }
    public double ProgressSeconds { get; set; }
    public DateTimeOffset? LastWatchedAt { get; set; }
}

// A download job represents one user action: one video or one full playlist.
// The v0.4-style engine executes a playlist in one yt-dlp process. Items stays as
// a compatibility/progress surface for the current API, normally with one item
// representing the active yt-dlp execution rather than one process per video.
public sealed class DownloadJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "queued";
    public bool IsPlaylist { get; set; }
    public int TotalItems { get; set; }
    public int CompletedItems { get; set; }
    public int FailedItems { get; set; }
    public int CancelledItems { get; set; }
    public int SkippedItems { get; set; }
    public double Progress { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<DownloadItemJob> Items { get; set; } = new();
}

public sealed class DownloadItemJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string BatchId { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Title { get; set; } = "Vídeo";
    public int PlaylistIndex { get; set; }
    public int PlaylistTotal { get; set; }
    public string Status { get; set; } = "queued";
    public double Progress { get; set; }
    public string Speed { get; set; } = "";
    public string Eta { get; set; } = "";
    public string? Error { get; set; }
    public string FailureCode { get; set; } = "";
    public int Attempt { get; set; }
    public int AttemptTotal { get; set; }
    public string Strategy { get; set; } = "";
    public bool Duplicate { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AnalyzeRequest
{
    public string Url { get; set; } = "";
    // Kept for API compatibility with the 0.5 UI. The restored v0.4 engine does
    // not import browser cookies automatically.
    public string CookieBrowser { get; set; } = "none";
}

public sealed class DownloadRequest
{
    public string Url { get; set; } = "";
    public string Mode { get; set; } = "video";
    public string Quality { get; set; } = "best";
    public string AudioFormat { get; set; } = "original";
    public bool Playlist { get; set; }
    public bool CapturedMedia { get; set; }
    public string SourcePage { get; set; } = "";
    // Kept for backward compatibility; ignored by the restored v0.4-style engine.
    public string CookieBrowser { get; set; } = "none";
}