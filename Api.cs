using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kanban;

public sealed class CreateBoardReq { public string? Name { get; set; } }
public sealed class UpdateBoardReq { public string? Name { get; set; } public int? Position { get; set; } }

public sealed class CreateColumnReq { public long BoardId { get; set; } public string? Name { get; set; } }
public sealed class UpdateColumnReq
{
    public string? Name { get; set; }
    public int? Position { get; set; }
    public int? WipLimit { get; set; }
    public bool ClearWipLimit { get; set; }
}

public sealed class CreateCardReq
{
    public long ColumnId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int? Priority { get; set; }
    public string? DueDate { get; set; }
    public string? Tags { get; set; }
}

public sealed class UpdateCardReq
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int? Priority { get; set; }
    public string? DueDate { get; set; }
    public bool ClearDueDate { get; set; }
    public string? Tags { get; set; }
}

public sealed class MoveCardReq { public long ColumnId { get; set; } public int Index { get; set; } }

public sealed class ChecklistReq { public List<ChecklistItemDto> Items { get; set; } = new(); }

public sealed class LinkReq { public string? Title { get; set; } public string? Url { get; set; } }

public static class Api
{
    /// <summary>Максимальный размер одного вложения.</summary>
    private const long MaxUpload = 25L * 1024 * 1024;

#if NOSYNC
    private const bool SyncAvailable = false;
#else
    private const bool SyncAvailable = true;
#endif

    private static readonly JsonSerializerOptions ImportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

#if NOSYNC
    public static void Map(WebApplication app, Store store, AppConfig cfg, int port, Action quit)
#else
    public static void Map(WebApplication app, Store store, AppConfig cfg, int port, SyncService sync, Action quit)
#endif
    {
        var web = WebAssets.Load();

        // Ошибки разбора тела запроса (битый JSON, не те типы) отдаём в том же формате, что и остальные ошибки API.
        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (BadHttpRequestException ex) when (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { error = "Некорректные данные: " + ex.Message });
            }
        });

        app.MapGet("/", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return web.Index();
        });

        app.MapGet("/{**path}", (string? path, HttpContext ctx) =>
        {
            if (!string.IsNullOrEmpty(path) && path.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "Не найдено" }, statusCode: 404);

            // no-store, а не no-cache: после обновления exe браузер не должен показывать старые html/js/css.
            ctx.Response.Headers.CacheControl = "no-store";
            return web.File(path ?? "");
        });

        // ---- состояние ----
        app.MapGet("/api/state", () => Guard(() => Results.Json(store.GetState())));

        // ---- доски ----
        app.MapPost("/api/boards", (CreateBoardReq req) => Guard(() =>
        {
            var board = store.CreateBoard(req.Name ?? "Новая доска");
            return Results.Json(board);
        }));

        app.MapPatch("/api/boards/{id:long}", (long id, UpdateBoardReq req) => Guard(() =>
        {
            store.UpdateBoard(id, req.Name, req.Position);
            return Results.NoContent();
        }));

        app.MapDelete("/api/boards/{id:long}", (long id) => Guard(() =>
        {
            store.DeleteBoard(id);
            return Results.NoContent();
        }));

        // ---- колонки ----
        app.MapPost("/api/columns", (CreateColumnReq req) => Guard(() =>
            Results.Json(store.CreateColumn(req.BoardId, req.Name))));

        app.MapPatch("/api/columns/{id:long}", (long id, UpdateColumnReq req) => Guard(() =>
        {
            store.UpdateColumn(id, req.Name, req.Position, req.WipLimit, req.ClearWipLimit);
            return Results.NoContent();
        }));

        app.MapDelete("/api/columns/{id:long}", (long id) => Guard(() =>
        {
            store.DeleteColumn(id);
            return Results.NoContent();
        }));

        app.MapPost("/api/columns/reorder", (ReorderReq req) => Guard(() =>
        {
            store.ReorderColumns(req.BoardId, req.Ids);
            return Results.NoContent();
        }));

        // ---- карточки ----
        app.MapPost("/api/cards", (CreateCardReq req) => Guard(() =>
            Results.Json(store.CreateCard(req.ColumnId, req.Title, req.Description, req.Priority, req.DueDate, req.Tags))));

        app.MapPatch("/api/cards/{id:long}", (long id, UpdateCardReq req) => Guard(() =>
        {
            store.UpdateCard(id, req.Title, req.Description, req.Priority, req.DueDate, req.ClearDueDate, req.Tags);
            return Results.NoContent();
        }));

        app.MapDelete("/api/cards/{id:long}", (long id) => Guard(() =>
        {
            store.DeleteCard(id);
            return Results.NoContent();
        }));

        app.MapPost("/api/cards/{id:long}/move", (long id, MoveCardReq req) => Guard(() =>
        {
            store.MoveCard(id, req.ColumnId, req.Index);
            return Results.NoContent();
        }));

        // ---- архив ----
        app.MapPost("/api/cards/{id:long}/archive", (long id) => Guard(() =>
        {
            store.SetArchived(id, true);
            return Results.NoContent();
        }));

        app.MapPost("/api/cards/{id:long}/unarchive", (long id) => Guard(() =>
        {
            store.SetArchived(id, false);
            return Results.NoContent();
        }));

        // ---- чек-лист ----
        app.MapPut("/api/cards/{id:long}/checklist", (long id, ChecklistReq req) => Guard(() =>
        {
            store.ReplaceChecklist(id, req.Items);
            return Results.NoContent();
        }));

        // ---- вложения ----
        app.MapPost("/api/cards/{id:long}/attachments/link", (long id, LinkReq req) => Guard(() =>
            Results.Json(store.AddLink(id, req.Title, req.Url))));

        app.MapPost("/api/cards/{id:long}/attachments/file", async (long id, string? name, HttpRequest req) =>
            await GuardAsync(async () =>
            {
                if (req.ContentLength is long length && length > MaxUpload)
                    return Results.Json(new { error = "Файл больше 25 МБ" }, statusCode: 413);

                using var ms = new MemoryStream();
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await req.Body.ReadAsync(buffer)) > 0)
                {
                    ms.Write(buffer, 0, read);
                    if (ms.Length > MaxUpload)
                        return Results.Json(new { error = "Файл больше 25 МБ" }, statusCode: 413);
                }

                return Results.Json(store.AddFile(id, name, ms.ToArray()));
            }));

        app.MapGet("/api/attachments/{id:long}/download", (long id) => Guard(() =>
        {
            var (att, storedName) = store.GetAttachment(id);
            if (att.Kind != "file" || string.IsNullOrEmpty(storedName))
                return Results.Json(new { error = "Это ссылка, а не файл" }, statusCode: 400);

            string path = Path.Combine(store.FilesDir, Path.GetFileName(storedName));
            if (!File.Exists(path))
                return Results.Json(new { error = "Файл вложения не найден на диске" }, statusCode: 404);

            return Results.File(path, "application/octet-stream", att.OriginalName ?? att.Title);
        }));

        app.MapDelete("/api/attachments/{id:long}", (long id) => Guard(() =>
        {
            store.DeleteAttachment(id);
            return Results.NoContent();
        }));

        // ---- обслуживание ----
        app.MapPost("/api/backup", () => Guard(() => Results.Json(new { file = store.Backup() })));

        app.MapGet("/api/export", () => Guard(() =>
        {
            byte[] bytes = Encoding.UTF8.GetBytes(store.ExportJson());
            return Results.File(bytes, "application/json; charset=utf-8",
                $"kanban-export-{DateTime.Now:yyyyMMdd-HHmm}.json");
        }));

        app.MapGet("/api/export/zip", () => Guard(() =>
            Results.File(store.ExportZip(), "application/zip",
                $"kanban-export-{DateTime.Now:yyyyMMdd-HHmm}.zip")));

        app.MapGet("/api/export/csv", () => Guard(() =>
            Results.File(Encoding.UTF8.GetBytes(store.ExportCsv()), "text/csv; charset=utf-8",
                $"kanban-export-{DateTime.Now:yyyyMMdd-HHmm}.csv")));

        // Импорт принимает и JSON, и ZIP с вложениями: формат определяется по сигнатуре «PK».
        // Обработчик пишет ответ сам: у делегата с HttpContext минимальный API иначе теряет IResult.
        app.MapPost("/api/import", (HttpContext ctx) => ImportAsync(ctx, store));

        app.MapGet("/api/info", () => Guard(() =>
        {
            var db = new FileInfo(store.DbPath);
            var backups = new DirectoryInfo(store.BackupDir).Exists
                ? new DirectoryInfo(store.BackupDir).GetFiles("kanban-*.db")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(20)
                    .Select(f => new { name = f.Name, size = f.Length, date = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm") })
                    .ToArray()
                : Array.Empty<object>();

            long filesSize = 0;
            int filesCount = 0;
            if (Directory.Exists(store.FilesDir))
            {
                var files = new DirectoryInfo(store.FilesDir).GetFiles();
                filesCount = files.Length;
                filesSize = files.Sum(f => f.Length);
            }

            return Results.Json(new
            {
                version = Program.AppVersion,
                port,
                appDir = store.DataDir,
                dbPath = store.DbPath,
                dbSize = db.Exists ? db.Length : 0,
                filesCount,
                filesSize,
                backups,
                updateDir = Updater.Dir(store.DataDir),
            });
        }));

        // ---- обновление: папка updates\ и GitHub Releases ----
        app.MapGet("/api/update/check", async (HttpContext ctx, CancellationToken ct) =>
        {
            bool force = ctx.Request.Query["force"] == "1";
            return await GuardAsync(async () =>
                Results.Json(await Updater.CheckAsync(store.DataDir, cfg, force, ct)));
        });

        app.MapGet("/api/update/progress", () => Guard(() => Results.Json(Updater.GetProgress())));

        app.MapPost("/api/update/apply", async (CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                var info = await Updater.CheckAsync(store.DataDir, cfg, force: true, ct);
                if (!info.Available)
                    return Results.Json(new { error = info.Error ?? "Обновление не найдено" }, statusCode: 400);

                await Updater.ApplyAsync(store.DataDir, info, quit, ct);
                return Results.Json(new { ok = true, version = info.Version, source = info.Source });
            }));

        app.MapPost("/api/reveal", () => Guard(() =>
        {
            Shell.OpenFolder(store.DataDir);
            return Results.Json(new { ok = true });
        }));

        // ---- синхронизация через WebDAV ----
#if !NOSYNC
        app.MapGet("/api/sync/status", () => Guard(() => Results.Json(sync.GetStatus())));

        app.MapPut("/api/sync/config", (SyncConfigReq req) => Guard(() =>
        {
            sync.SaveOptions(req);
            return Results.Json(sync.GetStatus());
        }));

        app.MapPost("/api/sync/test", async (CancellationToken ct) =>
            await GuardAsync(async () => Results.Json(await sync.TestAsync(ct))));

        app.MapPost("/api/sync/run", async (SyncRunReq req, CancellationToken ct) =>
            await GuardAsync(async () => Results.Json(await sync.RunAsync(req.Mode ?? "auto", ct))));

        app.MapPost("/api/sync/resolve", async (SyncRunReq req, CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                string mode = req.Mode is "push" or "pull" or "both" ? req.Mode : "auto";
                return Results.Json(await sync.RunAsync(mode, ct));
            }));
#endif

        app.MapPost("/api/quit", () =>
        {
            quit();
            return Results.Json(new { ok = true });
        });

        app.MapGet("/api/health", () => Results.Json(new { ok = true, version = Program.AppVersion, sync = SyncAvailable }));
    }

    public sealed class ReorderReq
    {
        public long BoardId { get; set; }
        public long[] Ids { get; set; } = Array.Empty<long>();
    }

    private static IResult Guard(Func<IResult> action)
    {
        try { return action(); }
        catch (AppException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
#if !NOSYNC
        catch (SyncException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
#endif
        catch (Exception ex)
        {
            AppLog.Error("API", ex);
            return Results.Json(new { error = "Внутренняя ошибка: " + ex.Message }, statusCode: 500);
        }
    }

    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (AppException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
#if !NOSYNC
        catch (SyncException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
#endif
        catch (Exception ex)
        {
            AppLog.Error("API", ex);
            return Results.Json(new { error = "Внутренняя ошибка: " + ex.Message }, statusCode: 500);
        }
    }

    /// <summary>Импорт JSON или ZIP. Пишет ответ напрямую в HttpResponse.</summary>
    private static async Task ImportAsync(HttpContext ctx, Store store)
    {
        try
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            byte[] bytes = ms.ToArray();

            if (bytes.Length == 0)
            {
                await WriteError(ctx, 400, "Пустой файл");
                return;
            }

            bool isZip = bytes.Length > 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K';
            string? tempDir = null;
            StateDto? state;

            try
            {
                if (isZip)
                {
                    tempDir = Path.Combine(Path.GetTempPath(), "kanban-import-" + Guid.NewGuid().ToString("N")[..8]);
                    var (dataPath, _) = Store.UnpackZip(bytes, tempDir);
                    state = JsonSerializer.Deserialize<StateDto>(await File.ReadAllTextAsync(dataPath), ImportJsonOptions);
                }
                else
                {
                    state = JsonSerializer.Deserialize<StateDto>(bytes, ImportJsonOptions);
                }
            }
            catch (AppException) { throw; }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                AppLog.Error("Разбор файла импорта", ex);
                await WriteError(ctx, 400, "Файл повреждён или имеет неверный формат");
                return;
            }

            try
            {
                if (state is null || state.Boards is null || state.Boards.Count == 0)
                {
                    await WriteError(ctx, 400, "В файле нет ни одной доски");
                    return;
                }

                // Перед перезаписью сохраняем текущее состояние.
                store.Backup();
                store.ImportJson(state, replaceFiles: true,
                    sourceFilesDir: tempDir is null ? null : Path.Combine(tempDir, "files"));

                await ctx.Response.WriteAsJsonAsync(new
                {
                    ok = true,
                    boards = state.Boards.Count,
                    cards = state.Cards?.Count ?? 0,
                    files = (state.Cards ?? new List<CardDto>()).Sum(c => (c.Attachments ?? new List<AttachmentDto>()).Count(a => a.Kind == "file")),
                });
            }
            finally
            {
                if (tempDir is not null)
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
        catch (AppException ex) { await WriteError(ctx, ex.StatusCode, ex.Message); }
        catch (Exception ex)
        {
            AppLog.Error("Импорт", ex);
            await WriteError(ctx, 500, "Внутренняя ошибка: " + ex.Message);
        }
    }

    private static async Task WriteError(HttpContext ctx, int status, string message)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { error = message });
    }
}

/// <summary>Статика (html/css/js) вшита в exe — приложению не нужен интернет и папка wwwroot рядом.</summary>
public sealed class WebAssets
{
    private readonly Dictionary<string, (byte[] Bytes, string ContentType)> _files =
        new(StringComparer.OrdinalIgnoreCase);

    public static WebAssets Load()
    {
        var assets = new WebAssets();
        Assembly asm = typeof(WebAssets).Assembly;
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("web/", StringComparison.Ordinal)) continue;
            using Stream? s = asm.GetManifestResourceStream(name);
            if (s is null) continue;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            string file = name["web/".Length..];
            assets._files[file] = (ms.ToArray(), ContentTypeFor(file));
        }
        return assets;
    }

    public IResult Index() =>
        _files.TryGetValue("index.html", out var f)
            ? Results.Bytes(f.Bytes, f.ContentType)
            : Results.Text("Ресурсы приложения не найдены (index.html)", statusCode: 500);

    public IResult File(string path)
    {
        path = path.Replace('\\', '/').TrimStart('/');
        if (_files.TryGetValue(path, out var f)) return Results.Bytes(f.Bytes, f.ContentType);
        return Results.NotFound();
    }

    private static string ContentTypeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };
}
