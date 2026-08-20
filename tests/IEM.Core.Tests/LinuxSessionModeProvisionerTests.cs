using System.Net;
using IEM.Core.Hosting;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Linux.Storage;
using IEM.Service.Runtime;
using IEM.Storage;
using IEM.Storage.Evidence;
using IEM.Storage.Layout;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IEM.Core.Tests;

public sealed class LinuxSessionModeProvisionerTests : IDisposable
{
    private readonly string _testRoot;

    public LinuxSessionModeProvisionerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "iem_session_mode_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    // 1. Safe canonical tree provisioning and verification
    [Fact]
    public async Task Provisioner_Creates_Canonical_Directory_Tree_And_Yields_Established()
    {
        var sessionDir = Path.Combine(_testRoot, "Sesija_20260820_223000");
        var layout = SessionLayoutDescriptor.CreateStandard("20260820_223000");
        var provisioner = new LinuxSessionModeProvisioner();

        var provObs = await provisioner.ProvisionSessionBoundariesAsync(sessionDir, layout);

        Assert.Equal(StorageProtectionState.Established, provObs.ProtectionState);
        Assert.True(provObs.RootBoundaryValid);
        Assert.True(provObs.ReparsePointCheck);
        Assert.True(Directory.Exists(Path.Combine(sessionDir, "Raw")));
        Assert.True(Directory.Exists(Path.Combine(sessionDir, "Evidence")));
        Assert.True(Directory.Exists(Path.Combine(sessionDir, "Derived")));
        Assert.True(Directory.Exists(Path.Combine(sessionDir, "Exports")));
        Assert.True(File.Exists(Path.Combine(sessionDir, SessionLayoutDescriptor.FileName)));

        // Idempotent verify on created tree
        var verObs = await provisioner.VerifyStorageProtectionAsync(sessionDir, layout);
        Assert.Equal(StorageProtectionState.Established, verObs.ProtectionState);
    }

    // 2. Missing area directory yields NotEstablished during Verify
    [Fact]
    public async Task Verify_Fails_When_Required_Subdirectory_Is_Missing()
    {
        var sessionDir = Path.Combine(_testRoot, "Sesija_20260820_223001");
        var layout = SessionLayoutDescriptor.CreateStandard("20260820_223001");
        var provisioner = new LinuxSessionModeProvisioner();

        await provisioner.ProvisionSessionBoundariesAsync(sessionDir, layout);

        // Delete Raw directory
        Directory.Delete(Path.Combine(sessionDir, "Raw"));

        var verObs = await provisioner.VerifyStorageProtectionAsync(sessionDir, layout);
        Assert.Equal(StorageProtectionState.NotEstablished, verObs.ProtectionState);
        Assert.False(verObs.ReparsePointCheck);
        Assert.Contains("Raw", verObs.DiagnosticMessage);
    }

    // 3. Missing or corrupted layout.json yields NotEstablished
    [Fact]
    public async Task Verify_Fails_When_LayoutDescriptor_Is_Missing_Or_Corrupted()
    {
        var sessionDir = Path.Combine(_testRoot, "Sesija_20260820_223002");
        var layout = SessionLayoutDescriptor.CreateStandard("20260820_223002");
        var provisioner = new LinuxSessionModeProvisioner();

        await provisioner.ProvisionSessionBoundariesAsync(sessionDir, layout);

        // Corrupt layout.json
        var layoutPath = Path.Combine(sessionDir, SessionLayoutDescriptor.FileName);
        await File.WriteAllTextAsync(layoutPath, "{ not valid json ... }");

        var verObs = await provisioner.VerifyStorageProtectionAsync(sessionDir, layout);
        Assert.Equal(StorageProtectionState.NotEstablished, verObs.ProtectionState);
        Assert.Contains("oštećen", verObs.DiagnosticMessage);
    }

    // 4. Mismatched SessionId in layout.json yields NotEstablished
    [Fact]
    public async Task Verify_Fails_When_LayoutDescriptor_Has_Mismatched_SessionId()
    {
        var sessionDir = Path.Combine(_testRoot, "Sesija_20260820_223003");
        var layout = SessionLayoutDescriptor.CreateStandard("20260820_223003");
        var provisioner = new LinuxSessionModeProvisioner();

        await provisioner.ProvisionSessionBoundariesAsync(sessionDir, layout);

        // Overwrite layout.json with a different SessionId
        var wrongLayout = SessionLayoutDescriptor.CreateStandard("DIFFERENT_SESSION_ID");
        var layoutPath = Path.Combine(sessionDir, SessionLayoutDescriptor.FileName);
        await File.WriteAllBytesAsync(layoutPath, wrongLayout.ToCanonicalBytes());

        var verObs = await provisioner.VerifyStorageProtectionAsync(sessionDir, layout);
        Assert.Equal(StorageProtectionState.NotEstablished, verObs.ProtectionState);
        Assert.Contains("neslaganje SessionId", verObs.DiagnosticMessage);
    }

    // 5. Mock POSIX: Symlink in root or area is detected and rejected
    [Fact]
    public void MockPosix_Rejects_Symlink_In_Session_Tree()
    {
        var mockPosix = new MockPosixStorageApi();
        mockPosix.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mockPosix.AddEntry("/var/lib/internet-evidence-monitor/sessions", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mockPosix.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_1", isDir: true, isSymlink: true, mode: 0x1C0, uid: 1000, gid: 1000); // SYMLINK!

        var guard = new LinuxSymlinkGuard(mockPosix);
        var result = guard.ValidatePath("/var/lib/internet-evidence-monitor", "/var/lib/internet-evidence-monitor/sessions/Sesija_1");

        Assert.False(result.IsSafe);
        Assert.Equal(StorageProtectionState.NotEstablished, result.State);
        Assert.Contains("symlink", result.ViolationMessage, StringComparison.OrdinalIgnoreCase);
    }

    // 6. Mock POSIX: Path traversal escape '..' is rejected
    [Fact]
    public void MockPosix_Rejects_Directory_Traversal_Escape()
    {
        var guard = new LinuxSymlinkGuard();
        var result = guard.ValidatePath("/var/lib/internet-evidence-monitor", "/var/lib/internet-evidence-monitor/../etc/shadow");

        Assert.False(result.IsSafe);
        Assert.Equal(StorageProtectionState.NotEstablished, result.State);
        Assert.Contains("traversal", result.ViolationMessage, StringComparison.OrdinalIgnoreCase);
    }

    // 7. Mock POSIX: Wrong UID/GID ownership is rejected and never silently repaired (Invariant 80)
    [Fact]
    public void MockPosix_Rejects_Wrong_Uid_Gid_Ownership()
    {
        var mockPosix = new MockPosixStorageApi();
        mockPosix.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mockPosix.AddEntry("/var/lib/internet-evidence-monitor/sessions", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mockPosix.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_1", isDir: true, isSymlink: false, mode: 0x1C0, uid: 0, gid: 0); // Root owned (wrong!)

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var guard = new LinuxSymlinkGuard(mockPosix, policy);
        var result = guard.ValidatePath("/var/lib/internet-evidence-monitor", "/var/lib/internet-evidence-monitor/sessions/Sesija_1");

        Assert.False(result.IsSafe);
        Assert.Equal(StorageProtectionState.NotEstablished, result.State);
        Assert.Contains("vlasnik", result.ViolationMessage, StringComparison.OrdinalIgnoreCase);
    }

    // 8. Mock POSIX: World-writable mode permissions are rejected
    [Fact]
    public void MockPosix_Rejects_World_Writable_Permission_Drift()
    {
        var mockPosix = new MockPosixStorageApi();
        mockPosix.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mockPosix.AddEntry("/var/lib/internet-evidence-monitor/sessions", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mockPosix.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_1", isDir: true, isSymlink: false, mode: 0x1FF, uid: 1000, gid: 1000); // 0777 (world-writable!)

        var guard = new LinuxSymlinkGuard(mockPosix);
        var result = guard.ValidatePath("/var/lib/internet-evidence-monitor", "/var/lib/internet-evidence-monitor/sessions/Sesija_1");

        Assert.False(result.IsSafe);
        Assert.Equal(StorageProtectionState.NotEstablished, result.State);
        Assert.Contains("world-writable", result.ViolationMessage, StringComparison.OrdinalIgnoreCase);
    }

    // 9. Invariant 81: EvidenceRecorder.Start throws when session directory is not provisioned
    [Fact]
    public void EvidenceRecorder_Start_Throws_When_Session_Directory_Not_Provisioned()
    {
        var sessionDir = Path.Combine(_testRoot, "NonExistentSessionDir");
        var paths = new SessionPaths(sessionDir);
        var clock = new ManualClock();
        var probeSource = new ScriptedProbeSource(clock, [CycleBuilder.Wired().Build()], TimeSpan.FromSeconds(1));
        var engine = new MonitorEngine(probeSource);
        var startPayload = new SessionStartPayload("S1", "3.1.0", DateTimeOffset.UtcNow, TimeSpan.FromHours(1), "host", "eth0", LinkMedium.Ethernet, 1_000_000_000, "192.168.1.1");

        var ex = Assert.Throws<InvalidOperationException>(() => EvidenceRecorder.Start(paths, engine, startPayload));
        Assert.Contains("boundary must be provisioned", ex.Message);
    }

    // 10. MonitorWorker fail-closed: StorageProtection NotEstablished aborts session start before probes or recorder start
    [Fact]
    public async Task MonitorWorker_Fails_Closed_When_Storage_Protection_Is_NotEstablished()
    {
        var services = new ServiceCollection();
        var lifetime = new DummyHostLifetime();
        var storageLayout = new LinuxStorageLayout(stateRoot: _testRoot);

        // Failing provisioner that always returns NotEstablished
        var failingProvisioner = new FailingStorageProtectionProvider();

        services.Configure<MonitorSettings>(opts =>
        {
            opts.AutoStart = true;
            opts.Duration = "1h";
        });
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddSingleton<IPlatformProbeFactory>(new LinuxProbeFactoryStub());
        services.AddSingleton<IPowerEventSource>(LinuxPowerEventSourceStub.Instance);
        services.AddSingleton<IPlatformStorageLayout>(storageLayout);
        services.AddSingleton<IStorageProtectionProvider>(failingProvisioner);
        services.AddSingleton<MonitorWorker>();

        var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<MonitorWorker>();

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(150);

        Assert.True(lifetime.StopRequested);
        Assert.Equal(MonitorWorker.FatalExitCode, Environment.ExitCode);
        Assert.Equal(SessionState.Interrupted, worker.Status.State);
        Assert.Contains("Storage boundary", worker.Status.Fault);
    }

    private sealed class FailingStorageProtectionProvider : IStorageProtectionProvider
    {
        public string PlatformName => "Failing";

        public Task<StorageProtectionObservation> ProvisionSessionBoundariesAsync(string sessionRoot, SessionLayoutDescriptor layout, CancellationToken ct = default) =>
            Task.FromResult(new StorageProtectionObservation(
                "fail-prov", layout.SessionId, DateTimeOffset.UtcNow, PlatformName, layout.LayoutVersion,
                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                StorageProtectionState.NotEstablished,
                RootBoundaryValid: false, ReparsePointCheck: false,
                DiagnosticMessage: "Namerna simulacija neuspele sigurnosne granice"));

        public Task<StorageProtectionObservation> VerifyStorageProtectionAsync(string sessionRoot, SessionLayoutDescriptor layout, CancellationToken ct = default) =>
            Task.FromResult(new StorageProtectionObservation(
                "fail-ver", layout.SessionId, DateTimeOffset.UtcNow, PlatformName, layout.LayoutVersion,
                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                StorageProtectionState.NotEstablished,
                RootBoundaryValid: false, ReparsePointCheck: false,
                DiagnosticMessage: "Namerna simulacija neuspele sigurnosne granice"));
    }

    private sealed class MockPosixStorageApi : ILinuxPosixStorageApi
    {
        private readonly Dictionary<string, PosixStat> _entries = new(StringComparer.Ordinal);
        private int _fdCounter = 100;
        private readonly Dictionary<int, string> _openFds = new();

        public void AddEntry(string path, bool isDir, bool isSymlink, int mode, uint uid, uint gid)
        {
            uint fullMode = (uint)mode;
            if (isSymlink) fullMode |= LinuxPosixStorageConstants.S_IFLNK;
            else if (isDir) fullMode |= LinuxPosixStorageConstants.S_IFDIR;
            else fullMode |= LinuxPosixStorageConstants.S_IFREG;

            _entries[path.TrimEnd('/')] = new PosixStat
            {
                Mode = fullMode,
                Uid = uid,
                Gid = gid
            };
        }

        public int Open(string path, int flags, int mode)
        {
            var norm = path.TrimEnd('/');
            if (_entries.ContainsKey(norm))
            {
                var fd = ++_fdCounter;
                _openFds[fd] = norm;
                return fd;
            }
            return -1;
        }

        public int OpenAt(int dirfd, string pathname, int flags, int mode)
        {
            if (!_openFds.TryGetValue(dirfd, out var baseDir)) return -1;
            var fullPath = $"{baseDir}/{pathname}".TrimEnd('/');
            if (_entries.ContainsKey(fullPath))
            {
                var fd = ++_fdCounter;
                _openFds[fd] = fullPath;
                return fd;
            }
            return -1;
        }

        public int OpenAt2(int dirfd, string pathname, ref OpenHow how)
        {
            return OpenAt(dirfd, pathname, (int)how.Flags, (int)how.Mode);
        }

        public int FstatAt(int dirfd, string pathname, out PosixStat statbuf, int flags)
        {
            string fullPath;
            if (dirfd == LinuxPosixStorageConstants.AT_FDCWD)
            {
                fullPath = pathname.TrimEnd('/');
            }
            else if (_openFds.TryGetValue(dirfd, out var baseDir))
            {
                fullPath = $"{baseDir}/{pathname}".TrimEnd('/');
            }
            else
            {
                statbuf = default;
                return -1;
            }

            if (_entries.TryGetValue(fullPath, out statbuf))
            {
                return 0;
            }
            return -1;
        }

        public int Fstat(int fd, out PosixStat statbuf)
        {
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out statbuf))
            {
                return 0;
            }
            statbuf = default;
            return -1;
        }

        public int MkdirAt(int dirfd, string pathname, int mode) => 0;
        public int Fchmod(int fd, int mode) => 0;
        public int Fchown(int fd, uint uid, uint gid) => 0;
        public int Close(int fd)
        {
            _openFds.Remove(fd);
            return 0;
        }
        public uint GetEuid() => 1000;
        public uint GetEgid() => 1000;
    }

    private sealed class LinuxProbeFactoryStub : IPlatformProbeFactory
    {
        public ValueTask<IPlatformLinkInspectionScope> CreateLinkInspectionAsync(string? interfaceName = null)
        {
            var inspector = new MockLinkInspector(LinkStatus.Up, "eth0", "00:11:22:33:44:55");
            return ValueTask.FromResult<IPlatformLinkInspectionScope>(new StubLinkInspectionScope(inspector));
        }

        public IRouteResolver CreateRouteResolver() => NullRouteResolver.Instance;
        public IBoundIcmp CreateBoundIcmp() => new MockBoundIcmp();
    }

    private sealed class StubLinkInspectionScope(ILinkInspector inspector) : IPlatformLinkInspectionScope
    {
        public ILinkInspector Inspector { get; } = inspector;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MockLinkInspector(LinkStatus status, string iface, string mac) : ILinkInspector
    {
        public LinkSnapshot Inspect() => new LinkSnapshot(iface, iface, status, LinkMedium.Ethernet)
        {
            MacAddress = mac,
            GatewayAddress = "192.168.1.1"
        };
    }

    private sealed class MockBoundIcmp : IBoundIcmp
    {
        public Task<IcmpEcho?> SendAsync(IPAddress destination, IPAddress source, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult<IcmpEcho?>(new IcmpEcho(true, false, TimeSpan.FromMilliseconds(5), 0));
    }

    private sealed class DummyHostLifetime : IHostApplicationLifetime
    {
        public bool StopRequested { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => StopRequested = true;
    }

    private sealed class LinuxPowerEventSourceStub : IPowerEventSource
    {
        public static readonly LinuxPowerEventSourceStub Instance = new();
        public IDisposable OnSuspending(Action callback) => new DummyDisposable();
        public IDisposable OnResumed(Action callback) => new DummyDisposable();
        public void Dispose() { }
        private sealed class DummyDisposable : IDisposable { public void Dispose() { } }
    }
}
