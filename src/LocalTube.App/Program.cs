using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using LocalTube;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstance = new Mutex(true, "Local\\YouTubeOffline.SingleInstance", out var firstInstance);
        if (!firstInstance)
        {
            NativeWindowHelper.ActivateExistingWindow("YouTube Offline");
            return;
        }

        ApplicationConfiguration.Initialize();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = "wwwroot"
        });

        builder.Services.AddSingleton<LocalTubeStore>();
        builder.Services.AddSingleton<ToolManager>();
        builder.Services.AddSingleton<MediaTooling>();
        builder.Services.AddSingleton<MediaScanner>();
        builder.Services.AddSingleton<YtDlpService>();
        builder.Services.AddSingleton<UpdateService>();

        var app = builder.Build();
        var store = app.Services.GetRequiredService<LocalTubeStore>();
        var scanner = app.Services.GetRequiredService<MediaScanner>();
        var downloader = app.Services.GetRequiredService<YtDlpService>();
        var updater = app.Services.GetRequiredService<UpdateService>();
        scanner.Scan();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/api/settings", () => new
        {
            libraryFolder = store.Settings.LibraryFolder,
            port = store.Settings.Port,
            toolStatus = downloader.GetToolStatus(),
            update = updater.GetStatus()
        });

        app.MapPost("/api/settings/library", IResult (JsonElement body) =>
        {
            if (!body.TryGetProperty("path", out var p) || string.IsNullOrWhiteSpace(p.GetString()))
                return Results.BadRequest(new { error = "Informe uma pasta." });
            try
            {
                store.SetLibraryFolder(p.GetString()!);
                var items = scanner.Scan();
                return Results.Ok(new { libraryFolder = store.Settings.LibraryFolder, count = items.Count });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/settings/open-folder", IResult () =>
        {
            try
            {
                Directory.CreateDirectory(store.Settings.LibraryFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{store.Settings.LibraryFolder}\"") { UseShellExecute = true });
                return Results.Ok();
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/media", (string? search, string? type) =>
        {
            IEnumerable<MediaItem> items = scanner.Items;
            if (!string.IsNullOrWhiteSpace(type) && type != "all")
                items = items.Where(x => x.MediaType.Equals(type, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(search))
            {
                var q = search.Trim();
                items = items.Where(x =>
                    x.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    x.Channel.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    (x.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    x.RelativePath.Contains(q, StringComparison.OrdinalIgnoreCase));
            }
            return Results.Ok(items);
        });

        app.MapGet("/api/media/{id}", IResult (string id) =>
        {
            var item = scanner.Find(id);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        app.MapPost("/api/media/rescan", () => Results.Ok(scanner.Scan()));

        app.MapGet("/api/media/{id}/stream", IResult (string id) =>
        {
            var item = scanner.Find(id);
            if (item is null || !File.Exists(item.FullPath)) return Results.NotFound();
            return Results.File(item.FullPath, GetContentType(item.FullPath), enableRangeProcessing: true);
        });

        app.MapGet("/api/media/{id}/thumbnail", IResult (string id) =>
        {
            var item = scanner.Find(id);
            if (item is null) return Results.NotFound();
            var thumbnail = item.ThumbnailPath;
            if (string.IsNullOrWhiteSpace(thumbnail) || !File.Exists(thumbnail))
                thumbnail = scanner.EnsureThumbnail(id);
            if (string.IsNullOrWhiteSpace(thumbnail) || !File.Exists(thumbnail)) return Results.NotFound();
            return Results.File(thumbnail, GetContentType(thumbnail), enableRangeProcessing: true);
        });

        app.MapPost("/api/media/{id}/like", IResult (string id, JsonElement body) =>
        {
            var item = scanner.Find(id);
            if (item is null) return Results.NotFound();
            var state = store.GetOrCreateVideoState(id);
            state.Liked = body.TryGetProperty("liked", out var liked) ? liked.GetBoolean() : !state.Liked;
            item.Liked = state.Liked;
            store.SaveState();
            return Results.Ok(new { liked = state.Liked });
        });

        app.MapPost("/api/media/{id}/watch-later", IResult (string id, JsonElement body) =>
        {
            var item = scanner.Find(id);
            if (item is null) return Results.NotFound();
            var state = store.GetOrCreateVideoState(id);
            state.WatchLater = body.TryGetProperty("watchLater", out var watchLater) ? watchLater.GetBoolean() : !state.WatchLater;
            item.WatchLater = state.WatchLater;
            store.SaveState();
            return Results.Ok(new { watchLater = state.WatchLater });
        });

        app.MapPost("/api/media/{id}/progress", IResult (string id, JsonElement body) =>
        {
            if (scanner.Find(id) is null) return Results.NotFound();
            var state = store.GetOrCreateVideoState(id);
            if (body.TryGetProperty("progressSeconds", out var p) && p.TryGetDouble(out var progress))
                state.ProgressSeconds = Math.Max(0, progress);
            if (body.TryGetProperty("durationSeconds", out var d) && d.TryGetDouble(out var duration))
                state.DurationSeconds = Math.Max(0, duration);
            state.LastWatchedAt = DateTimeOffset.UtcNow;
            store.SaveState();
            return Results.Ok();
        });

        app.MapGet("/api/playlists", () => Results.Ok(store.State.Playlists));

        app.MapPost("/api/playlists", IResult (JsonElement body) =>
        {
            var name = body.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "Nome obrigatório." });
            var playlist = new LocalPlaylist { Name = name.Length > 80 ? name[..80] : name };
            store.State.Playlists.Add(playlist);
            store.SaveState();
            return Results.Ok(playlist);
        });

        app.MapDelete("/api/playlists/{playlistId}", IResult (string playlistId) =>
        {
            var p = store.State.Playlists.FirstOrDefault(x => x.Id == playlistId);
            if (p is null) return Results.NotFound();
            store.State.Playlists.Remove(p);
            store.SaveState();
            return Results.Ok();
        });

        app.MapPost("/api/playlists/{playlistId}/items/{mediaId}", IResult (string playlistId, string mediaId) =>
        {
            var p = store.State.Playlists.FirstOrDefault(x => x.Id == playlistId);
            if (p is null || scanner.Find(mediaId) is null) return Results.NotFound();
            if (!p.ItemIds.Contains(mediaId)) p.ItemIds.Add(mediaId);
            store.SaveState();
            return Results.Ok(p);
        });

        app.MapDelete("/api/playlists/{playlistId}/items/{mediaId}", IResult (string playlistId, string mediaId) =>
        {
            var p = store.State.Playlists.FirstOrDefault(x => x.Id == playlistId);
            if (p is null) return Results.NotFound();
            p.ItemIds.Remove(mediaId);
            store.SaveState();
            return Results.Ok(p);
        });

        app.MapPost("/api/download/analyze", async Task<IResult> (AnalyzeRequest req) =>
        {
            try { return Results.Ok(await downloader.AnalyzeAsync(req.Url, req.CookieBrowser)); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/download/start", async Task<IResult> (DownloadRequest req) =>
        {
            try { return Results.Ok(await downloader.StartAsync(req)); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/download/jobs", () => Results.Ok(downloader.Jobs));
        app.MapPost("/api/download/items/{itemId}/cancel", IResult (string itemId) =>
        {
            return downloader.CancelItem(itemId)
                ? Results.Ok(new { cancelled = true })
                : Results.NotFound(new { error = "Download não encontrado." });
        });
        app.MapGet("/api/tools/status", () => Results.Ok(downloader.GetToolStatus()));
        app.MapPost("/api/tools/prepare", async Task<IResult> (ToolManager tools) =>
        {
            try
            {
                await tools.PrepareAllAsync();
                return Results.Ok(tools.GetStatus());
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/update/status", () => Results.Ok(updater.GetStatus()));

        app.MapPost("/api/update/check", async Task<IResult> () =>
        {
            try { return Results.Ok(await updater.CheckAsync()); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/update/install", async Task<IResult> () =>
        {
            try
            {
                var installer = await updater.DownloadInstallerAsync();
                updater.LaunchInstaller(installer);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(750);
                    Application.Exit();
                });
                return Results.Ok(new { started = true });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapFallbackToFile("index.html");

        var url = $"http://127.0.0.1:{store.Settings.Port}";
        app.Urls.Clear();
        app.Urls.Add(url);

        try
        {
            app.StartAsync().GetAwaiter().GetResult();
            using var form = new MainForm(url);

            // Atualização automática: depois que a janela abre, consulta o feed público.
            // Se houver uma versão nova, baixa o instalador, valida SHA-256, inicia a
            // instalação silenciosa e fecha esta instância. O instalador reabre o app.
            form.Shown += (_, _) =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(4));
                        var started = await updater.TryInstallAvailableUpdateAsync();
                        if (started && !form.IsDisposed)
                        {
                            form.BeginInvoke(new Action(Application.Exit));
                        }
                    }
                    catch
                    {
                        // Falhas de atualização não impedem o uso offline. O erro continua
                        // disponível em Configurações > Atualizações para tentativa manual.
                    }
                });
            };

            Application.Run(form);
        }
        finally
        {
            app.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static string GetContentType(string path)
    {
        var provider = new FileExtensionContentTypeProvider();
        if (provider.TryGetContentType(path, out var contentType)) return contentType;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mkv" => "video/x-matroska",
            ".opus" => "audio/ogg",
            ".flac" => "audio/flac",
            _ => "application/octet-stream"
        };
    }
}

internal static class NativeWindowHelper
{
    private const int SwRestore = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static void ActivateExistingWindow(string title)
    {
        var hwnd = FindWindow(null, title);
        if (hwnd == IntPtr.Zero) return;
        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }
}
