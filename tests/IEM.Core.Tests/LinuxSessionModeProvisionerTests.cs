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
    private readonly string _sessionsRoot;

    public LinuxSessionModeProvisionerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "iem_posix_tests_" + Guid.NewGuid().ToString("N"));
        _sessionsRoot = Path.Combine(_testRoot, "sessions");
        Directory.CreateDirectory(_testRoot);
        Directory.CreateDirectory(_sessionsRoot);
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

    // 1. Safe canonical tree: exact 0700 and 0600 yield Established
    [Fact]
    public void MockPosix_Exact_0700_Directories_And_0600_Layout_Yields_Established()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000); // 0700
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000); // 0700
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_1", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000); // 0700
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_1/Raw", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000); // 0700
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_1/layout.json", isDir: false, isSymlink: false, mode: 0x180, uid: 1000, gid: 1000); // 0600

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var guard = new LinuxSymlinkGuard(mock, policy);

        var resDir = guard.ValidatePath("/var/lib/internet-evidence-monitor", "/var/lib/internet-evidence-monitor/sessions/Sesija_1/Raw");
        Assert.True(resDir.IsSafe);
        Assert.Equal(StorageProtectionState.Established, resDir.State);

        var resFile = guard.ValidatePath("/var/lib/internet-evidence-monitor", "/var/lib/internet-evidence-monitor/sessions/Sesija_1/layout.json");
        Assert.True(resFile.IsSafe);
        Assert.Equal(StorageProtectionState.Established, resFile.State);
    }

    // 2. Exact mode truth: Directory mode drift (0755, 0750, 0770, 0777) FAILS closed
    [Theory]
    [InlineData(0x1ED)] // 0755
    [InlineData(0x1E8)] // 0750
    [InlineData(0x1F8)] // 0770
    [InlineData(0x1FF)] // 0777
    public void MockPosix_Rejects_Non_0700_Directory_Permissions(int mode)
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_1", isDir: true, isSymlink: false, mode: mode, uid: 1000, gid: 1000);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var guard = new LinuxSymlinkGuard(mock, policy);

        var result = guard.ValidatePath("/var/lib/internet-evidence-monitor", "/var/lib/internet-evidence-monitor/sessions/Sesija_1");
        Assert.False(result.IsSafe);
        Assert.Equal(StorageProtectionState.NotEstablished, result.State);
        Assert.Contains("0700", result.ViolationMessage);
    }

    // 3. Exact mode truth: File layout.json mode drift (0644, 0660, 0755) FAILS closed
    [Theory]
    [InlineData(0x1A4)] // 0644
    [InlineData(0x1B0)] // 0660
    [InlineData(0x1ED)] // 0755
    public void MockPosix_Rejects_Non_0600_Layout_Permissions(int mode)
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_1", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_1/layout.json", isDir: false, isSymlink: false, mode: mode, uid: 1000, gid: 1000);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var guard = new LinuxSymlinkGuard(mock, policy);

        var result = guard.ValidatePath("/var/lib/internet-evidence-monitor", "/var/lib/internet-evidence-monitor/sessions/Sesija_1/layout.json");
        Assert.False(result.IsSafe);
        Assert.Equal(StorageProtectionState.NotEstablished, result.State);
        Assert.Contains("0600", result.ViolationMessage);
    }

    // 4. Exact UID / GID ownership: Mismatches FAIL closed (never repaired silently)
    [Fact]
    public void MockPosix_Rejects_Wrong_Uid_Or_Gid()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_1", isDir: true, isSymlink: false, mode: 0x1C0, uid: 0, gid: 0); // root instead of iem

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var guard = new LinuxSymlinkGuard(mock, policy);

        var result = guard.ValidatePath("/var/lib/internet-evidence-monitor", "/var/lib/internet-evidence-monitor/sessions/Sesija_1");
        Assert.False(result.IsSafe);
        Assert.Equal(StorageProtectionState.NotEstablished, result.State);
        Assert.Contains("vlasnik", result.ViolationMessage, StringComparison.OrdinalIgnoreCase);
    }

    // 5. R1-D: Prefix collision check (e.g. trusted=/a/b, target=/a/bad) FAILS closed
    [Fact]
    public void Guard_Rejects_Prefix_Collision()
    {
        var guard = new LinuxSymlinkGuard();
        var result = guard.ValidatePath("/var/lib/iem", "/var/lib/iem-malicious/escape");
        Assert.False(result.IsSafe);
        Assert.Equal(StorageProtectionState.NotEstablished, result.State);
        Assert.Contains("van dozvoljenog korena", result.ViolationMessage);
    }

    // 6. R1-D: Path traversal escape '..' FAILS closed
    [Fact]
    public void Guard_Rejects_Directory_Traversal_Escape()
    {
        var guard = new LinuxSymlinkGuard();
        var result = guard.ValidatePath("/var/lib/internet-evidence-monitor", "/var/lib/internet-evidence-monitor/../etc/shadow");
        Assert.False(result.IsSafe);
        Assert.Equal(StorageProtectionState.NotEstablished, result.State);
        Assert.Contains("traversal", result.ViolationMessage, StringComparison.OrdinalIgnoreCase);
    }

    // 7. Symlink anywhere in path tree (sessions, sessionRoot, Raw, layout.json) FAILS closed
    [Theory]
    [InlineData("/var/lib/internet-evidence-monitor/sessions", true, false, false, false)]
    [InlineData("/var/lib/internet-evidence-monitor/sessions/Sesija_1", false, true, false, false)]
    [InlineData("/var/lib/internet-evidence-monitor/sessions/Sesija_1/Raw", false, false, true, false)]
    [InlineData("/var/lib/internet-evidence-monitor/sessions/Sesija_1/layout.json", false, false, false, true)]
    public void MockPosix_Rejects_Symlink_At_Any_Hierarchy_Level(string target, bool symlinkSessions, bool symlinkSession, bool symlinkRaw, bool symlinkLayout)
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions", isDir: true, isSymlink: symlinkSessions, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_1", isDir: true, isSymlink: symlinkSession, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_1/Raw", isDir: true, isSymlink: symlinkRaw, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_1/layout.json", isDir: false, isSymlink: symlinkLayout, mode: 0x180, uid: 1000, gid: 1000);

        var guard = new LinuxSymlinkGuard(mock);
        var result = guard.ValidatePath("/var/lib/internet-evidence-monitor", target);

        Assert.False(result.IsSafe);
        Assert.Equal(StorageProtectionState.NotEstablished, result.State);
        Assert.Contains("symlink", result.ViolationMessage, StringComparison.OrdinalIgnoreCase);
    }

    // 8. R1-E: openat2 failure (ENOSYS, EXDEV, ELOOP) returns NotEstablished and NEVER calls OpenAt fallback
    [Fact]
    public void MockPosix_OpenAt2_Failure_Returns_NotEstablished_Without_OpenAt_Fallback()
    {
        var mock = new MockPosixStorageApi
        {
            FailOpenAt2 = true // Simulates ENOSYS or EXDEV on openat2
        };
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_1", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        var guard = new LinuxSymlinkGuard(mock);
        var result = guard.ValidatePath("/var/lib/internet-evidence-monitor", "/var/lib/internet-evidence-monitor/sessions/Sesija_1");

        Assert.False(result.IsSafe);
        Assert.Equal(StorageProtectionState.NotEstablished, result.State);
        Assert.Contains("openat2", result.ViolationMessage);
        Assert.Equal(0, mock.OpenAtCallCount); // Verified: NO fallback to OpenAt!
    }

    // 9. R1-A: Missing IStorageProtectionProvider in DI fails fast on resolution
    [Fact]
    public void DI_Missing_StorageProtectionProvider_Fails_Fast()
    {
        var services = new ServiceCollection();
        services.Configure<MonitorSettings>(_ => { });
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, DummyHostLifetime>();
        services.AddSingleton<IPlatformProbeFactory, LinuxProbeFactoryStub>();
        services.AddSingleton<IPowerEventSource>(LinuxPowerEventSourceStub.Instance);
        services.AddSingleton<IPlatformStorageLayout>(new LinuxStorageLayout(stateRoot: _testRoot));
        // Note: IStorageProtectionProvider is deliberately NOT registered
        services.AddSingleton<MonitorWorker>();

        var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<MonitorWorker>());
        Assert.Contains("IStorageProtectionProvider", ex.Message);
    }

    // 10. Invariant 81: EvidenceRecorder.Start throws when session directory is not provisioned
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

    // 11. MonitorWorker fail-closed: StorageProtection NotEstablished aborts session start before probes or recorder start
    [Fact]
    public async Task MonitorWorker_Fails_Closed_When_Storage_Protection_Is_NotEstablished()
    {
        var services = new ServiceCollection();
        var lifetime = new DummyHostLifetime();
        var storageLayout = new LinuxStorageLayout(stateRoot: _testRoot);

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

    // 12. LinuxSessionModeProvisioner: Full provisioning and idempotent verification
    [Fact]
    public async Task Provisioner_Creates_And_Verifies_Valid_Hierarchy()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var guard = new LinuxSymlinkGuard(mock, policy);
        var provisioner = new LinuxSessionModeProvisioner(
            stateRoot: "/var/lib/internet-evidence-monitor",
            symlinkGuard: guard,
            posix: mock,
            ownershipPolicy: policy);

        var sessionDir = "/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_230000";
        var layout = SessionLayoutDescriptor.CreateStandard("20260820_230000");

        var provObs = await provisioner.ProvisionSessionBoundariesAsync(sessionDir, layout);
        Assert.True(provObs.ProtectionState == StorageProtectionState.Established, $"Provision failed: {provObs.DiagnosticMessage}");
        Assert.True(provObs.RootBoundaryValid);
        Assert.True(provObs.ReparsePointCheck);

        var verObs = await provisioner.VerifyStorageProtectionAsync(sessionDir, layout);
        Assert.True(verObs.ProtectionState == StorageProtectionState.Established, $"Verify failed: {verObs.DiagnosticMessage}");
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

        public bool FailOpenAt2 { get; set; }
        public int OpenAtCallCount { get; private set; }

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
            OpenAtCallCount++;
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
            if (FailOpenAt2) return -1;
            if (!_openFds.TryGetValue(dirfd, out var baseDir)) return -1;
            var fullPath = $"{baseDir}/{pathname}".TrimEnd('/');

            if (!_entries.ContainsKey(fullPath) && (how.Flags & (ulong)LinuxPosixStorageConstants.O_CREAT) != 0)
            {
                AddEntry(fullPath, isDir: false, isSymlink: false, mode: (int)how.Mode, uid: 1000, gid: 1000);
            }

            if (_entries.ContainsKey(fullPath))
            {
                var fd = ++_fdCounter;
                _openFds[fd] = fullPath;
                return fd;
            }
            return -1;
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

        public int MkdirAt(int dirfd, string pathname, int mode)
        {
            string fullPath;
            if (dirfd == LinuxPosixStorageConstants.AT_FDCWD) fullPath = pathname.TrimEnd('/');
            else if (_openFds.TryGetValue(dirfd, out var baseDir)) fullPath = $"{baseDir}/{pathname}".TrimEnd('/');
            else return -1;

            AddEntry(fullPath, isDir: true, isSymlink: false, mode: mode, uid: 1000, gid: 1000);
            return 0;
        }

        public int Fchmod(int fd, int mode)
        {
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var stat))
            {
                uint cleanMode = (stat.Mode & ~0x1FFu) | (uint)(mode & 0x1FF);
                _entries[path] = new PosixStat
                {
                    Mode = cleanMode,
                    Uid = stat.Uid,
                    Gid = stat.Gid
                };
                return 0;
            }
            return -1;
        }

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
