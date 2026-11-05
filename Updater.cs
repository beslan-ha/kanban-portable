using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kanban;

public sealed class UpdateInfo
{
    public string CurrentVersion { get; set; } = Program.AppVersion;
    public bool Available { get; set; }
    public string? Version { get; set; }
    public string? Notes { get; set; }
    public string? FileName { get; set; }
    public long Size { get; set; }
    public string UpdateDir { get; set; } = "";
    public string? Error { get; set; }
    /// <summary>Откуда обновление: "folder" (updates\) или "github".</summary>
    public string? Source { get; set; }
    public string? DownloadUrl { get; set; }
    public string? Sha256 { get; set; }
    public string? ReleaseUrl { get; set; }
    public string? Tag { get; set; }
}

/// <summary>
/// Обновления из двух источников: папка updates\ рядом с exe (офлайн) и GitHub Releases (онлайн).
/// Онлайн-проверка кэшируется, чтобы не тратить лимит GitHub API.
/// </summary>
internal static class Updater
{
    private const string ExeName = "Kanban.exe";

    private static readonly HttpClient Http = CreateClient();
    private static readonly object CacheLock = new();
    private static DateTimeOffset _lastCheck = DateTimeOffset.MinValue;
    private static UpdateInfo? _cached;
    private static string? _etag;
    private static string? _statePath;
    private static string? _stateRepo;
    private static DateTimeOffset _errorAt = DateTimeOffset.MinValue;
    private static UpdateInfo? _errorInfo;

    public static string Dir(string appDir) => Path.Combine(appDir, "updates");

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kanban/" + Program.AppVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Проверяет папку updates и GitHub. Папка имеет приоритет (работает без интернета).</summary>
    public static async Task<UpdateInfo> CheckAsync(string appDir, AppConfig cfg, bool force = false,
        CancellationToken ct = default)
    {
        UpdateInfo folder = CheckFolder(appDir);
        if (folder.Available) return folder;

        if (!cfg.CheckUpdatesOnline) return folder;

        UpdateInfo github = await CheckGitHubAsync(appDir, cfg, force, ct);
        if (github.Available) return github;

        // Онлайн-проверка без ошибки: показываем её результат («последняя версия»).
        if (string.IsNullOrEmpty(github.Error)) return github;

        // Ошибку сети показываем, только если в папке нет своего (возможно, битого) манифеста.
        return string.IsNullOrEmpty(folder.Error) ? github : folder;
    }

    /// <summary>Офлайн-обновление из updates\version.json.</summary>
    public static UpdateInfo CheckFolder(string appDir)
    {
        var info = new UpdateInfo { Source = "folder", UpdateDir = Dir(appDir) };
        string manifest = Path.Combine(info.UpdateDir, "version.json");
        if (!File.Exists(manifest)) return info;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            var root = doc.RootElement;
            string? version = GetString(root, "version");
            if (string.IsNullOrWhiteSpace(version))
            {
                info.Error = "В version.json не указана версия";
                return info;
            }

            info.Version = version.Trim();
            info.Notes = GetString(root, "notes");
            info.FileName = GetString(root, "file");

            if (string.IsNullOrWhiteSpace(info.FileName))
                info.FileName = $"Kanban-{info.Version}.exe";

            string exePath = Path.Combine(info.UpdateDir, Path.GetFileName(info.FileName));
            if (File.Exists(exePath)) info.Size = new FileInfo(exePath).Length;

            int cmp = CompareVersions(info.Version, Program.AppVersion);
            info.Available = cmp > 0 && File.Exists(exePath);
            info.Source = "folder";

            if (cmp > 0 && !File.Exists(exePath))
                info.Error = $"В папке updates нет файла «{Path.GetFileName(info.FileName)}»";
        }
        catch (Exception ex)
        {
            info.Error = "Не удалось прочитать updates\\version.json: " + ex.Message;
            AppLog.Error("Проверка обновлений в папке", ex);
        }

        return info;
    }

    /// <summary>Последний релиз на GitHub; результат кэшируется на UpdateCheckMinutes.</summary>
    private static async Task<UpdateInfo> CheckGitHubAsync(string appDir, AppConfig cfg, bool force, CancellationToken ct)
    {
        var info = new UpdateInfo { Source = "github" };

        string repo = (cfg.UpdateRepo ?? "").Trim().Trim('/');
        if (repo.Length == 0)
        {
            info.Error = "Не указан репозиторий обновлений (updateRepo)";
            return info;
        }

        EnsureStateLoaded(appDir, repo);

        int minutes = Math.Max(1, cfg.UpdateCheckMinutes);
        lock (CacheLock)
        {
            if (!force && _cached is not null && DateTimeOffset.UtcNow - _lastCheck < TimeSpan.FromMinutes(minutes))
                return _cached;

            // Ошибка сети: не дёргаем GitHub чаще, чем раз в 5 минут.
            if (!force && _errorInfo is not null && DateTimeOffset.UtcNow - _errorAt < TimeSpan.FromMinutes(5))
                return _errorInfo;
        }

        string apiBase = (cfg.UpdateApiBase ?? "https://api.github.com").TrimEnd('/');
        string url = $"{apiBase}/repos/{repo}/releases/latest";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);

            string? etag;
            lock (CacheLock) etag = _etag;
            if (!force && !string.IsNullOrEmpty(etag))
                req.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.StatusCode == HttpStatusCode.NotModified)
            {
                UpdateInfo? cached;
                lock (CacheLock) cached = _cached;

                if (cached is not null)
                {
                    CacheSuccess(appDir, repo, cached);
                    return cached;
                }
            }

            if (!res.IsSuccessStatusCode)
            {
                info.Error = $"GitHub вернул HTTP {(int)res.StatusCode} при проверке обновлений";
                RememberError(info);
                return info;
            }

            string json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? newEtag = res.Headers.ETag?.Tag;
            lock (CacheLock) _etag = newEtag;

            string tag = GetString(root, "tag_name") ?? "";
            info.Tag = tag;
            info.Version = tag.TrimStart('v', 'V');
            info.Notes = Shorten(GetString(root, "body"), 220);
            info.ReleaseUrl = GetString(root, "html_url");

            if (string.IsNullOrEmpty(info.Version) ||
                CompareVersions(info.Version, Program.AppVersion) <= 0)
            {
                CacheSuccess(appDir, repo, info);
                return info;
            }

            // Для полной версии берём Kanban-*, для Basic — Kanban-Basic-*.
            string prefix = Program.Edition == "basic" ? "Kanban-Basic-" : "Kanban-";
            JsonElement? chosen = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = GetString(asset, "name") ?? "";
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Program.Edition != "basic" && name.StartsWith("Kanban-Basic-", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    chosen = asset;
                    break;
                }
            }

            if (chosen is null)
            {
                info.Error = $"В релизе {tag} не найден архив обновления для этой версии";
                CacheSuccess(appDir, repo, info);
                return info;
            }

            var a = chosen.Value;
            info.FileName = GetString(a, "name");
            info.DownloadUrl = GetString(a, "browser_download_url");
            info.Size = a.TryGetProperty("size", out var size) && size.TryGetInt64(out long s) ? s : 0;

            string? digest = GetString(a, "digest");
            if (!string.IsNullOrEmpty(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                info.Sha256 = digest["sha256:".Length..];

            info.Available = !string.IsNullOrEmpty(info.DownloadUrl);
            if (!info.Available) info.Error = "У релиза нет ссылки на скачивание";

            CacheSuccess(appDir, repo, info);
            return info;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            info.Error = "GitHub не ответил вовремя";
            RememberError(info);
            return info;
        }
        catch (Exception ex)
        {
            info.Error = "Не удалось проверить обновления на GitHub: " + ex.Message;
            AppLog.Info("Проверка обновлений на GitHub: " + ex.Message);
            RememberError(info);
            return info;
        }
    }

    /// <summary>Сохраняет успешный результат проверки, чтобы пережить перезапуск.</summary>
    private static void CacheSuccess(string appDir, string repo, UpdateInfo info)
    {
        lock (CacheLock)
        {
            _lastCheck = DateTimeOffset.UtcNow;
            _cached = info;
            _stateRepo = repo;
            _errorInfo = null;
            _errorAt = DateTimeOffset.MinValue;
            SaveStateLocked(appDir);
        }
    }

    private static void RememberError(UpdateInfo info)
    {
        lock (CacheLock)
        {
            _errorAt = DateTimeOffset.UtcNow;
            _errorInfo = info;
        }
    }

    private static void EnsureStateLoaded(string appDir, string repo)
    {
        string path = Path.Combine(appDir, "update-state.json");
        lock (CacheLock)
        {
            if (string.Equals(_statePath, path, StringComparison.OrdinalIgnoreCase)) return;

            _statePath = path;
            _cached = null;
            _lastCheck = DateTimeOffset.MinValue;
            _etag = null;

            try
            {
                if (!File.Exists(path)) return;

                var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (state is null || state.Info is null) return;
                if (!string.Equals(state.Repo, repo, StringComparison.OrdinalIgnoreCase)) return;

                var info = state.Info;
                info.CurrentVersion = Program.AppVersion;
                if (!string.IsNullOrEmpty(info.Version))
                    info.Available = CompareVersions(info.Version, Program.AppVersion) > 0 &&
                                     !string.IsNullOrEmpty(info.DownloadUrl);

                _cached = info;
                _lastCheck = state.LastCheckUtc;
                _etag = state.ETag;
                _stateRepo = state.Repo;
            }
            catch (Exception ex) { AppLog.Info("Чтение update-state.json: " + ex.Message); }
        }
    }

    private static void SaveStateLocked(string appDir)
    {
        try
        {
            var state = new PersistedState
            {
                LastCheckUtc = _lastCheck,
                Repo = _stateRepo,
                ETag = _etag,
                Info = _cached,
            };

            File.WriteAllText(Path.Combine(appDir, "update-state.json"),
                JsonSerializer.Serialize(state, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                }),
                new UTF8Encoding(false));
        }
        catch (Exception ex) { AppLog.Info("Запись update-state.json: " + ex.Message); }
    }

    private sealed class PersistedState
    {
        public DateTimeOffset LastCheckUtc { get; set; }
        public string? Repo { get; set; }
        public string? ETag { get; set; }
        public UpdateInfo? Info { get; set; }
    }

    /// <summary>Скачивает и устанавливает обновление, затем вызывает quit.</summary>
    public static async Task ApplyAsync(string appDir, UpdateInfo info, Action quit, CancellationToken ct = default)
    {
        if (!info.Available) throw new AppException(400, "Новое обновление не найдено");

        if (string.Equals(info.Source, "github", StringComparison.OrdinalIgnoreCase))
        {
            await DownloadAndStageAsync(appDir, info, ct);
        }
        else
        {
            if (string.IsNullOrEmpty(info.FileName))
                throw new AppException(400, "В version.json не указан файл обновления");

            string source = Path.Combine(info.UpdateDir, Path.GetFileName(info.FileName));
            if (!File.Exists(source)) throw new AppException(404, "Файл обновления не найден");
            File.Copy(source, Path.Combine(appDir, "Kanban.update.exe"), true);
        }

        WriteScript(appDir);

        AppLog.Info($"Обновление {Program.AppVersion} → {info.Version} ({info.Source}): Kanban.update.exe");

        Process.Start(new ProcessStartInfo("cmd.exe", "/c \"\"update.cmd\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = appDir,
        });

        quit();
    }

    private static async Task DownloadAndStageAsync(string appDir, UpdateInfo info, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl)) throw new AppException(400, "Нет ссылки на файл обновления");

        string zipPath = Path.Combine(appDir, "Kanban.download.zip");
        try
        {
            using (var res = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!res.IsSuccessStatusCode)
                    throw new AppException(502, $"Не удалось скачать обновление (HTTP {(int)res.StatusCode})");

                await using var source = await res.Content.ReadAsStreamAsync(ct);
                await using var target = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(target, ct);
            }

            var file = new FileInfo(zipPath);
            if (info.Size > 0 && file.Length != info.Size)
                throw new AppException(502, "Размер скачанного файла не совпадает с ожидаемым");

            if (!string.IsNullOrEmpty(info.Sha256))
            {
                string hash = Convert.ToHexString(await ComputeSha256Async(zipPath, ct));
                if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new AppException(502, "Контрольная сумма скачанного файла не совпала");
            }

            using var zip = ZipFile.OpenRead(zipPath);
            Extract(zip, ExeName, Path.Combine(appDir, "Kanban.update.exe"));

            // DLL необязательны: в старых релизах их могло не быть в архиве.
            Extract(zip, "e_sqlite3.dll", Path.Combine(appDir, "e_sqlite3.update.dll"), required: false);
            Extract(zip, "vcruntime140_cor3.dll", Path.Combine(appDir, "vcruntime140_cor3.update.dll"), required: false);
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }
    }

    private static void Extract(ZipArchive zip, string entryName, string target, bool required = true)
    {
        var entry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), entryName, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            if (required) throw new AppException(502, $"В архиве обновления нет файла {entryName}");
            return;
        }

        entry.ExtractToFile(target, true);
    }

    private static async Task<byte[]> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await SHA256.HashDataAsync(stream, ct);
    }

    /// <summary>Убирает следы прошлого обновления при старте.</summary>
    public static void Cleanup(string appDir)
    {
        try
        {
            foreach (string path in new[]
                     {
                         Path.Combine(appDir, "Kanban.update.exe"),
                         Path.Combine(appDir, "e_sqlite3.update.dll"),
                         Path.Combine(appDir, "vcruntime140_cor3.update.dll"),
                         Path.Combine(appDir, "Kanban.download.zip"),
                         Path.Combine(appDir, "update.cmd"),
                         Path.Combine(appDir, "Kanban.exe.old"),
                     })
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (IOException) { /* файл ещё держит прошлый процесс — удалится в следующий раз */ }
            }
        }
        catch (Exception ex) { AppLog.Error("Очистка файлов обновления", ex); }
    }

    private static void WriteScript(string appDir)
    {
        string script = Path.Combine(appDir, "update.cmd");

        // ping вместо timeout: timeout не работает, когда у cmd нет консоли.
        // Ждём, пока приложение отпустит exe, затем подменяем файлы и запускаем заново.
        string content = """
            @echo off
            setlocal
            cd /d "%~dp0"
            set /a tries=0
            :loop
            ping -n 2 127.0.0.1 >nul
            move /y "Kanban.update.exe" "Kanban.exe" >nul 2>&1
            if not errorlevel 1 goto done
            set /a tries+=1
            if %tries% lss 60 goto loop
            exit /b 1
            :done
            if exist "e_sqlite3.update.dll" move /y "e_sqlite3.update.dll" "e_sqlite3.dll" >nul 2>&1
            if exist "vcruntime140_cor3.update.dll" move /y "vcruntime140_cor3.update.dll" "vcruntime140_cor3.dll" >nul 2>&1
            start "" "Kanban.exe"
            (goto) 2>nul & del "%~f0"
            """;

        File.WriteAllText(script, content, new UTF8Encoding(false));
    }

    private static string? Shorten(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string s = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Сравнивает версии вида 1.2.3 (лишние части и суффиксы игнорируются).</summary>
    public static int CompareVersions(string a, string b)
    {
        int[] pa = Parse(a), pb = Parse(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = i < pa.Length ? pa[i] : 0;
            int y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static int[] Parse(string version)
    {
        var parts = new List<int>();
        foreach (string chunk in (version ?? "").Split('.'))
        {
            var digits = new StringBuilder();
            foreach (char ch in chunk)
            {
                if (char.IsDigit(ch)) digits.Append(ch);
                else break;
            }

            parts.Add(int.TryParse(digits.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0);
            if (parts.Count >= 4) break;
        }
        return parts.ToArray();
    }
}
