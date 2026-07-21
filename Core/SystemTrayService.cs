using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace KiloviewSetup.Core;

public sealed class SystemTrayService(
    IHostApplicationLifetime applicationLifetime,
    IWebHostEnvironment environment,
    ILogger<SystemTrayService> logger) : IHostedService, IDisposable
{
    private readonly ManualResetEventSlim _initialized = new(false);
    private Thread? _trayThread;
    private SynchronizationContext? _traySynchronizationContext;
    private Exception? _startupException;
    private int _restartRequested;

    public bool RestartRequested => Volatile.Read(ref _restartRequested) == 1;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;

        _trayThread = new Thread(RunTray)
        {
            IsBackground = true,
            Name = "Kiloview Job Configurator tray"
        };
        _trayThread.SetApartmentState(ApartmentState.STA);
        _trayThread.Start();

        if (!_initialized.Wait(TimeSpan.FromSeconds(5), cancellationToken))
            throw new InvalidOperationException("The Kiloview Job Configurator tray icon did not start.");
        if (_startupException is not null)
            throw new InvalidOperationException("The Kiloview Job Configurator tray icon could not start.", _startupException);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _traySynchronizationContext?.Post(_ => Application.ExitThread(), null);
        if (_trayThread is { IsAlive: true } && !_trayThread.Join(TimeSpan.FromSeconds(5)))
            logger.LogWarning("The system tray thread did not stop within five seconds.");
        return Task.CompletedTask;
    }

    private void RunTray()
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            _traySynchronizationContext = SynchronizationContext.Current;

            using var icon = LoadApplicationIcon();
            using var menu = new ContextMenuStrip();
            var openItem = new ToolStripMenuItem("Open Web UI")
            {
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            var restartItem = new ToolStripMenuItem("Restart");
            var exitItem = new ToolStripMenuItem("Exit");
            openItem.Click += (_, _) => OpenWebUi();
            restartItem.Click += (_, _) => RequestStop(restart: true);
            exitItem.Click += (_, _) => RequestStop(restart: false);
            menu.Items.Add(openItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(restartItem);
            menu.Items.Add(exitItem);

            using var notifyIcon = new NotifyIcon
            {
                Icon = icon,
                Text = $"Kiloview Job Configurator v{CurrentVersion}",
                ContextMenuStrip = menu,
                Visible = true
            };
            notifyIcon.DoubleClick += (_, _) => OpenWebUi();

            _initialized.Set();
            Application.Run();
            notifyIcon.Visible = false;
        }
        catch (Exception exception)
        {
            _startupException = exception;
            logger.LogError(exception, "The Kiloview Job Configurator tray icon failed.");
            _initialized.Set();
        }
    }

    private Icon LoadApplicationIcon()
    {
        var installedIcon = Path.Combine(environment.ContentRootPath, "KiloviewSetup.ico");
        if (File.Exists(installedIcon)) return new Icon(installedIcon);
        var sourceIcon = Path.Combine(environment.ContentRootPath, "wwwroot", "KiloviewSetup.ico");
        if (File.Exists(sourceIcon)) return new Icon(sourceIcon);
        var executableIcon = Environment.ProcessPath is { } executable
            ? Icon.ExtractAssociatedIcon(executable)
            : null;
        return executableIcon ?? (Icon)SystemIcons.Application.Clone();
    }

    private void OpenWebUi()
    {
        try
        {
            var servicePort = int.TryParse(Environment.GetEnvironmentVariable("KILOVIEW_SERVICE_PORT"), out var configuredPort)
                && configuredPort is >= 1024 and <= 65535 ? configuredPort : 8091;
            Process.Start(new ProcessStartInfo($"http://localhost:{servicePort}") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The default browser could not be opened.");
        }
    }

    private void RequestStop(bool restart)
    {
        if (restart) Interlocked.Exchange(ref _restartRequested, 1);
        applicationLifetime.StopApplication();
    }

    private static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

    public void Dispose()
    {
        _initialized.Dispose();
    }
}
