using System.Text;
using System.Globalization;

namespace IEM.App.Linux;

internal static class CrashLog
{
    public static void TryWrite(Exception exception)
    {
        try
        {
            var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (string.IsNullOrWhiteSpace(stateHome))
            {
                var userHome = Environment.GetEnvironmentVariable("HOME");
                if (string.IsNullOrWhiteSpace(userHome))
                {
                    return;
                }

                stateHome = Path.Combine(userHome, ".local", "state");
            }

            var directory = Path.Combine(stateHome, "internet-evidence-monitor", "logs");
            Directory.CreateDirectory(directory);
            var text = new StringBuilder()
                .AppendLine(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .AppendLine(exception.ToString())
                .ToString();
            File.AppendAllText(Path.Combine(directory, "ui-crash.log"), text, Encoding.UTF8);
        }
        catch
        {
            // Crash reporting must never replace the original failure.
        }
    }
}
