using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Kanban;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kanban;

internal static class Program
{
    public const string AppVersion = "1.3.4";

#if NOSYNC
    /// <summary>Издание: от него зависит, какой архив скачивать с GitHub.</summary>
    public const string Edition = "basic";
#else
    public const string Edition = "full";
#endif

    public static string AppDir { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    public static string LogPath { get; } = Path.Combine(AppContext.BaseDirectory, "kanban.log");

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            AppLog.Fatal("Запуск", ex);
            try
            {
                MessageBox.Show(
                    "Не удалось запустить Kanban.\n\n" + ex.Message + "\n\nПодробности в файле:\n" + LogPath,
                    "Kanban", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { /* нет интерактивного сеанса */ }
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var cfg = AppConfig.Load(AppDir);
        bool noBrowser = args.Any(a => a.Equals("--no-browser", StringComparison.OrdinalIgnoreCase)) || !cfg.OpenBrowser;

        // Один экземпляр на папку с данными: иначе два процесса будут писать в один файл БД.
        using var mutex = new Mutex(true, MutexName(AppDir), out bool firstInstance);
        if (!firstInstance)
        {
            FocusExistingInstance();
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }

        // Убираем хвосты прошлого обновления (Kanban.update.exe, update.cmd).
        Updater.Cleanup(AppDir);

        var store = new Store(AppDir) { BackupsToKeep = cfg.BackupsToKeep };
        store.Init();
#if !NOSYNC
        var sync = new SyncService(store, cfg, AppDir);
#endif

        if (cfg.BackupOnStart)
        {
            try { store.BackupIfOlderThan(TimeSpan.FromHours(23)); }
            catch (Exception ex) { AppLog.Error("Автоматическая резервная копия", ex); }
        }

        int port = FindFreePort(cfg.Port);
        string url = $"http://127.0.0.1:{port}/";

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppDir,
            EnvironmentName = Environments.Production,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(LogPath));
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, port);
            o.AddServerHeader = false;
        });
        builder.WebHost.UseSetting(WebHostDefaults.SuppressStatusMessagesKey, "true");

        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
            o.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        });

        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(cfg);

        var app = builder.Build();

        int quitRequested = 0;
#if NOSYNC
        Api.Map(app, store, cfg, port, () => Interlocked.Exchange(ref quitRequested, 1));
#else
        Api.Map(app, store, cfg, port, sync, () => Interlocked.Exchange(ref quitRequested, 1));
#endif

        bool stopped = false;
        void Quit()
        {
            if (stopped) return;
            stopped = true;
            try { RuntimeInfo.Delete(AppDir); } catch { }

#if !NOSYNC
            // Отправляем изменения на сервер перед выходом (если включено и сеть отвечает).
            if (cfg.Sync.Enabled && cfg.Sync.OnExit && sync.IsConfigured)
            {
                try
                {
                    // Важно: у UI-потока есть SynchronizationContext, поэтому синхронизацию
                    // запускаем в отдельном потоке и ждём здесь — иначе получим взаимоблокировку.
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                    var task = Task.Run(() => sync.RunAsync("auto", cts.Token));

                    if (task.Wait(TimeSpan.FromSeconds(9)))
                    {
                        var result = task.Result;
                        AppLog.Info($"Синхронизация при выходе: {result.Action} — {result.Message}");
                    }
                    else
                    {
                        AppLog.Info("Синхронизация при выходе не завершилась вовремя");
                    }
                }
                catch (Exception ex) { AppLog.Error("Синхронизация при выходе", ex); }
            }
#endif

            try { store.Checkpoint(); } catch { }

            // Останавливаем хост в отдельном потоке: у UI-потока есть SynchronizationContext,
            // и блокирующее ожидание StopAsync на нём привело бы к взаимной блокировке.
            var shutdown = new Thread(() =>
            {
                try { app.StopAsync(new CancellationTokenSource(2000).Token).GetAwaiter().GetResult(); } catch { }
                try { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            })
            { IsBackground = true, Name = "kanban-shutdown" };
            shutdown.Start();
            shutdown.Join(2500);

            Application.ExitThread();
        }

        app.Start();
        RuntimeInfo.Save(AppDir, port, Environment.ProcessId);
        AppLog.Info($"Kanban {AppVersion} запущен: {url} (папка: {AppDir})");

        if (!noBrowser) Shell.OpenUrl(url);

        TrayContext? tray = null;

        // Проверка обновлений при запуске — в фоне, чтобы окно открывалось сразу.
        if (cfg.CheckUpdatesOnStart)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var info = await Updater.CheckAsync(AppDir, cfg);
                    if (!string.IsNullOrEmpty(info.Error))
                    {
                        AppLog.Info("Проверка обновлений: " + info.Error);
                    }
                    else if (info.Available)
                    {
                        AppLog.Info($"Доступно обновление {info.Version} ({info.Source}); текущая {AppVersion}");

                        if (cfg.AutoApplyUpdates)
                        {
                            AppLog.Info("Автоматическая установка обновления " + info.Version);
                            await Updater.ApplyAsync(AppDir, info, () => Interlocked.Exchange(ref quitRequested, 1));
                        }
                    }
                }
                catch (Exception ex) { AppLog.Error("Проверка обновлений", ex); }
            });
        }

        tray = new TrayContext(
            url,
            onOpen: () => Shell.OpenUrl(url),
            onBackup: () =>
            {
                try
                {
                    string name = store.Backup();
                    tray?.Notify("Резервная копия создана", $"backups\\{name}");
                }
                catch (Exception ex)
                {
                    AppLog.Error("Резервная копия", ex);
                    tray?.Notify("Не удалось создать копию", ex.Message, ToolTipIcon.Error);
                }
            },
            onFolder: () => Shell.OpenFolder(AppDir),
#if NOSYNC
            onSync: null,
#else
            onSync: () =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var result = await sync.RunAsync("auto");
                        if (result.Conflict)
                            tray?.Notify("Синхронизация: конфликт", "Данные менялись на двух ПК. Откройте доску и выберите версию.", ToolTipIcon.Warning);
                        else if (!result.Ok)
                            tray?.Notify("Синхронизация не выполнена", result.Message ?? "", ToolTipIcon.Error);
                        else
                            tray?.Notify("Синхронизация", result.Message ?? "Готово", ToolTipIcon.Info);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("Синхронизация из трея", ex);
                        tray?.Notify("Синхронизация не выполнена", ex.Message, ToolTipIcon.Error);
                    }
                });
            },
#endif
            onUpdate: () =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var info = await Updater.CheckAsync(AppDir, cfg, force: true);

                        if (!string.IsNullOrEmpty(info.Error))
                        {
                            tray?.Notify("Обновление Kanban", info.Error, ToolTipIcon.Warning);
                            return;
                        }

                        if (!info.Available)
                        {
                            tray?.Notify("Обновление Kanban", $"Установлена последняя версия: {AppVersion}", ToolTipIcon.Info);
                            return;
                        }

                        tray?.Notify("Доступно обновление",
                            $"{info.Version}. Откройте доску — появится баннер «Обновить».", ToolTipIcon.Info);
                        Shell.OpenUrl(url);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("Проверка обновлений из трея", ex);
                        tray?.Notify("Обновление Kanban", ex.Message, ToolTipIcon.Error);
                    }
                });
            },
            onQuit: Quit);

#if !NOSYNC
        // Синхронизация при запуске — в фоне, чтобы окно и доска открывались сразу.
        if (cfg.Sync.Enabled && cfg.Sync.OnStart && sync.IsConfigured)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await sync.RunAsync("auto");
                    AppLog.Info($"Синхронизация при запуске: {result.Action} — {result.Message}");

                    if (result.Conflict)
                        tray?.Notify("Синхронизация: конфликт", "Данные менялись на двух ПК. Откройте доску и выберите версию.", ToolTipIcon.Warning);
                    else if (!result.Ok)
                        tray?.Notify("Синхронизация не выполнена", result.Message ?? "", ToolTipIcon.Error);
                }
                catch (Exception ex) { AppLog.Error("Синхронизация при запуске", ex); }
            });
        }
#endif

        // /api/quit приходит из HTTP-потока, поэтому выходим через таймер в UI-потоке.
        var quitTimer = new System.Windows.Forms.Timer { Interval = 150 };
        quitTimer.Tick += (_, _) =>
        {
            if (Interlocked.CompareExchange(ref quitRequested, 0, 0) != 1) return;
            quitTimer.Stop();
            quitTimer.Dispose();
            Quit();
        };
        quitTimer.Start();

        Application.Run(tray);

        RuntimeInfo.Delete(AppDir);
        AppLog.Info("Kanban остановлен");
        return 0;
    }

    private static string MutexName(string dir)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(dir.ToLowerInvariant()));
        return "Local\\KanbanPortable_" + Convert.ToHexString(hash)[..16];
    }

    private static void FocusExistingInstance()
    {
        for (int i = 0; i < 50; i++)
        {
            int? port = RuntimeInfo.TryGetPort(AppDir);
            if (port is int p && IsPortOpen(p))
            {
                Shell.OpenUrl($"http://127.0.0.1:{p}/");
                return;
            }
            Thread.Sleep(120);
        }

        MessageBox.Show(
            "Kanban уже запущен, но не удалось определить его порт.\n\n" +
            "Найдите значок приложения в области уведомлений (рядом с часами) или завершите процесс Kanban.exe.",
            "Kanban", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static bool IsPortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(IPAddress.Loopback, port).Wait(400) && client.Connected;
        }
        catch { return false; }
    }

    private static int FindFreePort(int start)
    {
        for (int p = start; p < start + 50; p++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, p);
                listener.Start();
                listener.Stop();
                return p;
            }
            catch (SocketException) { /* занят */ }
        }
        throw new InvalidOperationException($"Нет свободного порта в диапазоне {start}–{start + 49}.");
    }
}

internal static class Shell
{
    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Error("Открыть браузер", ex); }
    }

    public static void OpenFolder(string dir)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Error("Открыть папку", ex); }
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;

    public TrayContext(string url, Action onOpen, Action onBackup, Action? onSync, Action onFolder, Action onUpdate, Action onQuit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Открыть доску", null, (_, _) => onOpen()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        if (onSync is not null)
            menu.Items.Add(new ToolStripMenuItem("Синхронизировать", null, (_, _) => onSync()));
        menu.Items.Add(new ToolStripMenuItem("Создать резервную копию", null, (_, _) => onBackup()));
        menu.Items.Add(new ToolStripMenuItem("Открыть папку программы", null, (_, _) => onFolder()));
        menu.Items.Add(new ToolStripMenuItem("Проверить обновления", null, (_, _) => onUpdate()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => onQuit()));

        _icon = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = "Kanban — доска задач",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => onOpen();
        _icon.ShowBalloonTip(2500, "Kanban запущен", url + "\nЗакрыть — правый клик по значку → Выход.", ToolTipIcon.Info);
    }

    public void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
        => _icon.ShowBalloonTip(3000, title, text, icon);

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}

internal static class AppIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var bg = new SolidBrush(Color.FromArgb(24, 27, 34)))
            using (var path = Rounded(new RectangleF(0, 0, 32, 32), 7))
                g.FillPath(bg, path);

            var colors = new[]
            {
                Color.FromArgb(79, 140, 255),
                Color.FromArgb(255, 176, 32),
                Color.FromArgb(52, 211, 153),
            };
            for (int i = 0; i < 3; i++)
            {
                using var b = new SolidBrush(colors[i]);
                int x = 5 + i * 8;
                g.FillRectangle(b, x, 7, 5, 18);
                g.FillRectangle(b, x, 7, 5, 5);
            }
        }

        IntPtr handle = bmp.GetHicon();
        using var tmp = Icon.FromHandle(handle);
        return (Icon)tmp.Clone();
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
