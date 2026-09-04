using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using IEM.App.Linux.Hosting;
using IEM.App.Linux.ViewModels;

namespace IEM.App.Linux;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var host = LinuxMonitorHostFactory.Create();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new LinuxShellViewModel(host),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
