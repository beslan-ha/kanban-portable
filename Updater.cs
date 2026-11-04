using System.Diagnostics;
using System.Globalization;
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
}

/// <summary>
/// Обновление «из папки»: рядом с exe лежит папка updates с version.json и новым Kanban.exe.
/// Интернет не нужен — файл обновления приносит пользователь.
/// </summary>
internal static class Updater
{
    public static string Dir(string appDir) => Path.Combine(appDir, "updates");

    /// <summary>Читает updates\version.json и сравнивает версию с текущей.</summary>
    public static UpdateInfo Check(string appDir)
    {
        var info = new UpdateInfo { CurrentVersion = Program.AppVersion, UpdateDir = Dir(appDir) };
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

            if (cmp > 0 && !File.Exists(exePath))
                info.Error = $"В папке updates нет файла «{Path.GetFileName(info.FileName)}»";
        }
        catch (Exception ex)
        {
            info.Error = "Не удалось прочитать updates\\version.json: " + ex.Message;
            AppLog.Error("Проверка обновлений", ex);
        }

        return info;
    }

    /// <summary>
    /// Кладёт новый exe рядом и запускает отдельный cmd, который дождётся выхода приложения,
    /// подменит файл и снова запустит Kanban. Затем вызывается quit — приложение закрывается.
    /// </summary>
    public static void Apply(string appDir, UpdateInfo info, Action quit)
    {
        if (!info.Available) throw new AppException(400, "Новое обновление не найдено");
        if (string.IsNullOrEmpty(info.FileName)) throw new AppException(400, "В version.json не указан файл обновления");

        string source = Path.Combine(info.UpdateDir, Path.GetFileName(info.FileName));
        if (!File.Exists(source)) throw new AppException(404, "Файл обновления не найден");

        string exePath = Environment.ProcessPath ?? Path.Combine(appDir, "Kanban.exe");
        if (!File.Exists(exePath)) throw new AppException(500, "Не удалось определить путь к Kanban.exe");

        string exeName = Path.GetFileName(exePath);
        string tempName = Path.GetFileNameWithoutExtension(exeName) + ".update.exe";
        string tempPath = Path.Combine(appDir, tempName);

        File.Copy(source, tempPath, true);

        string script = Path.Combine(appDir, "update.cmd");
        File.WriteAllText(script, BuildScript(tempName, exeName), new UTF8Encoding(false));

        AppLog.Info($"Обновление {Program.AppVersion} → {info.Version}: {tempName}");

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = appDir,
        });

        quit();
    }

    /// <summary>Убирает следы прошлого обновления при старте.</summary>
    public static void Cleanup(string appDir)
    {
        try
        {
            string exeName = Path.GetFileName(Environment.ProcessPath ?? "Kanban.exe");
            string tempName = Path.GetFileNameWithoutExtension(exeName) + ".update.exe";

            foreach (string path in new[]
                     {
                         Path.Combine(appDir, tempName),
                         Path.Combine(appDir, "update.cmd"),
                         Path.Combine(appDir, exeName + ".old"),
                     })
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (IOException) { /* файл ещё держит прошлый процесс — удалится в следующий раз */ }
            }
        }
        catch (Exception ex) { AppLog.Error("Очистка файлов обновления", ex); }
    }

    private static string BuildScript(string tempName, string exeName)
    {
        // ping вместо timeout: timeout не работает, когда у cmd нет консоли.
        // Подменяем файл, пока он занят, с повторами — приложение закрывается не мгновенно.
        return $"""
            @echo off
            setlocal
            cd /d "%~dp0"
            set /a tries=0
            :loop
            ping -n 2 127.0.0.1 >nul
            move /y "{tempName}" "{exeName}" >nul 2>&1
            if not errorlevel 1 goto done
            set /a tries+=1
            if %tries% lss 40 goto loop
            exit /b 1
            :done
            start "" "{exeName}"
            (goto) 2>nul & del "%~f0"
            """;
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
