using System.Net;
using System.Runtime.InteropServices;
using System.Text;
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

    // R2-A: struct stat size must match Linux glibc x86_64 ABI exactly (144 bytes)
    [Fact]
    public void PosixStat_Matches_Glibc_X64_ABI()
    {
        Assert.Equal(144, Marshal.SizeOf<PosixStat>());
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

    // R2-B: Invariant 80: Existing session directory with 0755 mode FAILS closed and is NEVER fchmod repaired
    [Fact]
    public async Task Provision_Existing_Session_With_0755_Returns_NotEstablished_And_Never_Fchmods()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_0755", isDir: true, isSymlink: false, mode: 0x1ED, uid: 1000, gid: 1000); // 0755 drift!

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var guard = new LinuxSymlinkGuard(mock, policy);
        var provisioner = new LinuxSessionModeProvisioner(
            stateRoot: "/var/lib/internet-evidence-monitor",
            symlinkGuard: guard,
            posix: mock,
            ownershipPolicy: policy);

        var sessionDir = "/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_0755";
        var layout = SessionLayoutDescriptor.CreateStandard("20260820_0755");

        var provObs = await provisioner.ProvisionSessionBoundariesAsync(sessionDir, layout);
        Assert.Equal(StorageProtectionState.NotEstablished, provObs.ProtectionState);
        Assert.Contains("0755", provObs.DiagnosticMessage);
        Assert.Equal(0, mock.FchmodCallCount); // ZERO repair!
    }

    // R2-B: Invariant 80: Existing Raw area with 0750 mode FAILS closed and is NEVER repaired
    [Fact]
    public async Task Provision_Existing_Raw_With_0750_Returns_NotEstablished_And_Never_Repairs()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_0750", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_0750/Raw", isDir: true, isSymlink: false, mode: 0x1E8, uid: 1000, gid: 1000); // 0750 drift!

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var guard = new LinuxSymlinkGuard(mock, policy);
        var provisioner = new LinuxSessionModeProvisioner(
            stateRoot: "/var/lib/internet-evidence-monitor",
            symlinkGuard: guard,
            posix: mock,
            ownershipPolicy: policy);

        var sessionDir = "/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_0750";
        var layout = SessionLayoutDescriptor.CreateStandard("20260820_0750");

        var provObs = await provisioner.ProvisionSessionBoundariesAsync(sessionDir, layout);
        Assert.Equal(StorageProtectionState.NotEstablished, provObs.ProtectionState);
        Assert.Contains("0750", provObs.DiagnosticMessage);
        Assert.Equal(0, mock.FchmodCallCount); // ZERO repair!
    }

    // R2-D: Invariant 80: Existing layout.json with 0644 mode is NOT overwritten and refuses provisioning with O_EXCL
    [Fact]
    public async Task Provision_Existing_Layout_Refuses_Overwrite_With_O_EXCL()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_excl", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_excl/layout.json", isDir: false, isSymlink: false, mode: 0x1A4, uid: 1000, gid: 1000); // 0644 existing file!

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var guard = new LinuxSymlinkGuard(mock, policy);
        var provisioner = new LinuxSessionModeProvisioner(
            stateRoot: "/var/lib/internet-evidence-monitor",
            symlinkGuard: guard,
            posix: mock,
            ownershipPolicy: policy);

        var sessionDir = "/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_excl";
        var layout = SessionLayoutDescriptor.CreateStandard("20260820_excl");

        var provObs = await provisioner.ProvisionSessionBoundariesAsync(sessionDir, layout);
        Assert.Equal(StorageProtectionState.NotEstablished, provObs.ProtectionState);
        Assert.Contains("openat2", provObs.DiagnosticMessage);
    }

    // R2-B: Invariant 80: Wrong ownership is never fchown repaired
    [Fact]
    public async Task Existing_Wrong_Uid_Gid_Returns_NotEstablished_And_Never_Fchowns()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_uid", isDir: true, isSymlink: false, mode: 0x1C0, uid: 0, gid: 0); // Root owned drift!

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var guard = new LinuxSymlinkGuard(mock, policy);
        var provisioner = new LinuxSessionModeProvisioner(
            stateRoot: "/var/lib/internet-evidence-monitor",
            symlinkGuard: guard,
            posix: mock,
            ownershipPolicy: policy);

        var sessionDir = "/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_uid";
        var layout = SessionLayoutDescriptor.CreateStandard("20260820_uid");

        var provObs = await provisioner.ProvisionSessionBoundariesAsync(sessionDir, layout);
        Assert.Equal(StorageProtectionState.NotEstablished, provObs.ProtectionState);
        Assert.Equal(0, mock.FchownCallCount); // ZERO repair!
    }

    // R2-C: StateRoot in system mode with 0755 returns NotEstablished and remains 0755
    [Fact]
    public async Task StateRoot_With_0755_Returns_NotEstablished_And_Remains_0755()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1ED, uid: 1000, gid: 1000); // 0755 on StateRoot!

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var guard = new LinuxSymlinkGuard(mock, policy);
        var provisioner = new LinuxSessionModeProvisioner(
            stateRoot: "/var/lib/internet-evidence-monitor",
            symlinkGuard: guard,
            posix: mock,
            ownershipPolicy: policy);

        var sessionDir = "/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_stateroot";
        var layout = SessionLayoutDescriptor.CreateStandard("20260820_stateroot");

        var provObs = await provisioner.ProvisionSessionBoundariesAsync(sessionDir, layout);
        Assert.Equal(StorageProtectionState.NotEstablished, provObs.ProtectionState);
        Assert.Contains("0755", provObs.DiagnosticMessage);
        Assert.Equal(0, mock.FchmodCallCount);
    }

    // R2-C: StateRoot missing in system mode returns NotEstablished without trying to create it
    [Fact]
    public async Task StateRoot_Missing_In_System_Mode_Returns_NotEstablished()
    {
        var mock = new MockPosixStorageApi(); // StateRoot not added!

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var guard = new LinuxSymlinkGuard(mock, policy);
        var provisioner = new LinuxSessionModeProvisioner(
            stateRoot: "/var/lib/internet-evidence-monitor",
            symlinkGuard: guard,
            posix: mock,
            ownershipPolicy: policy);

        var sessionDir = "/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_missing";
        var layout = SessionLayoutDescriptor.CreateStandard("20260820_missing");

        var provObs = await provisioner.ProvisionSessionBoundariesAsync(sessionDir, layout);
        Assert.Equal(StorageProtectionState.NotEstablished, provObs.ProtectionState);
        Assert.Contains("ne postoji", provObs.DiagnosticMessage);
    }

    // R2-E: Verify reads layout from validated FD without pathname TOCTOU
    [Fact]
    public async Task Verify_Reads_Layout_From_Validated_FD_Without_Path_TOCTOU()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_fd", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_fd/Raw", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_fd/Evidence", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_fd/Derived", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_fd/Exports", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        var layout = SessionLayoutDescriptor.CreateStandard("20260820_fd");
        var layoutBytes = layout.ToCanonicalBytes();
        mock.AddEntry("/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_fd/layout.json", isDir: false, isSymlink: false, mode: 0x180, uid: 1000, gid: 1000, content: layoutBytes);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var guard = new LinuxSymlinkGuard(mock, policy);
        var provisioner = new LinuxSessionModeProvisioner(
            stateRoot: "/var/lib/internet-evidence-monitor",
            symlinkGuard: guard,
            posix: mock,
            ownershipPolicy: policy);

        var sessionDir = "/var/lib/internet-evidence-monitor/sessions/Sesija_20260820_fd";
        var verObs = await provisioner.VerifyStorageProtectionAsync(sessionDir, layout);
        Assert.Equal(StorageProtectionState.Established, verObs.ProtectionState);
        Assert.True(verObs.RootBoundaryValid);
        Assert.True(verObs.ReparsePointCheck);
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
        private sealed class FileEntry
        {
            public PosixStat Stat;
            public byte[] Content = Array.Empty<byte>();
        }

        private readonly Dictionary<string, FileEntry> _entries = new(StringComparer.Ordinal);
        private int _fdCounter = 100;
        private readonly Dictionary<int, string> _openFds = new();

        public bool FailOpenAt2 { get; set; }
        public int OpenAtCallCount { get; private set; }
        public int FchmodCallCount { get; private set; }
        public int FchownCallCount { get; private set; }

        public void AddEntry(string path, bool isDir, bool isSymlink, int mode, uint uid, uint gid, byte[]? content = null)
        {
            uint fullMode = (uint)mode;
            if (isSymlink) fullMode |= LinuxPosixStorageConstants.S_IFLNK;
            else if (isDir) fullMode |= LinuxPosixStorageConstants.S_IFDIR;
            else fullMode |= LinuxPosixStorageConstants.S_IFREG;

            var bytes = content ?? Array.Empty<byte>();
            _entries[path.TrimEnd('/')] = new FileEntry
            {
                Stat = new PosixStat
                {
                    Mode = fullMode,
                    Uid = uid,
                    Gid = gid,
                    Size = bytes.Length
                },
                Content = bytes
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

            bool exists = _entries.ContainsKey(fullPath);
            bool isOcreat = (how.Flags & (ulong)LinuxPosixStorageConstants.O_CREAT) != 0;
            bool isOexcl = (how.Flags & (ulong)LinuxPosixStorageConstants.O_EXCL) != 0;

            if (exists && isOcreat && isOexcl)
            {
                // O_CREAT | O_EXCL on existing file returns -1 (EEXIST)
                return -1;
            }

            if (!exists && isOcreat)
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

            if (_entries.TryGetValue(fullPath, out var entry))
            {
                statbuf = entry.Stat;
                return 0;
            }
            statbuf = default;
            return -1;
        }

        public int Fstat(int fd, out PosixStat statbuf)
        {
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var entry))
            {
                statbuf = entry.Stat;
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
            FchmodCallCount++;
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var entry))
            {
                uint cleanMode = (entry.Stat.Mode & ~0x1FFu) | (uint)(mode & 0x1FF);
                entry.Stat.Mode = cleanMode;
                return 0;
            }
            return -1;
        }

        public int Fchown(int fd, uint uid, uint gid)
        {
            FchownCallCount++;
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var entry))
            {
                entry.Stat.Uid = uid;
                entry.Stat.Gid = gid;
                return 0;
            }
            return -1;
        }

        public int Write(int fd, ReadOnlySpan<byte> buffer)
        {
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var entry))
            {
                entry.Content = buffer.ToArray();
                entry.Stat.Size = entry.Content.Length;
                return entry.Content.Length;
            }
            return -1;
        }

        public int Read(int fd, Span<byte> buffer)
        {
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var entry))
            {
                int toCopy = Math.Min(buffer.Length, entry.Content.Length);
                entry.Content.AsSpan(0, toCopy).CopyTo(buffer);
                return toCopy;
            }
            return -1;
        }

        public int Fsync(int fd) => 0;

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
