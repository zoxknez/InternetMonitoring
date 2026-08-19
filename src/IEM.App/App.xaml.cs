using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using IEM.App.Hosting;
using IEM.App.ViewModels;
using IEM.Core.Presentation;
using IEM.Presentation.Hosting;
using IEM.Storage;

namespace IEM.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private ShellViewModel? _shell;

    protected override void OnStartup(StartupEventArgs e)
    {
        InstallCrashHandlers();

        // Serbian regardless of the language of the user's Windows. Left to the system,
        // dates in the window would come out as 8/13/2026 with a decimal point on an
        // English install, and the window would disagree with the report beside it.
        CultureInfo.DefaultThreadCurrentCulture = SerbianText.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = SerbianText.Culture;

        // WPF ignores the thread culture when formatting bindings unless told otherwise.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(SerbianText.Culture.IetfLanguageTag)));

        if (!ClaimSingleInstance())
        {
            // A second window would poll the same service and start a competing in-process
            // session in the same folder. One at a time.
            MessageBox.Show(
                "Monitor internet dokaza je već pokrenut.",
                "Monitor internet dokaza",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown();
            return;
        }

        base.OnStartup(e);

        var probe = new IEM.Windows.WindowsInstallationProbe();
        var storageLayout = new IEM.Windows.Storage.WindowsStorageLayout();
        var installState = probe.Probe();

        IMonitorHost host;
        string outputRoot;

        if (installState.Presence == InstallationPresence.InstalledSystemService)
        {
            outputRoot = storageLayout.DefaultOutputRoot;
            if (installState.Reachability == ServiceReachability.Reachable)
            {
                host = new ServiceMonitorHost(outputRoot);
            }
            else
            {
                // Invariant 276: INSTALLED_UNREACHABLE_NEVER_FALLS_BACK_TO_PORTABLE
                host = new ServiceUnavailableMonitorHost(
                    outputRoot,
                    "Windows servis je instaliran, ali trenutno nije dostupan. Pokrenite servis pre početka nadzora.");
            }
        }
        else if (installState.Presence == InstallationPresence.PortableOnly)
        {
            outputRoot = storageLayout.PortableOutputRoot;
            host = new InProcessMonitorHost(outputRoot, IEM.Windows.WindowsProbeFactory.Instance);
        }
        else
        {
            outputRoot = storageLayout.DefaultOutputRoot;
            host = new ServiceUnavailableMonitorHost(
                outputRoot,
                "Stanje instalacije servisa nije moguće pouzdano utvrditi.");
        }

        MainWindow? window = null;

        _shell = new ShellViewModel(host, outputRoot)
        {
            // The dialog is owned by the window, so it centres on it and cannot be lost behind
            // it. Resolved lazily because the window does not exist yet at this line.
            StopPromptAsked = (monitored, incidents) =>
            {
                var dialog = new StopWindow(monitored, incidents)
                {
                    Owner = window is { IsVisible: true } visible ? visible : null,
                };

                dialog.ShowDialog();
                return dialog.Choice;
            },
        };

        window = new MainWindow { DataContext = _shell };
        MainWindow = window;
        window.Show();

        _ = _shell.ConnectAsync();
    }

    /// <summary>
    /// Catches what would otherwise be a silent death.
    /// <para>
    /// Added after a failure in the tray icon - a decorative detail - took the whole
    /// process down with no message and no log, on a background continuation nothing was
    /// watching. For an application someone may have left running for two days, vanishing
    /// without explanation is the worst possible behaviour: the evidence on disk is intact
    /// and its owner has no way of knowing that.
    /// </para>
    /// </summary>
    private static void InstallCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Report(args.ExceptionObject as Exception, fatal: true);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            Report(args.Exception, fatal: false);
        };

        Current.DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            Report(args.Exception, fatal: false);
        };
    }

    private static void Report(Exception? exception, bool fatal)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            var log = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "InternetEvidenceMonitor",
                "greske.log");

            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"{DateTimeOffset.Now:O}  {exception}{Environment.NewLine}{Environment.NewLine}");

            if (fatal)
            {
                MessageBox.Show(
                    "Došlo je do neočekivane greške i aplikacija se zatvara.\n\n" +
                    "Ako je nadzor radio kao Windows servis, on nastavlja da radi i podaci nisu izgubljeni.\n\n" +
                    $"Detalji su upisani u:\n{log}",
                    "Monitor internet dokaza",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If even writing the log fails there is nothing further worth attempting.
        }
    }

    private bool ClaimSingleInstance()
    {
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\InternetEvidenceMonitor.App", out var created);
        return created;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_shell is not null)
        {
            // Blocking here is deliberate: an in-process session must be closed off and its
            // report written before the process goes away, or the evidence is left half-open.
            //
            // Bounded, also deliberately. Disposal includes a poller that may be sitting in a
            // pipe read the other end has gone quiet on, and waiting for that indefinitely
            // froze the whole application on exit - no window, tray icon still clickable, the
            // process refusing to leave. A session abandoned by the timeout stays open on
            // disk and resumes by design; a frozen exit has no such recovery.
            var disposal = _shell.DisposeAsync().AsTask();

            // A frame rather than a blocking wait: the dispatcher keeps pumping while
            // disposal runs, so everything queued behind the exit - chiefly the tray icon's
            // removal - happens at once instead of after the wait. A blocked dispatcher left
            // the icon sitting in the notification area for the whole exit, menu and all,
            // looking exactly like the freeze this bounded wait was meant to prevent.
            var frame = new System.Windows.Threading.DispatcherFrame();

            _ = Task.Run(() =>
            {
                try
                {
                    disposal.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    // Neither a timeout nor a disposal failure may hold the exit hostage;
                    // an abandoned session stays open on disk and resumes by design.
                }

                frame.Continue = false;
            });

            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
