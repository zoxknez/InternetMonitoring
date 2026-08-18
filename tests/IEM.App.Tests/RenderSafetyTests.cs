using System.IO;

namespace IEM.App.Tests;

/// <summary>
/// The interface may not ask the graphics driver for anything it can silently fail to deliver.
/// <para>
/// A WPF <c>Effect</c> renders the element and its whole subtree into an intermediate surface
/// on the GPU. When that allocation fails - which depends on the driver, not on this program -
/// the content simply does not appear: no exception, nothing in any log, layout correct,
/// screen blank. Several testers reported exactly that, on the two largest cards first.
/// </para>
/// <para>
/// A grep test rather than a behavioural one, because the failure cannot be provoked on a
/// machine whose driver does not have the fault. What can be checked is that the program never
/// asks.
/// </para>
/// </summary>
public sealed class RenderSafetyTests
{
    [Fact]
    public void No_part_of_the_interface_uses_a_gpu_effect()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(AppRoot(), "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            foreach (var banned in new[] { "DropShadowEffect", "BlurEffect", "BitmapEffect" })
            {
                // The word appears in the paragraph explaining why it is gone; only markup counts.
                if (text.Contains('<' + banned, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {banned}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Walks up to the repository root. CallerFilePath is rewritten under a CI build, so the
    /// path is found from where the tests actually run.
    /// </summary>
    private static string AppRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && directory.GetFiles("*.slnx").Length == 0)
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "IEM.App");
    }
}
