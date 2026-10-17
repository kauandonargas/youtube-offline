using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalTube;

public sealed class UpdateService
{
    public const string FeedUrl = "https://raw.githubusercontent.com/kauandonargas/youtube-offline/main/update.json";

    private readonly object _sync = new();
    private readonly string _updatesDir;
    private readonly HttpClient _client;
    private UpdateFeed? _feed;
    private string? _error;
    private DateTimeOffset? _lastCheckedAt;
    private bool _checking;
    private bool _downloading;
    private int _downloadPercent;

    public UpdateService(LocalTubeStore store)
    {
        _updatesDir = Path.Combine(store.DataDirectory, "updates");
        Directory.CreateDirectory(_updatesDir);

        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YouTubeOffline", CurrentVersion));
        _client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
    }

    public static string CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    public object GetStatus()
    {
        lock (_sync)
        {
            var available = IsNewer(_feed?.Version, CurrentVersion);
            return new
            {
                currentVersion = CurrentVersion,
                latestVersion = _feed?.Version,
                available,
                mandatory = _feed?.Mandatory ?? false,
                notes = _feed?.Notes ?? Array.Empty<string>(),
                publishedAt = _feed?.PublishedAt,
                checking = _checking,
                downloading = _downloading,
                downloadPercent = _downloadPercent,
                lastCheckedAt = _lastCheckedAt,
                error = _error,
                feedUrl = FeedUrl
            };
        }
    }

    public async Task<object> CheckAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_checking) return GetStatus();
            _checking = true;
            _error = null;
        }

        try
        {
            var separator = FeedUrl.Contains('?') ? '&' : '?';
            var url = FeedUrl + separator + "t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var feed = JsonSerializer.Deserialize<UpdateFeed>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (feed is null || string.IsNullOrWhiteSpace(feed.Version) || string.IsNullOrWhiteSpace(feed.InstallerUrl) || string.IsNullOrWhiteSpace(feed.Sha256))
                throw new InvalidOperationException("O feed de atualização está incompleto.");
            if (!string.Equals(feed.Product, "YouTube Offline", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O feed de atualização pertence a outro produto.");
            if (!Uri.TryCreate(feed.InstallerUrl, UriKind.Absolute, out var installerUri) || installerUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("A URL do instalador no feed não é segura.");

            lock (_sync)
            {
                _feed = feed;
                _lastCheckedAt = DateTimeOffset.UtcNow;
                _error = null;
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _lastCheckedAt = DateTimeOffset.UtcNow;
                _error = SimplifyError(ex);
            }
        }
        finally
        {
            lock (_sync) _checking = false;
        }

        return GetStatus();
    }

    public async Task<bool> TryInstallAvailableUpdateAsync(CancellationToken cancellationToken = default)
    {
        // Esta chamada é usada pela inicialização do aplicativo. Ela deve apenas
        // consultar o feed; instalar silenciosamente e chamar Application.Exit()
        // poucos segundos depois de abrir a janela torna qualquer falha do
        // instalador indistinguível de um crash. A instalação continua disponível
        // explicitamente por DownloadInstallerAsync + LaunchInstaller.
        await CheckAsync(cancellationToken);
        return false;
    }

    public async Task<string> DownloadInstallerAsync(CancellationToken cancellationToken = default)
    {
        UpdateFeed? feed;
        lock (_sync) feed = _feed;
        if (feed is null)
        {
            await CheckAsync(cancellationToken);
            lock (_sync) feed = _feed;
        }
        if (feed is null)
            throw new InvalidOperationException("Não foi possível obter as informações da atualização.");
        if (!IsNewer(feed.Version, CurrentVersion))
            throw new InvalidOperationException("Você já está usando a versão mais recente.");

        lock (_sync)
        {
            if (_downloading) throw new InvalidOperationException("Uma atualização já está sendo baixada.");
            _downloading = true;
            _downloadPercent = 0;
            _error = null;
        }

        var safeVersion = string.Concat(feed.Version.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'));
        var target = Path.Combine(_updatesDir, $"YouTube-Offline-Setup-{safeVersion}-x64.exe");
        var temp = target + ".download";

        try
        {
            if (File.Exists(target) && await HashMatchesAsync(target, feed.Sha256, cancellationToken))
            {
                lock (_sync) _downloadPercent = 100;
                return target;
            }

            TryDelete(temp);
            using var response = await _client.GetAsync(feed.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);

            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buffer = new byte[128 * 1024];
                long written = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read <= 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                    if (total is > 0)
                    {
                        var pct = (int)Math.Clamp(written * 100L / total.Value, 0, 99);
                        lock (_sync) _downloadPercent = pct;
                    }
                }
                await output.FlushAsync(cancellationToken);
            }

            if (!await HashMatchesAsync(temp, feed.Sha256, cancellationToken))
                throw new InvalidOperationException("O instalador baixado falhou na validação SHA-256. A atualização foi cancelada.");

            File.Move(temp, target, true);
            lock (_sync) _downloadPercent = 100;
            return target;
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            lock (_sync) _error = SimplifyError(ex);
            throw;
        }
        finally
        {
            lock (_sync) _downloading = false;
        }
    }

    public void LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath)) throw new FileNotFoundException("Instalador da atualização não encontrado.", installerPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
            UseShellExecute = true
        });
    }

    private static bool IsNewer(string? latestText, string currentText)
    {
        if (string.IsNullOrWhiteSpace(latestText)) return false;
        var latestClean = latestText.Trim().TrimStart('v', 'V');
        var currentClean = currentText.Trim().TrimStart('v', 'V');
        return Version.TryParse(latestClean, out var latest) && Version.TryParse(currentClean, out var current) && latest > current;
    }

    private static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken cancellationToken)
    {
        var normalized = expected.Trim().Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(actual),
            System.Text.Encoding.ASCII.GetBytes(normalized));
    }

    private static string SimplifyError(Exception ex)
    {
        if (ex is HttpRequestException)
            return "Não foi possível consultar o GitHub. Verifique sua conexão e tente novamente.";
        return ex.Message;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed class UpdateFeed
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("product")] public string Product { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("publishedAt")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("installerUrl")] public string InstallerUrl { get; set; } = "";
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
        [JsonPropertyName("mandatory")] public bool Mandatory { get; set; }
        [JsonPropertyName("notes")] public string[] Notes { get; set; } = Array.Empty<string>();
    }
}
