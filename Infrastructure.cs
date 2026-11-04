using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Kanban;

/// <summary>Настройки рядом с exe. Файл необязательный: при отсутствии создаётся с умолчаниями.</summary>
public sealed class AppConfig
{
    public int Port { get; set; } = 5183;
    public bool OpenBrowser { get; set; } = true;
    public bool BackupOnStart { get; set; } = true;
    public int BackupsToKeep { get; set; } = 30;
    /// <summary>Проверять папку updates при запуске.</summary>
    public bool CheckUpdatesOnStart { get; set; } = true;
    /// <summary>Устанавливать найденное обновление без подтверждения.</summary>
    public bool AutoApplyUpdates { get; set; } = false;
#if !NOSYNC
    /// <summary>Синхронизация через WebDAV (Яндекс.Диск, Mail.ru, Nextcloud и т. п.).</summary>
    public SyncOptions Sync { get; set; } = new();
#endif

    public static string FilePath(string dir) => Path.Combine(dir, "kanban.config.json");

    public static AppConfig Load(string dir)
    {
        string path = FilePath(dir);
        var cfg = new AppConfig();

        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                if (loaded is not null) cfg = loaded;
            }
            catch (Exception ex)
            {
                AppLog.Error("Чтение kanban.config.json (используются умолчания)", ex);
            }
        }
        else
        {
            try { cfg.Save(dir); } catch { }
        }

        if (cfg.Port is < 1024 or > 65000) cfg.Port = 5183;
        if (cfg.BackupsToKeep < 3) cfg.BackupsToKeep = 3;
#if !NOSYNC
        cfg.Sync ??= new SyncOptions();
#endif
        return cfg;
    }

    public void Save(string dir)
    {
        try
        {
            File.WriteAllText(FilePath(dir), JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppLog.Error("Запись kanban.config.json", ex);
        }
    }
}

/// <summary>Сведения о работающем экземпляре — чтобы повторный запуск просто открыл вкладку.</summary>
internal static class RuntimeInfo
{
    private static string Path_(string dir) => Path.Combine(dir, "runtime.json");

    public static void Save(string dir, int port, int pid)
    {
        try
        {
            File.WriteAllText(Path_(dir), JsonSerializer.Serialize(new
            {
                pid,
                port,
                url = $"http://127.0.0.1:{port}/",
                startedAt = DateTime.Now.ToString("O"),
                version = Program.AppVersion,
            }), new UTF8Encoding(false));
        }
        catch (Exception ex) { AppLog.Error("runtime.json", ex); }
    }

    public static int? TryGetPort(string dir)
    {
        try
        {
            string path = Path_(dir);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("port", out var p) && p.TryGetInt32(out int port)) return port;
        }
        catch { }
        return null;
    }

    public static void Delete(string dir)
    {
        try { File.Delete(Path_(dir)); } catch { }
    }
}

/// <summary>Простой лог в файл рядом с exe (нужен для диагностики без консоли).</summary>
public static class AppLog
{
    private static readonly object Sync = new();
    private const long MaxSize = 1_000_000;

    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);
    public static void Fatal(string message, Exception? ex = null) => Write("FATAL", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Sync)
            {
                string path = Program.LogPath;
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxSize)
                {
                    string old = path + ".1";
                    try { File.Delete(old); } catch { }
                    try { File.Move(path, old); } catch { }
                }

                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" [").Append(level).Append("] ").Append(message);
                if (ex is not null) sb.Append(Environment.NewLine).Append(ex);
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch { /* логирование не должно ломать приложение */ }
    }
}

public sealed class FileLoggerProvider : ILoggerProvider
{
    public FileLoggerProvider(string path) { _ = path; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);
    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        public FileLogger(string category) => _category = category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (logLevel >= LogLevel.Error) AppLog.Error($"{_category}: {formatter(state, exception)}", exception);
            else AppLog.Info($"{_category}: {formatter(state, exception)}");
        }
    }
}
