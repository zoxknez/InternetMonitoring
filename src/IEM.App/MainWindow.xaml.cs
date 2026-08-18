using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using IEM.App.Controls;
using IEM.App.ViewModels;
using IEM.Core.Presentation;

using IEM.Core.Model;

namespace IEM.App;

public partial class MainWindow : Window
{
    private ShellViewModel? _shell;
    private bool _closeExplained;
    private bool _reallyExit;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellPropertyChanged;
            _shell.TrayNotification -= OnTrayNotification;
        }

        _shell = DataContext as ShellViewModel;

        if (_shell is not null)
        {
            _shell.PropertyChanged += OnShellPropertyChanged;
            _shell.TrayNotification += OnTrayNotification;
            Refresh();
        }
    }

    private void OnTrayNotification(string title, string body)
    {
        if (_closed)
        {
            return;
        }

        try
        {
            Tray.ShowNotification(title, body);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // The icon is already gone - the exit path disposes it. Not worth a crash over.
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    /// <summary>
    /// Applies the colours that depend on live state.
    /// <para>
    /// Done here rather than through converters in the markup because several elements
    /// take their colour from the same verdict, and keeping that in one method makes it
    /// impossible for the status card and the tray icon to drift apart.
    /// </para>
    /// </summary>
    private void Refresh()
    {
        if (_shell is null || _closed)
        {
            return;
        }

        var verdict = _shell.Verdict;

        // Three steps, not two. Coloured by "is it an outage" alone, a degraded state - a
        // resolver that will not answer, a filtered ping, latency through the roof - got the
        // same green tick as a connection with nothing wrong with it, directly above a
        // headline saying something did not work. The outage strip drew those same moments
        // amber, so the window disagreed with itself.
        var severity = _shell.CurrentSeverity;

        StateText.Foreground = Palette.ForSeverity(severity);
        StateGlyph.Stroke = Palette.ForSeverity(severity);

        // Shape as well as colour, so the tile still reads to someone who cannot separate
        // the two: a tick when everything answers, an exclamation when it does not.
        StateIcon.Background = severity switch
        {
            Severity.Outage => Frozen("#FCEBE9"),
            Severity.Degraded => Frozen("#FDF4E2"),
            _ => Frozen("#E8F6EE"),
        };

        StateGlyph.Data = Geometry.Parse(severity is Severity.Ok or Severity.Info
            ? "M 5,10.4 L 8.6,14 L 15,6.6"
            : "M 10,4.5 L 10,11.4 M 10,14.6 L 10,14.7");

        VerdictCard.Background = SoftFor(verdict.Kind);
        VerdictCard.BorderBrush = AccentFor(verdict.Kind);
        VerdictHeadline.Foreground = AccentFor(verdict.Kind);

        // The dot in the header takes its colour from the same place, so a glance at the
        // top of the window and a glance at the verdict cannot tell different stories.
        StatusDot.Fill = _shell.IsRunning
            ? _shell.IsOnline ? Palette.Ok : Palette.Outage
            : Palette.Neutral;

        // The previous icon owns a native handle; replacing it without disposing would leak
        // one on every state change, over a session that may run for days.
        var previousIcon = Tray.Icon;
        Tray.Icon = TrayIconFactory.Create(verdict.Kind, _shell.IsRunning);
        previousIcon?.Dispose();
        Tray.ToolTipText = _shell.IsRunning
            ? $"Internet Monitoring - {_shell.StateLabel}, {_shell.ElapsedText}"
            : "Internet Monitoring - nadzor nije pokrenut";
    }

    private static Brush SoftFor(VerdictKind kind) => kind switch
    {
        VerdictKind.UpstreamFault => Frozen("#FDECEB"),
        VerdictKind.LocalFault => Frozen("#FDF5E2"),
        VerdictKind.Stable => Frozen("#EAF7EF"),
        _ => Frozen("#F4F4F6"),
    };

    private static Brush AccentFor(VerdictKind kind) => kind switch
    {
        VerdictKind.UpstreamFault => Palette.Outage,
        VerdictKind.LocalFault => Palette.Degraded,
        VerdictKind.Stable => Palette.Ok,
        _ => Palette.Neutral,
    };

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Closing the window.
    /// <para>
    /// With the service, the window is only a view and closing it is harmless - but the
    /// user has no way of knowing that, so it is said once, explicitly. Without the
    /// service, closing genuinely ends the session, and that has to be confirmed rather
    /// than discovered afterwards.
    /// </para>
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_reallyExit || _shell is null)
        {
            // The icon goes before shutdown starts: while the exit is still winding down it
            // stayed in the tray, clickable, and a click on it tried to show a window that
            // no longer existed.
            Tray.Dispose();
            base.OnClosing(e);
            Application.Current.Shutdown();
            return;
        }

        if (_shell.SurvivesClosing)
        {
            e.Cancel = true;
            Hide();

            if (!_closeExplained)
            {
                _closeExplained = true;
                Tray.ShowNotification(
                    "Nadzor se nastavlja",
                    "Prozor je zatvoren, ali test i dalje traje. Kliknite ikonu da ga ponovo otvorite.");
            }

            return;
        }

        if (_shell.IsRunning)
        {
            var answer = MessageBox.Show(
                "Nadzor radi u ovom prozoru. Ako ga zatvorite, test se zaustavlja i sesija se zatvara " +
                "sa onim što je do sada prikupljeno.\n\n" +
                "Za test koji preživljava zatvaranje prozora i restart računara, instalirajte servis.\n\n" +
                "Zaustaviti nadzor i izaći?",
                "Internet Monitoring",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        base.OnClosing(e);
        Application.Current.Shutdown();
    }

    private void OnTrayClicked(object sender, RoutedEventArgs e) => ShowWindow();

    private void OnShowWindow(object sender, RoutedEventArgs e) => ShowWindow();

    private void OnOpenFolder(object sender, RoutedEventArgs e) => _shell?.OpenFolderCommand.Execute(null);

    /// <summary>
    /// Version, licence, and where to send a bug.
    /// <para>
    /// Owned by this window when there is one to own it - the same handler serves the tray
    /// menu, where the main window may be hidden and cannot be an owner. A second click while
    /// it is already up brings the existing one forward rather than opening a second.
    /// </para>
    /// </summary>
    private void OnAbout(object sender, RoutedEventArgs e)
    {
        if (_about is not null)
        {
            _about.Activate();
            return;
        }

        _about = new AboutWindow { Owner = _closed || !IsVisible ? null : this };
        _about.Closed += (_, _) => _about = null;
        _about.Show();
    }

    private AboutWindow? _about;

    private void OnExit(object sender, RoutedEventArgs e)
    {
        _reallyExit = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    private void ShowWindow()
    {
        // During exit the icon could still be clicked for a moment after the window closed;
        // showing a closed window throws, and that crash is what actually ended up in the
        // error log while the frozen exit was blamed for everything.
        if (_closed)
        {
            return;
        }

        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
}
