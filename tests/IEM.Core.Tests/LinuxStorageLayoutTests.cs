using IEM.Linux.Storage;
using Xunit;

namespace IEM.Core.Tests;

public sealed class LinuxStorageLayoutTests
{
    // 1. System namespace canonical paths
    [Fact]
    public void LinuxStorageLayout_System_Default_Paths_Match_Canonical_Hierarchy()
    {
        var layout = LinuxStorageLayout.Instance;

        Assert.Equal("/var/lib/internet-evidence-monitor", layout.StateRoot);
        Assert.Equal("/var/lib/internet-evidence-monitor/sessions", layout.SessionsRoot);
        Assert.Equal("/var/lib/internet-evidence-monitor/keys", layout.KeysRoot);
        Assert.Equal("/var/lib/internet-evidence-monitor/cases", layout.CasesRoot);
        Assert.Equal("/var/lib/internet-evidence-monitor/state", layout.StateDataRoot);
        Assert.Equal("/run/internet-evidence-monitor", layout.RuntimeDirectory);

        Assert.Equal("/var/lib/internet-evidence-monitor/sessions", layout.DefaultOutputRoot);
        Assert.Equal("/var/lib/internet-evidence-monitor/sessions", layout.ResolveOutputRoot(isInstalled: true));
    }

    [Fact]
    public void LinuxStorageLayout_Session_Directory_Follows_Standard_Sesija_Naming()
    {
        var layout = LinuxStorageLayout.Instance;
        var sessionDir = layout.GetSessionDirectory("20260820_221500", isInstalled: true).Replace('\\', '/');

        Assert.Equal("/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_221500", sessionDir);
    }

    // 2. Portable namespace with XDG_STATE_HOME
    [Fact]
    public void LinuxPortableStorageLayout_Uses_XDG_STATE_HOME_When_Present()
    {
        var env = new Dictionary<string, string>
        {
            { "XDG_STATE_HOME", "/custom/user/state" },
            { "HOME", "/home/testuser" },
            { "XDG_DATA_HOME", "/custom/user/data" } // MUST BE IGNORED
        };

        var layout = new LinuxPortableStorageLayout(getEnvironmentVariable: k => env.GetValueOrDefault(k));

        Assert.True(layout.IsAvailable);
        Assert.Equal("/custom/user/state/internet-evidence-monitor", layout.StateRoot.Replace('\\', '/'));
        Assert.Equal("/custom/user/state/internet-evidence-monitor/sessions", layout.SessionsRoot.Replace('\\', '/'));
        Assert.Equal("/custom/user/state/internet-evidence-monitor/keys", layout.KeysRoot.Replace('\\', '/'));
        Assert.Equal("/custom/user/state/internet-evidence-monitor/cases", layout.CasesRoot.Replace('\\', '/'));
        Assert.Equal("/custom/user/state/internet-evidence-monitor/state", layout.StateDataRoot.Replace('\\', '/'));

        Assert.Equal("/custom/user/state/internet-evidence-monitor/sessions", layout.DefaultOutputRoot.Replace('\\', '/'));
        Assert.Equal("/custom/user/state/internet-evidence-monitor/sessions", layout.PortableOutputRoot.Replace('\\', '/'));
    }

    // 3. Portable namespace with HOME fallback (.local/state)
    [Fact]
    public void LinuxPortableStorageLayout_Uses_HOME_Local_State_When_XDG_STATE_HOME_Missing()
    {
        var env = new Dictionary<string, string>
        {
            { "HOME", "/home/alice" },
            { "XDG_DATA_HOME", "/home/alice/.local/share" } // MUST BE IGNORED
        };

        var layout = new LinuxPortableStorageLayout(getEnvironmentVariable: k => env.GetValueOrDefault(k));

        Assert.True(layout.IsAvailable);
        Assert.Equal("/home/alice/.local/state/internet-evidence-monitor", layout.StateRoot.Replace('\\', '/'));
        Assert.Equal("/home/alice/.local/state/internet-evidence-monitor/sessions", layout.SessionsRoot.Replace('\\', '/'));
        Assert.Equal("/home/alice/.local/state/internet-evidence-monitor/keys", layout.KeysRoot.Replace('\\', '/'));
        Assert.Equal("/home/alice/.local/state/internet-evidence-monitor/cases", layout.CasesRoot.Replace('\\', '/'));
        Assert.Equal("/home/alice/.local/state/internet-evidence-monitor/state", layout.StateDataRoot.Replace('\\', '/'));
    }

    // 4. Characterization test: MUST NEVER use XDG_DATA_HOME or .local/share
    [Fact]
    public void LinuxPortableStorageLayout_Characterization_Never_Uses_XdgDataHome_Or_Share()
    {
        var env = new Dictionary<string, string>
        {
            { "HOME", "/home/bob" },
            { "XDG_DATA_HOME", "/home/bob/.local/share" }
        };

        var root = LinuxStoragePaths.TryResolvePortableStateRoot(k => env.GetValueOrDefault(k));

        Assert.NotNull(root);
        Assert.DoesNotContain("share", root);
        Assert.Contains(".local/state", root.Replace('\\', '/'));
    }

    // 5. Fail-closed: Missing both XDG_STATE_HOME and HOME yields null / unavailable (ZERO /tmp fallback)
    [Fact]
    public void LinuxPortableStorageLayout_FailClosed_When_No_Valid_Environment()
    {
        var env = new Dictionary<string, string>(); // Empty environment

        var resolved = LinuxStoragePaths.TryResolvePortableStateRoot(k => env.GetValueOrDefault(k));
        Assert.Null(resolved); // ZERO /tmp fallback!

        var layout = new LinuxPortableStorageLayout(getEnvironmentVariable: k => env.GetValueOrDefault(k));
        Assert.False(layout.IsAvailable);

        var ex = Assert.Throws<InvalidOperationException>(() => layout.StateRoot);
        Assert.Contains("unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // 6. Fail-closed: Relative or malformed path in XDG_STATE_HOME and HOME is rejected
    [Theory]
    [InlineData("relative/path")]
    [InlineData("\0invalid_null_byte")]
    [InlineData("   ")]
    public void LinuxPortableStorageLayout_Rejects_Invalid_Or_Relative_State_Paths(string invalidPath)
    {
        var env = new Dictionary<string, string>
        {
            { "XDG_STATE_HOME", invalidPath }
        };

        var resolved = LinuxStoragePaths.TryResolvePortableStateRoot(k => env.GetValueOrDefault(k));
        Assert.Null(resolved);
    }
}
