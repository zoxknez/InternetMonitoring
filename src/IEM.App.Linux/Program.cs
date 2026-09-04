using Avalonia;
using System.Globalization;
using IEM.Core.Presentation;

namespace IEM.App.Linux;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = SerbianText.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = SerbianText.Culture;

        using var instance = SingleInstanceLease.TryAcquire();
        if (instance is null)
        {
            Console.Error.WriteLine("Monitor internet dokaza je već pokrenut za ovog korisnika.");
            return 2;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            CrashLog.TryWrite(ex);
            Console.Error.WriteLine($"Aplikacija nije pokrenuta: {ex.Message}");
            return 3;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
