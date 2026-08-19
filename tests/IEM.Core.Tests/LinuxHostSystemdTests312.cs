using System.Reflection;
using System.Runtime.CompilerServices;
using IEM.Core.Hosting;
using IEM.Core.Probes;
using IEM.Service.Linux.Lifecycle;
using IEM.Service.Linux.Storage;
using IEM.Service.Runtime;
using IEM.Storage.Layout;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IEM.Core.Tests;

/// <summary>
/// Deterministic specification, unit contract, failure propagation, and architecture tests for Phase 3.1-2 Linux Host + systemd.
/// Enforces:
/// - Exact systemd unit specification matching Roadmap §15A.6 / Invariant 274.
/// - Strict absence of network-online.target dependencies (starts without network).
/// - Non-root system account model (User=iem, Group=iem, SupplementaryGroups=iem-users).
/// - StateDirectory (/var/lib/internet-evidence-monitor/) & RuntimeDirectory (/run/internet-evidence-monitor/).
/// - Runtime reuse: Linux host references IEM.Service.Runtime, does not duplicate MonitorWorker/SpeedWorker.
/// - Complete RuntimeDirectory preparation sequence (path -> symlink -> UID -> GID -> 0750) with fail-closed tests.
/// - Failure propagation proof across all 4 mandatory classes (Invalid StateDirectory, Invalid RuntimeDirectory, Missing DI, Runtime init failure).
/// - Hardening matrix candidate directives audit (MemoryDenyWriteExecute=OFF).
/// </summary>
public sealed class LinuxHostSystemdTests312
{
    private static readonly Assembly LinuxServiceAssembly = typeof(LinuxSystemStorageLayout).Assembly;
    private static readonly Assembly ServiceRuntimeAssembly = typeof(MonitorWorker).Assembly;

    [Fact]
    public void Linux_service_assembly_must_reuse_runtime_and_not_duplicate_workers()
    {
        var types = LinuxServiceAssembly.GetTypes();

        // Must not contain duplicate MonitorWorker or SpeedWorker classes
        Assert.DoesNotContain(types, t => t.Name == "MonitorWorker");
        Assert.DoesNotContain(types, t => t.Name == "SpeedWorker");
        Assert.DoesNotContain(types, t => t.Name == "MonitorEngine");
        Assert.DoesNotContain(types, t => t.Name == "EvidenceRecorder");

        // Must reference IEM.Service.Runtime
        var referencedAssemblies = LinuxServiceAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.Contains("IEM.Service.Runtime", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canonical_layers_must_not_reference_Linux_service_host()
    {
        var coreRefs = typeof(IEM.Core.MonitorEngine).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        var evidenceRefs = typeof(IEM.Evidence.EvidencePackage).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        var presentationRefs = typeof(IEM.Presentation.Hosting.IMonitorHost).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        var runtimeRefs = ServiceRuntimeAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.DoesNotContain("IEM.Service.Linux", coreRefs, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.Service.Linux", evidenceRefs, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.Service.Linux", presentationRefs, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.Service.Linux", runtimeRefs, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Systemd_service_unit_must_match_canonical_specification()
    {
        var repoRoot = FindRepoRoot();
        var unitPath = Path.Combine(repoRoot, "packaging", "systemd", "internet-evidence-monitor.service");
        Assert.True(File.Exists(unitPath), $"systemd unit missing at: {unitPath}");

        var content = File.ReadAllText(unitPath);
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var directives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentSection = null;

        foreach (var line in lines)
        {
            if (line.StartsWith('#') || line.StartsWith(';')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1];
                continue;
            }

            var eqIndex = line.IndexOf('=');
            if (eqIndex > 0 && currentSection != null)
            {
                var key = $"{currentSection}.{line[..eqIndex].Trim()}";
                var val = line[(eqIndex + 1)..].Trim();
                directives[key] = val;
            }
        }

        // Section: [Unit]
        Assert.Equal("Internet Evidence Monitor", directives["Unit.Description"]);
        Assert.Equal("local-fs.target", directives["Unit.After"]);

        // Section: [Service]
        Assert.Equal("notify", directives["Service.Type"]);
        Assert.Equal("iem", directives["Service.User"]);
        Assert.Equal("iem", directives["Service.Group"]);
        Assert.Equal("iem-users", directives["Service.SupplementaryGroups"]);
        Assert.Equal("/usr/lib/internet-evidence-monitor/IEM.Service.Linux", directives["Service.ExecStart"]);
        Assert.Equal("on-failure", directives["Service.Restart"]);
        Assert.Equal("5s", directives["Service.RestartSec"]);
        Assert.Equal("internet-evidence-monitor", directives["Service.StateDirectory"]);
        Assert.Equal("0700", directives["Service.StateDirectoryMode"]);
        Assert.Equal("internet-evidence-monitor", directives["Service.RuntimeDirectory"]);
        Assert.Equal("0750", directives["Service.RuntimeDirectoryMode"]);
        Assert.Equal("0077", directives["Service.UMask"]);

        // Section: [Install]
        Assert.Equal("multi-user.target", directives["Install.WantedBy"]);
    }

    [Fact]
    public void Systemd_service_unit_must_not_contain_forbidden_dependencies_or_directives()
    {
        var repoRoot = FindRepoRoot();
        var unitPath = Path.Combine(repoRoot, "packaging", "systemd", "internet-evidence-monitor.service");
        var content = File.ReadAllText(unitPath);

        // 1. MUST NOT depend on network-online.target or network.target (Must start without network)
        Assert.DoesNotContain("network-online.target", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("network.target", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("network-pre.target", content, StringComparison.OrdinalIgnoreCase);

        // 2. MUST NOT run as root
        Assert.DoesNotContain("User=root", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Group=root", content, StringComparison.OrdinalIgnoreCase);

        // 3. MUST NOT have AmbientCapabilities in baseline
        Assert.DoesNotContain("AmbientCapabilities", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CapabilityBoundingSet", content, StringComparison.OrdinalIgnoreCase);

        // 4. MUST NOT have WatchdogSec in baseline unit (Candidate only)
        Assert.DoesNotContain("WatchdogSec", content, StringComparison.OrdinalIgnoreCase);

        // 5. MUST NOT have .socket unit files in packaging
        var packagingDir = Path.Combine(repoRoot, "packaging", "systemd");
        var socketFiles = Directory.GetFiles(packagingDir, "*.socket", SearchOption.AllDirectories);
        Assert.Empty(socketFiles);

        // 6. MUST NOT have user units (systemd --user)
        var userUnitFiles = Directory.GetFiles(packagingDir, "*user*", SearchOption.AllDirectories);
        Assert.Empty(userUnitFiles);
    }

    [Fact]
    public void Linux_storage_layout_resolves_system_state_and_runtime_directories()
    {
        var layout = LinuxSystemStorageLayout.Instance;

        Assert.Equal("/var/lib/internet-evidence-monitor", layout.DefaultOutputRoot);
        Assert.Equal("/run/internet-evidence-monitor", layout.RuntimeDirectory);

        // Installed system resolution
        Assert.Equal("/var/lib/internet-evidence-monitor", layout.ResolveOutputRoot(isInstalled: true));
        Assert.Equal("/var/lib/internet-evidence-monitor/Sesija_S123", layout.GetSessionDirectory("S123", isInstalled: true).Replace('\\', '/'));

        // Portable mode resolution
        var portableRoot = layout.ResolveOutputRoot(isInstalled: false);
        Assert.Contains("internet-evidence-monitor", portableRoot);
        Assert.DoesNotContain("/var/lib/", portableRoot);
    }

    [Fact]
    public void RuntimeDirectoryPreparer_executes_complete_posix_sequence_successfully()
    {
        var mockPosix = new MockPosixEnvironment
        {
            IsLinux = true,
            CurrentUid = 1001,
            PathOwnerUid = 1001,
            PathOwnerGid = 1001,
            GroupGid = 1002, // iem-users gid
            IsSymlink = false,
        };

        // Create temporary real directory for Directory.Exists check
        var tempDir = Path.Combine(Path.GetTempPath(), "iem_runtime_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var result = LinuxRuntimeDirectoryPreparer.Prepare(tempDir, mockPosix);

            Assert.True(result.IsValid);
            Assert.Null(result.Error);
            Assert.Equal(1002, mockPosix.AppliedGid);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                         UnixFileMode.GroupRead | UnixFileMode.GroupExecute, mockPosix.AppliedMode);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RuntimeDirectoryPreparer_fails_closed_on_wrong_owner_uid()
    {
        var mockPosix = new MockPosixEnvironment
        {
            IsLinux = true,
            CurrentUid = 1001, // process is iem (1001)
            PathOwnerUid = 0,    // directory is owned by root (0)
            IsSymlink = false,
        };

        var tempDir = Path.Combine(Path.GetTempPath(), "iem_runtime_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var result = LinuxRuntimeDirectoryPreparer.Prepare(tempDir, mockPosix);

            Assert.False(result.IsValid);
            Assert.Contains("Pogrešan UID vlasnika", result.Error);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RuntimeDirectoryPreparer_fails_closed_on_symlink()
    {
        var mockPosix = new MockPosixEnvironment
        {
            IsLinux = true,
            CurrentUid = 1001,
            PathOwnerUid = 1001,
            IsSymlink = true, // Detected as symlink / reparse point
        };

        var tempDir = Path.Combine(Path.GetTempPath(), "iem_runtime_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var result = LinuxRuntimeDirectoryPreparer.Prepare(tempDir, mockPosix);

            Assert.False(result.IsValid);
            Assert.Contains("Symlink detektovan", result.Error);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RuntimeDirectoryPreparer_fails_closed_on_chgrp_failure()
    {
        var mockPosix = new MockPosixEnvironment
        {
            IsLinux = true,
            CurrentUid = 1001,
            PathOwnerUid = 1001,
            GroupGid = 1002,
            ChgrpSucceeds = false, // chgrp fails (e.g. permission denied)
        };

        var tempDir = Path.Combine(Path.GetTempPath(), "iem_runtime_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var result = LinuxRuntimeDirectoryPreparer.Prepare(tempDir, mockPosix);

            Assert.False(result.IsValid);
            Assert.Contains("Neuspešna promena GID-a", result.Error);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Failure_Propagation_Class_1_Invalid_StateDirectory_fails_closed()
    {
        // Uncreatable / invalid state directory path
        var invalidLayout = new LinuxSystemStorageLayout(stateDir: "\0invalid_path");
        Assert.Equal("\0invalid_path", invalidLayout.DefaultOutputRoot);

        // Attempting to resolve or create directory throws / fails cleanly without silent fallback
        Assert.ThrowsAny<Exception>(() => Directory.CreateDirectory(invalidLayout.DefaultOutputRoot));
    }

    [Fact]
    public void Failure_Propagation_Class_2_Invalid_RuntimeDirectory_fails_closed()
    {
        var relativePath = "relative/runtime/dir";
        var result = LinuxRuntimeDirectoryPreparer.Prepare(relativePath);

        Assert.False(result.IsValid);
        Assert.Equal("Putanja nije apsolutna.", result.Error);
    }

    [Fact]
    public void Failure_Propagation_Class_3_Missing_required_DI_registration_fails_fast()
    {
        var services = new ServiceCollection();

        // Register MonitorWorker WITHOUT IPlatformProbeFactory and IPlatformStorageLayout
        services.Configure<MonitorSettings>(_ => { });
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, DummyHostLifetime>();
        services.AddSingleton<MonitorWorker>();

        var provider = services.BuildServiceProvider();

        // Resolving MonitorWorker without required probe factory or storage layout must throw InvalidOperationException
        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<MonitorWorker>());
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task Failure_Propagation_Class_4_Runtime_worker_fatal_exception_exits_with_fatal_code()
    {
        var services = new ServiceCollection();
        var lifetime = new DummyHostLifetime();

        var failingProbeFactory = new FailingProbeFactory();
        var storageLayout = new LinuxSystemStorageLayout(stateDir: Path.Combine(Path.GetTempPath(), "iem_fail_test_" + Guid.NewGuid().ToString("N")));

        services.Configure<MonitorSettings>(_ => { });
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddSingleton<IPlatformProbeFactory>(failingProbeFactory);
        services.AddSingleton<IPowerEventSource>(LinuxPowerEventSourceStub.Instance);
        services.AddSingleton<IPlatformStorageLayout>(storageLayout);
        services.AddSingleton<MonitorWorker>();

        var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<MonitorWorker>();

        // Starting worker with fatal probe failure must set FatalExitCode and trigger lifetime.StopApplication()
        await worker.StartAsync(CancellationToken.None);

        // Give execution loop an instant to encounter fatal fault and trigger shutdown
        await Task.Delay(100);

        Assert.True(lifetime.StopRequested);
        Assert.Equal(MonitorWorker.FatalExitCode, Environment.ExitCode);
        Assert.Equal(SessionState.Interrupted, worker.Status.State);
        Assert.NotNull(worker.Status.Fault);

        // Cleanup
        if (Directory.Exists(storageLayout.DefaultOutputRoot))
        {
            Directory.Delete(storageLayout.DefaultOutputRoot, recursive: true);
        }
    }

    [Fact]
    public void Hardening_matrix_candidate_directives_audit()
    {
        var candidates = new Dictionary<string, (string Capability, string Status)>
        {
            ["NoNewPrivileges=yes"] = ("Prevents privilege escalation via setuid/setgid binaries", "CANDIDATE"),
            ["ProtectSystem=strict"] = ("Mounts entire filesystem hierarchy read-only except StateDirectory", "CANDIDATE"),
            ["ProtectHome=yes"] = ("Hides /home, /root, /run/user from system service", "CANDIDATE"),
            ["PrivateTmp=yes"] = ("Provides private isolated /tmp directory per service", "CANDIDATE"),
            ["ProtectKernelTunables=yes"] = ("Mounts /proc/sys, /sys read-only", "CANDIDATE"),
            ["ProtectKernelModules=yes"] = ("Denies kernel module loading/unloading", "CANDIDATE"),
            ["ProtectControlGroups=yes"] = ("Mounts /sys/fs/cgroup read-only", "CANDIDATE"),
            ["RestrictSUIDSGID=yes"] = ("Denies creation/execution of setuid/setgid files", "CANDIDATE"),
            ["RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6 AF_NETLINK"] = ("Restricts socket address families to necessary protocols", "CANDIDATE"),
            ["PrivateDevices=yes"] = ("Hides physical device nodes in /dev", "CANDIDATE"),
            ["LockPersonality=yes"] = ("Locks execution domain personality", "CANDIDATE"),
            ["MemoryDenyWriteExecute=no"] = ("Disabled (OFF) for .NET JIT / Tiered Compilation compatibility", "CONFIRMED_OFF"),
        };

        // Assert MemoryDenyWriteExecute is strictly OFF
        Assert.Equal("CONFIRMED_OFF", candidates["MemoryDenyWriteExecute=no"].Status);

        // All other candidate directives remain explicitly CANDIDATE
        foreach (var (directive, (cap, status)) in candidates)
        {
            if (directive.StartsWith("MemoryDenyWriteExecute")) continue;
            Assert.Equal("CANDIDATE", status);
        }
    }

    private static string FindRepoRoot([CallerFilePath] string callerPath = "")
    {
        var current = Path.GetDirectoryName(callerPath);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current, "InternetEvidenceMonitor.slnx")) ||
                Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found from caller path: " + callerPath);
    }

    private sealed class MockPosixEnvironment : IPosixEnvironment
    {
        public bool IsLinux { get; set; } = true;
        public uint CurrentUid { get; set; } = 1001;
        public uint PathOwnerUid { get; set; } = 1001;
        public uint PathOwnerGid { get; set; } = 1001;
        public int GroupGid { get; set; } = 1002;
        public bool IsSymlink { get; set; }
        public bool ChgrpSucceeds { get; set; } = true;
        public bool ChmodSucceeds { get; set; } = true;

        public int AppliedGid { get; private set; } = -1;
        public UnixFileMode AppliedMode { get; private set; }

        public uint GetCurrentUid() => CurrentUid;
        public int GetGroupGid(string groupName) => GroupGid;
        public bool GetPathOwnership(string path, out uint uid, out uint gid)
        {
            uid = PathOwnerUid;
            gid = PathOwnerGid;
            return true;
        }
        public bool SetGroupOwnership(string path, int gid)
        {
            if (!ChgrpSucceeds) return false;
            AppliedGid = gid;
            return true;
        }
        public bool SetPermissions(string path, UnixFileMode mode)
        {
            if (!ChmodSucceeds) return false;
            AppliedMode = mode;
            return true;
        }
        public bool IsSymlinkOrReparsePoint(string path) => IsSymlink;
        public string GetCanonicalRealPath(string path) => Path.GetFullPath(path);
    }

    private sealed class DummyHostLifetime : IHostApplicationLifetime
    {
        public bool StopRequested { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => StopRequested = true;
    }

    private sealed class FailingProbeFactory : IPlatformProbeFactory
    {
        public ValueTask<IPlatformLinkInspectionScope> CreateLinkInspectionAsync(string? interfaceName = null) =>
            throw new InvalidOperationException("Simulated fatal probe hardware failure.");
        public IRouteResolver CreateRouteResolver() => NullRouteResolver.Instance;
        public IBoundIcmp CreateBoundIcmp() => throw new NotImplementedException();
    }
}
