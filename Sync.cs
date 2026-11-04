using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Kanban;

/// <summary>Настройки WebDAV-синхронизации, которые лежат в kanban.config.json.</summary>
public sealed class SyncOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "https://webdav.yandex.ru";
    public string Login { get; set; } = "";
    /// <summary>Пароль приложения, зашифрованный DPAPI (Base64). Открытый текст в файл не попадает.</summary>
    public string PasswordProtected { get; set; } = "";
    public string Folder { get; set; } = "Kanban";
    public bool OnStart { get; set; } = true;
    public bool OnExit { get; set; } = true;

    /// <summary>Пароль в памяти. В JSON не сохраняется.</summary>
    [JsonIgnore]
    public string Password { get; set; } = "";

    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(Password);
}

public sealed class SyncConfigReq
{
    public string? Url { get; set; }
    public string? Login { get; set; }
    public string? Password { get; set; }
    public string? Folder { get; set; }
    public bool? Enabled { get; set; }
    public bool? OnStart { get; set; }
    public bool? OnExit { get; set; }
}

public sealed class SyncRunReq
{
    /// <summary>auto | push | pull | both</summary>
    public string? Mode { get; set; }
}

public sealed class SyncStatusDto
{
    public bool Configured { get; set; }
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public string Login { get; set; } = "";
    public string Folder { get; set; } = "";
    public bool OnStart { get; set; }
    public bool OnExit { get; set; }
    public bool HasPassword { get; set; }
    public bool PendingChanges { get; set; }
    public bool Running { get; set; }
    public bool Conflict { get; set; }
    public string? LastSyncAt { get; set; }
    public string? LastError { get; set; }
    public string? LastAction { get; set; }
    public string? LastMessage { get; set; }
    public string Device { get; set; } = Environment.MachineName;
}

public sealed class SyncRunResult
{
    public bool Ok { get; set; } = true;
    /// <summary>none | push | pull | both | conflict | busy | error | test</summary>
    public string Action { get; set; } = "none";
    public bool Conflict { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public int UploadedFiles { get; set; }
    public int DownloadedFiles { get; set; }
    public long RemoteSize { get; set; }
    public string? RemoteDevice { get; set; }
    public string? RemoteCreatedAt { get; set; }
    public string At { get; set; } = DateTime.Now.ToString("O");
}

/// <summary>Ошибка синхронизации с человекочитаемым текстом.</summary>
internal sealed class SyncException : Exception
{
    public int Status { get; }

    public SyncException(string message, int status = 0) : base(message) => Status = status;
}

/// <summary>Шифрование пароля средствами Windows (DPAPI, текущий пользователь).</summary>
internal static class Secret
{
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }
        catch (Exception ex)
        {
            AppLog.Error("DPAPI: не удалось зашифровать пароль", ex);
            return "";
        }
    }

    public static string? Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            // Другой пользователь/ПК: пароль придётся ввести заново.
            return null;
        }
    }
}

internal sealed class RemoteSnapshot
{
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
    public string? ETag { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public string Revision { get; set; } = "";
    public StateDto State { get; set; } = new();
    public string Device { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

/// <summary>Минимальный WebDAV-клиент: только то, что нужно для синхронизации снимка.</summary>
internal sealed class WebDavClient : IDisposable
{
    private static readonly HttpMethod Propfind = new("PROPFIND");
    private static readonly HttpMethod Mkcol = new("MKCOL");

    private readonly HttpClient _http;
    private readonly string _root;

    public WebDavClient(string url, string login, string password, string folder)
    {
        string baseUrl = (url ?? "").Trim();
        if (baseUrl.Length == 0) throw new SyncException("Не указан адрес сервера");
        if (!baseUrl.Contains("://", StringComparison.Ordinal)) baseUrl = "https://" + baseUrl;

        Uri uri;
        try { uri = new Uri(baseUrl, UriKind.Absolute); }
        catch (UriFormatException) { throw new SyncException("Некорректный адрес сервера: " + url); }

        // Mail.ru (и большинство облаков) отвечает 301 с http на https, а редирект для MKCOL/PROPFIND
        // превращается в GET и ломает запрос. Поэтому для внешних адресов принудительно используем https.
        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !IsLocalHost(uri.Host))
            uri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;

        string folderPath = string.Join('/', (folder ?? "")
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));

        string basePath = uri.AbsolutePath.TrimEnd('/');
        _root = $"{uri.GetLeftPart(UriPartial.Authority)}{basePath}/" + (folderPath.Length > 0 ? folderPath + "/" : "");

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Kanban/" + Program.AppVersion);
        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
    }

    public string Root => _root;

    /// <summary>Локальные адреса (127.0.0.1, 192.168.*, NAS в домашней сети) оставляем как есть.</summary>
    private static bool IsLocalHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;

        byte[] b = ip.GetAddressBytes();
        if (b.Length == 4)
            return b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);

        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;
    }

    public async Task EnsureFolderAsync(CancellationToken ct)
    {
        await MkcolAsync(_root, ct);
        await MkcolAsync(_root + "files/", ct);
    }

    public async Task<RemoteSnapshot?> GetSnapshotAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _root + "kanban.json");
        using var res = await SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw Fail(res, "Не удалось прочитать kanban.json");

        return new RemoteSnapshot
        {
            Bytes = await res.Content.ReadAsByteArrayAsync(ct),
            ETag = res.Headers.ETag?.ToString(),
            Modified = res.Content.Headers.LastModified,
        };
    }

    public async Task<string?> PutSnapshotAsync(byte[] data, string? etag, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, _root + "kanban.json");
        req.Content = new ByteArrayContent(data);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        // If-Match защищает от гонки: если файл на сервере изменился после чтения — получим 412.
        if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-Match", etag);

        using var res = await SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw Fail(res, "Не удалось загрузить kanban.json");
        return res.Headers.ETag?.ToString();
    }

    public async Task<HashSet<string>> ListFilesAsync(CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var req = new HttpRequestMessage(Propfind, _root + "files/");
        req.Headers.TryAddWithoutValidation("Depth", "1");
        req.Content = new StringContent(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>",
            Encoding.UTF8, "application/xml");

        using var res = await SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return names;
        if (!res.IsSuccessStatusCode) throw Fail(res, "Не удалось получить список вложений");

        string xml = await res.Content.ReadAsStringAsync(ct);
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { return names; }

        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "href"))
        {
            string href = el.Value.Trim();
            if (href.EndsWith('/')) continue;
            int slash = href.LastIndexOf('/');
            string name = Uri.UnescapeDataString(href[(slash + 1)..]);
            if (name.Length > 0) names.Add(name);
        }
        return names;
    }

    public async Task<byte[]?> GetFileAsync(string name, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _root + "files/" + Uri.EscapeDataString(name));
        using var res = await SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw Fail(res, "Не удалось скачать вложение");
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task PutFileAsync(string name, byte[] data, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, _root + "files/" + Uri.EscapeDataString(name));
        req.Content = new ByteArrayContent(data);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var res = await SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw Fail(res, $"Не удалось загрузить вложение «{name}»");
    }

    private async Task MkcolAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(Mkcol, url) { Content = new ByteArrayContent(Array.Empty<byte>()) };

        using (var res = await SendAsync(req, ct))
        {
            if (res.IsSuccessStatusCode) return;
            if (res.StatusCode == HttpStatusCode.MethodNotAllowed) return; // папка уже есть

            // Часть серверов (в том числе Mail.ru) отвечает 403/409 на MKCOL существующей папки.
            // Поэтому при любой ошибке сначала проверяем, есть ли уже такая коллекция.
            if (res.StatusCode != HttpStatusCode.Unauthorized && await ExistsAsync(url, ct)) return;

            throw Fail(res, "Не удалось создать папку на сервере");
        }
    }

    private async Task<bool> ExistsAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(Propfind, url);
        req.Headers.TryAddWithoutValidation("Depth", "0");
        req.Content = new StringContent(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>",
            Encoding.UTF8, "application/xml");

        using var res = await SendAsync(req, ct);
        return res.IsSuccessStatusCode;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try { return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SyncException("Сервер не ответил вовремя — проверьте адрес и интернет");
        }
        catch (HttpRequestException ex)
        {
            throw new SyncException("Нет связи с сервером: " + ex.Message);
        }
    }

    private static SyncException Fail(HttpResponseMessage res, string what)
    {
        int status = (int)res.StatusCode;
        string message = status switch
        {
            401 => "Неверный логин или пароль. Для Яндекс.Диска и Mail.ru нужен пароль приложения, а не обычный пароль от почты",
            403 => "Неверный логин или пароль (403). Mail.ru так отвечает и на обычный пароль вместо пароля для внешних приложений — проверьте его в настройках почты",
            404 => "Папка на сервере не найдена",
            409 => "Папка на сервере недоступна (409)",
            412 => "Файл на сервере изменился во время синхронизации",
            507 => "На сервере закончилось свободное место",
            _ => $"{what} (HTTP {status})",
        };
        return new SyncException(message, status);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Состояние последней синхронизации (sync-state.json рядом с базой).</summary>
public sealed class SyncState
{
    public string LocalHash { get; set; } = "";
    public string RemoteRevision { get; set; } = "";
    public string LastSyncAt { get; set; } = "";
    public string? LastError { get; set; }
    public string? LastAction { get; set; }
    public string? LastMessage { get; set; }
    public bool Conflict { get; set; }
    public string? ConflictAt { get; set; }
}

/// <summary>
/// Синхронизация «снимком»: на сервере лежит kanban.json и папка files с вложениями.
/// Одновременная работа не предполагается, поэтому слияние не построчное: побеждает
/// тот ПК, который синхронизировался, а при одновременных правках пользователь выбирает версию.
/// </summary>
public sealed class SyncService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Store _store;
    private readonly AppConfig _cfg;
    private readonly string _dir;
    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SyncState _state;

    public SyncService(Store store, AppConfig cfg, string dir)
    {
        _store = store;
        _cfg = cfg;
        _dir = dir;
        _statePath = Path.Combine(dir, "sync-state.json");
        _state = LoadState();

        // Пароль из конфига расшифровываем один раз и держим в памяти.
        if (string.IsNullOrEmpty(cfg.Sync.Password))
            cfg.Sync.Password = Secret.Unprotect(cfg.Sync.PasswordProtected) ?? "";
    }

    public SyncOptions Options => _cfg.Sync;

    public bool IsConfigured =>
        Options.Url.Trim().Length > 0 && Options.Login.Trim().Length > 0 && Options.HasPassword;

    public SyncStatusDto GetStatus()
    {
        bool pending;
        try { pending = ComputeLocalHash() != _state.LocalHash; }
        catch { pending = true; }

        return new SyncStatusDto
        {
            Configured = IsConfigured,
            Enabled = Options.Enabled,
            Url = Options.Url,
            Login = Options.Login,
            Folder = Options.Folder,
            OnStart = Options.OnStart,
            OnExit = Options.OnExit,
            HasPassword = Options.HasPassword,
            PendingChanges = pending,
            Running = _gate.CurrentCount == 0,
            Conflict = _state.Conflict,
            LastSyncAt = NullIfEmpty(_state.LastSyncAt),
            LastError = _state.LastError,
            LastAction = _state.LastAction,
            LastMessage = _state.LastMessage,
        };
    }

    public void SaveOptions(SyncConfigReq req)
    {
        var o = Options;

        if (req.Url is not null) o.Url = req.Url.Trim();
        if (req.Login is not null) o.Login = req.Login.Trim();
        if (req.Folder is not null) o.Folder = req.Folder.Trim().Trim('/', '\\');
        if (req.Enabled is bool enabled) o.Enabled = enabled;
        if (req.OnStart is bool onStart) o.OnStart = onStart;
        if (req.OnExit is bool onExit) o.OnExit = onExit;

        if (!string.IsNullOrEmpty(req.Password))
        {
            o.Password = req.Password;
            o.PasswordProtected = Secret.Protect(req.Password);
        }

        if (o.Folder.Length == 0) o.Folder = "Kanban";
        _cfg.Save(_dir);
    }

    /// <summary>Проверка подключения: создаёт папки и читает список файлов.</summary>
    public async Task<SyncRunResult> TestAsync(CancellationToken outer = default)
    {
        if (!IsConfigured) throw new SyncException("Заполните адрес, логин и пароль приложения");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(TimeSpan.FromSeconds(45));
        var ct = cts.Token;

        using var client = new WebDavClient(Options.Url, Options.Login, ResolvePassword(), Options.Folder);
        await client.EnsureFolderAsync(ct);
        var snapshot = await client.GetSnapshotAsync(ct);
        var files = await client.ListFilesAsync(ct);

        return new SyncRunResult
        {
            Action = "test",
            Message = snapshot is null
                ? $"Подключение работает. Папка «{Options.Folder}» готова, файла синхронизации на сервере пока нет."
                : $"Подключение работает. На сервере найден снимок, вложений: {files.Count}.",
            RemoteSize = snapshot?.Bytes.Length ?? 0,
            DownloadedFiles = files.Count,
        };
    }

    /// <summary>Основная операция. mode: auto | push | pull | both.</summary>
    public async Task<SyncRunResult> RunAsync(string mode = "auto", CancellationToken outer = default)
    {
        if (!await _gate.WaitAsync(0, outer))
            return new SyncRunResult { Action = "busy", Message = "Синхронизация уже выполняется" };

        try
        {
            if (!IsConfigured)
                return Error("Синхронизация не настроена: укажите адрес, логин и пароль приложения");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            cts.CancelAfter(TimeSpan.FromSeconds(180));
            var ct = cts.Token;

            using var client = new WebDavClient(Options.Url, Options.Login, ResolvePassword(), Options.Folder);
            await client.EnsureFolderAsync(ct);

            var local = BuildLocal();
            var remote = await FetchRemoteAsync(client, ct);

            bool hasState = !string.IsNullOrEmpty(_state.LocalHash) || !string.IsNullOrEmpty(_state.RemoteRevision);
            bool localChanged = local.Hash != _state.LocalHash;
            bool remoteChanged = remote is not null && remote.Revision != _state.RemoteRevision;

            string action;
            if (mode is "push" or "pull" or "both") action = mode;
            else if (remote is null) action = "push";
            else if (!hasState) action = "conflict"; // первый запуск, а на сервере уже есть данные
            else if (!localChanged && !remoteChanged) action = "none";
            else if (localChanged && !remoteChanged) action = "push";
            else if (!localChanged && remoteChanged) action = "pull";
            else action = "conflict";

            switch (action)
            {
                case "none":
                    return Success("none", "Изменений нет — данные уже совпадают с сервером");

                case "push":
                    return await PushAsync(client, local, remote, ct);

                case "pull":
                case "both":
                    if (remote is null) return Error("На сервере ещё нет данных для загрузки");
                    return await PullAsync(client, remote, ct, keepLocalCopy: action == "both");

                default:
                    _state.Conflict = true;
                    _state.ConflictAt = Now();
                    _state.LastAction = "conflict";
                    _state.LastMessage = "Данные менялись и здесь, и на сервере";
                    SaveState();
                    return new SyncRunResult
                    {
                        Action = "conflict",
                        Conflict = true,
                        Message = "Данные менялись и на этом ПК, и на сервере. Выберите, какую версию оставить.",
                        RemoteSize = remote?.Bytes.Length ?? 0,
                        RemoteDevice = remote?.Device,
                        RemoteCreatedAt = remote?.CreatedAt,
                    };
            }
        }
        catch (SyncException ex)
        {
            return Error(ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Error("Синхронизация прервана: сервер не ответил вовремя");
        }
        catch (Exception ex)
        {
            AppLog.Error("Синхронизация", ex);
            return Error("Синхронизация не выполнена: " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncRunResult> PushAsync(WebDavClient client, LocalSnapshot local, RemoteSnapshot? remote, CancellationToken ct)
    {
        string revision = Guid.NewGuid().ToString("N");
        byte[] payload = BuildEnvelope(local.State, revision);

        var remoteFiles = await client.ListFilesAsync(ct);
        int uploaded = 0;
        foreach (string name in local.FileNames)
        {
            if (remoteFiles.Contains(name)) continue;
            string path = Path.Combine(_store.FilesDir, name);
            if (!File.Exists(path)) continue;
            await client.PutFileAsync(name, await File.ReadAllBytesAsync(path, ct), ct);
            uploaded++;
        }

        try
        {
            await client.PutSnapshotAsync(payload, remote?.ETag, ct);
        }
        catch (SyncException ex) when (ex.Status == 412)
        {
            _state.Conflict = true;
            _state.ConflictAt = Now();
            SaveState();
            return new SyncRunResult
            {
                Action = "conflict",
                Conflict = true,
                Message = "Пока шла отправка, файл на сервере изменился. Выберите версию.",
            };
        }

        _state.LocalHash = local.Hash;
        _state.RemoteRevision = revision;
        _state.LastSyncAt = Now();
        _state.LastError = null;
        _state.LastAction = "push";
        _state.LastMessage = uploaded > 0 ? $"Данные и вложений: {uploaded}" : "Данные отправлены";
        _state.Conflict = false;
        _state.ConflictAt = null;
        SaveState();

        return Success("push", _state.LastMessage!, uploadedFiles: uploaded);
    }

    private async Task<SyncRunResult> PullAsync(WebDavClient client, RemoteSnapshot remote, CancellationToken ct, bool keepLocalCopy)
    {
        var dto = remote.State;
        if (dto.Boards.Count == 0) throw new SyncException("На сервере пустой снимок — загрузка отменена");

        Directory.CreateDirectory(_store.FilesDir);
        int downloaded = 0;

        foreach (var att in dto.Cards.SelectMany(c => c.Attachments).Where(a => a.Kind == "file"))
        {
            string name = Path.GetFileName(att.FileName ?? "");
            if (!IsSafeFileName(name))
                throw new SyncException("На сервере недопустимое имя файла вложения — синхронизация отменена");

            string localPath = Path.Combine(_store.FilesDir, name);
            if (File.Exists(localPath)) continue;

            byte[]? data = await client.GetFileAsync(name, ct);
            if (data is null)
                throw new SyncException($"На сервере нет файла вложения «{att.Title}» — синхронизация отменена");

            await File.WriteAllBytesAsync(localPath, data, ct);
            downloaded++;
        }

        if (keepLocalCopy) SaveLocalCopy();

        // Перед заменой базы — обычная резервная копия.
        _store.Backup();
        _store.ImportJson(dto, replaceFiles: true, sourceFilesDir: _store.FilesDir, preserveFileNames: true);

        _state.LocalHash = ComputeLocalHash();
        _state.RemoteRevision = remote.Revision;
        _state.LastSyncAt = Now();
        _state.LastError = null;
        _state.LastAction = keepLocalCopy ? "both" : "pull";
        _state.LastMessage = downloaded > 0 ? $"Данные и вложений: {downloaded}" : "Данные получены";
        _state.Conflict = false;
        _state.ConflictAt = null;
        SaveState();

        return Success(keepLocalCopy ? "both" : "pull",
            keepLocalCopy ? "Загружена версия с сервера, своя сохранена в backups" : _state.LastMessage!,
            downloadedFiles: downloaded);
    }

    private async Task<RemoteSnapshot?> FetchRemoteAsync(WebDavClient client, CancellationToken ct)
    {
        var raw = await client.GetSnapshotAsync(ct);
        if (raw is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(raw.Bytes);
            var root = doc.RootElement;
            JsonElement stateEl = root;
            string revision = "", device = "", createdAt = "";

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                stateEl = s;
                revision = GetString(root, "revision");
                device = GetString(root, "device");
                createdAt = GetString(root, "createdAt");
            }

            var dto = stateEl.Deserialize<StateDto>(Json);
            if (dto is null) throw new SyncException("Файл kanban.json на сервере пуст");

            dto.Boards ??= new();
            dto.Columns ??= new();
            dto.Cards ??= new();
            foreach (var card in dto.Cards)
            {
                card.Checklist ??= new();
                card.Attachments ??= new();
            }

            if (string.IsNullOrEmpty(revision))
                revision = "legacy-" + Convert.ToHexString(SHA256.HashData(raw.Bytes))[..16];

            raw.Revision = revision;
            raw.Device = device;
            raw.CreatedAt = createdAt;
            raw.State = dto;
            return raw;
        }
        catch (JsonException ex)
        {
            throw new SyncException("Файл kanban.json на сервере повреждён: " + ex.Message);
        }
    }

    private LocalSnapshot BuildLocal()
    {
        var state = _store.GetState();
        var names = new List<string>();

        foreach (var card in state.Cards)
            foreach (var att in card.Attachments)
            {
                att.DownloadUrl = null; // служебное поле, в снимок и хеш не входит
                if (att.Kind != "file" || string.IsNullOrEmpty(att.FileName)) continue;
                string name = Path.GetFileName(att.FileName);
                if (IsSafeFileName(name)) names.Add(name);
            }

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        string hash = Convert.ToHexString(SHA256.HashData(json));
        return new LocalSnapshot { Hash = hash, State = state, FileNames = names.Distinct().ToList() };
    }

    private string ComputeLocalHash() => BuildLocal().Hash;

    private byte[] BuildEnvelope(StateDto state, string revision)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["format"] = "kanban-sync",
            ["formatVersion"] = 1,
            ["revision"] = revision,
            ["createdAt"] = DateTime.UtcNow.ToString("O"),
            ["appVersion"] = Program.AppVersion,
            ["device"] = Environment.MachineName,
            ["state"] = state,
        };
        return JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
    }

    private void SaveLocalCopy()
    {
        try
        {
            Directory.CreateDirectory(_store.BackupDir);
            string name = $"kanban-local-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            File.WriteAllText(Path.Combine(_store.BackupDir, name), _store.ExportJson(), new UTF8Encoding(false));
        }
        catch (Exception ex) { AppLog.Error("Сохранение локальной копии перед синхронизацией", ex); }
    }

    private string ResolvePassword()
    {
        if (!string.IsNullOrEmpty(Options.Password)) return Options.Password;

        string? saved = Secret.Unprotect(Options.PasswordProtected);
        if (string.IsNullOrEmpty(saved))
            throw new SyncException("Пароль приложения не сохранён на этом ПК — введите его в настройках синхронизации");
        Options.Password = saved;
        return saved;
    }

    private SyncRunResult Success(string action, string message, int uploadedFiles = 0, int downloadedFiles = 0) =>
        new()
        {
            Action = action,
            Message = message,
            UploadedFiles = uploadedFiles,
            DownloadedFiles = downloadedFiles,
        };

    private SyncRunResult Error(string message)
    {
        _state.LastError = message;
        _state.LastAction = "error";
        _state.LastMessage = message;
        SaveState();
        return new SyncRunResult { Ok = false, Action = "error", Error = message, Message = message };
    }

    private SyncState LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return new SyncState();
            return JsonSerializer.Deserialize<SyncState>(File.ReadAllText(_statePath), Json) ?? new SyncState();
        }
        catch (Exception ex)
        {
            AppLog.Error("Чтение sync-state.json", ex);
            return new SyncState();
        }
    }

    private void SaveState()
    {
        try
        {
            File.WriteAllText(_statePath,
                JsonSerializer.Serialize(_state, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                }),
                new UTF8Encoding(false));
        }
        catch (Exception ex) { AppLog.Error("Запись sync-state.json", ex); }
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static bool IsSafeFileName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 120) return false;
        if (name is "." or "..") return false;
        if (name.Contains('/') || name.Contains('\\') || name.Contains(':')) return false;
        return name.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-');
    }

    private sealed class LocalSnapshot
    {
        public string Hash { get; init; } = "";
        public StateDto State { get; init; } = new();
        public List<string> FileNames { get; init; } = new();
    }
}
