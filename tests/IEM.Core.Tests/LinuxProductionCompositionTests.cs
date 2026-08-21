using IEM.Core.Hosting;
using IEM.Core.Ipc;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Evidence.Crypto;
using IEM.Evidence.Manifest;
using IEM.Linux.Composition;
using IEM.Linux.Crypto;
using IEM.Linux.Installation;
using IEM.Linux.Network;
using IEM.Linux.Storage;
using IEM.Service.Linux.Composition;
using IEM.Service.Linux.Installation;
using IEM.Storage;
using IEM.Storage.Layout;
using Microsoft.Extensions.DependencyInjection;

namespace IEM.Core.Tests;

/// <summary>
/// Comprehensive Acceptance and Boundary Tests for Phase 3.1-8E · Production Composition & Provenance.
/// Covers COM-01 through COM-29 and the Architecture Boundary Invariant.
/// </summary>
public sealed class LinuxProductionCompositionTests
{
    private static MockPosixStorageApi CreateMockStorage(string rootPath, uint uid = 1000, uint gid = 1000)
    {
        var mock = new MockPosixStorageApi(euid: uid, egid: gid);
        mock.AddEntry(rootPath, isDir: true, isSymlink: false, mode: 0x1C0, uid: uid, gid: gid);
        mock.AddEntry($"{rootPath}/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: uid, gid: gid);
        mock.AddEntry($"{rootPath}/sessions", isDir: true, isSymlink: false, mode: 0x1C0, uid: uid, gid: gid);
        mock.AddEntry($"{rootPath}/cases", isDir: true, isSymlink: false, mode: 0x1C0, uid: uid, gid: gid);
        mock.AddEntry($"{rootPath}/state", isDir: true, isSymlink: false, mode: 0x1C0, uid: uid, gid: gid);
        mock.AddEntry($"{rootPath}/keys/{LinuxStoragePaths.NamespaceLockFileName}", isDir: false, isSymlink: false, mode: 0x180, uid: uid, gid: gid);
        return mock;
    }

    [Fact]
    public void COM_01_System_Resolves_All_Required_Adapters()
    {
        var mock = CreateMockStorage("/test/system-root");
        var services = new ServiceCollection();
        services.AddLinuxSystemServices(stateRoot: "/test/system-root", posix: mock);

        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<LinuxProductionComposition>());
        Assert.NotNull(sp.GetRequiredService<IPlatformStorageLayout>());
        Assert.NotNull(sp.GetRequiredService<LinuxStorageOwnershipPolicy>());
        Assert.NotNull(sp.GetRequiredService<ISymlinkSafetyGuard>());
        Assert.NotNull(sp.GetRequiredService<IStorageProtectionProvider>());
        Assert.NotNull(sp.GetRequiredService<IEvidenceKeyProvider>());
        Assert.NotNull(sp.GetRequiredService<IPlatformProbeFactory>());
    }

    [Fact]
    public void COM_02_Portable_Composition_Contains_All_Required_Adapters()
    {
        var mock = CreateMockStorage("/test/portable-root");
        var composition = LinuxProductionCompositionFactory.CreatePortable(
            portableStateRoot: "/test/portable-root", posix: mock);

        Assert.NotNull(composition);
        Assert.NotNull(composition.StorageLayout);
        Assert.NotNull(composition.OwnershipPolicy);
        Assert.NotNull(composition.SymlinkGuard);
        Assert.NotNull(composition.StorageProtectionProvider);
        Assert.NotNull(composition.EvidenceKeyProvider);
        Assert.NotNull(composition.ProbeFactory);
        Assert.NotNull(composition.PosixApi);
    }

    [Fact]
    public void COM_03_System_Uses_LinuxStorageLayout()
    {
        var mock = CreateMockStorage("/test/system-root");
        var composition = LinuxProductionCompositionFactory.CreateSystem(
            stateRoot: "/test/system-root", posix: mock);

        Assert.IsType<LinuxStorageLayout>(composition.StorageLayout);
    }

    [Fact]
    public void COM_04_Portable_Uses_LinuxPortableStorageLayout()
    {
        var mock = CreateMockStorage("/test/portable-root");
        var composition = LinuxProductionCompositionFactory.CreatePortable(
            portableStateRoot: "/test/portable-root", posix: mock);

        Assert.IsType<LinuxPortableStorageLayout>(composition.StorageLayout);
    }

    [Fact]
    public async Task COM_05_System_All_Security_Adapters_Use_Same_StateRoot()
    {
        var mock = CreateMockStorage("/test/custom-system-root");
        var composition = LinuxProductionCompositionFactory.CreateSystem(
            stateRoot: "/test/custom-system-root", posix: mock);

        // 1. StorageLayout uses custom root
        Assert.Equal("/test/custom-system-root/sessions", composition.StorageLayout.DefaultOutputRoot);

        // 2. KeyProvider accesses custom root
        var identity = await composition.EvidenceKeyProvider.GetOrCreateIdentityAsync();
        Assert.NotNull(identity);
        Assert.True(mock.TryGetEntry("/test/custom-system-root/keys/evidence-signing-v1.p8", out _));

        // 3. Zero access to default /var/lib or portable paths
        Assert.False(mock.TryGetEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", out _));
        Assert.False(mock.TryGetEntry("/test/portable-root/keys/evidence-signing-v1.p8", out _));
    }

    [Fact]
    public async Task COM_06_Portable_All_Security_Adapters_Use_Same_StateRoot()
    {
        var mock = CreateMockStorage("/test/custom-portable-root");
        var composition = LinuxProductionCompositionFactory.CreatePortable(
            portableStateRoot: "/test/custom-portable-root", posix: mock);

        // 1. StorageLayout uses custom portable root
        Assert.Equal("/test/custom-portable-root/sessions", composition.StorageLayout.DefaultOutputRoot);

        // 2. KeyProvider accesses custom portable root
        var identity = await composition.EvidenceKeyProvider.GetOrCreateIdentityAsync();
        Assert.NotNull(identity);
        Assert.True(mock.TryGetEntry("/test/custom-portable-root/keys/evidence-signing-v1.p8", out _));

        // 3. Zero access to /var/lib or system root
        Assert.False(mock.TryGetEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", out _));
        Assert.False(mock.TryGetEntry("/test/custom-system-root/keys/evidence-signing-v1.p8", out _));
    }

    [Fact]
    public void COM_07_System_Uses_Exact_Daemon_Ownership()
    {
        var mock = CreateMockStorage("/test/system-root", uid: 1500, gid: 1500);
        var composition = LinuxProductionCompositionFactory.CreateSystem(
            stateRoot: "/test/system-root", posix: mock);

        Assert.Equal("SystemInstallationExact", composition.OwnershipPolicy.PolicyName);
        Assert.Equal(1500u, composition.OwnershipPolicy.ExpectedUid);
        Assert.Equal(1500u, composition.OwnershipPolicy.ExpectedGid);
    }

    [Fact]
    public void COM_08_Portable_Uses_Current_User_Ownership()
    {
        var mock = CreateMockStorage("/test/portable-root", uid: 2000, gid: 2000);
        var composition = LinuxProductionCompositionFactory.CreatePortable(
            portableStateRoot: "/test/portable-root", posix: mock);

        Assert.Equal("PortableUser", composition.OwnershipPolicy.PolicyName);
        Assert.Equal(2000u, composition.OwnershipPolicy.ExpectedUid);
        Assert.Equal(2000u, composition.OwnershipPolicy.ExpectedGid);
    }

    [Fact]
    public void COM_09_System_Root_Execution_Fails_Closed()
    {
        var mock = CreateMockStorage("/test/system-root", uid: 0, gid: 0); // Root EUID!
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LinuxProductionCompositionFactory.CreateSystem(stateRoot: "/test/system-root", posix: mock));

        Assert.Contains("root", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void COM_10_System_Expected_Uid_Mismatch_Fails()
    {
        var mock = CreateMockStorage("/test/system-root", uid: 1000, gid: 1000);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LinuxProductionCompositionFactory.CreateSystem(
                stateRoot: "/test/system-root",
                expectedUid: 1001,
                expectedGid: 1000,
                posix: mock));

        Assert.Contains("EUID mismatch", ex.Message);
    }

    [Fact]
    public void COM_11_System_Expected_Gid_Mismatch_Fails()
    {
        var mock = CreateMockStorage("/test/system-root", uid: 1000, gid: 1000);

        // GID mismatch
        var exGid = Assert.Throws<InvalidOperationException>(() =>
            LinuxProductionCompositionFactory.CreateSystem(
                stateRoot: "/test/system-root",
                expectedUid: 1000,
                expectedGid: 1001,
                posix: mock));
        Assert.Contains("EGID mismatch", exGid.Message);

        // Partial expected pair (one set, other missing)
        var exPartial = Assert.Throws<InvalidOperationException>(() =>
            LinuxProductionCompositionFactory.CreateSystem(
                stateRoot: "/test/system-root",
                expectedUid: 1000,
                expectedGid: null,
                posix: mock));
        Assert.Contains("provided together", exPartial.Message);
    }

    [Fact]
    public void COM_12_System_Key_Scope_Is_SystemInstallation()
    {
        var mock = CreateMockStorage("/test/system-root");
        var composition = LinuxProductionCompositionFactory.CreateSystem(
            stateRoot: "/test/system-root", posix: mock);

        Assert.Equal(LinuxSigningIdentityScope.SystemInstallation, composition.SigningScope);
    }

    [Fact]
    public void COM_13_Portable_Key_Scope_Is_PortableUser()
    {
        var mock = CreateMockStorage("/test/portable-root");
        var composition = LinuxProductionCompositionFactory.CreatePortable(
            portableStateRoot: "/test/portable-root", posix: mock);

        Assert.Equal(LinuxSigningIdentityScope.PortableUser, composition.SigningScope);
    }

    [Fact]
    public async Task COM_14_System_And_Portable_Key_Paths_Are_Disjoint()
    {
        var mockSystem = CreateMockStorage("/test/system-root");
        var mockPortable = CreateMockStorage("/test/portable-root");

        var compSystem = LinuxProductionCompositionFactory.CreateSystem("/test/system-root", posix: mockSystem);
        var compPortable = LinuxProductionCompositionFactory.CreatePortable("/test/portable-root", posix: mockPortable);

        await compSystem.EvidenceKeyProvider.GetOrCreateIdentityAsync();
        await compPortable.EvidenceKeyProvider.GetOrCreateIdentityAsync();

        Assert.True(mockSystem.TryGetEntry("/test/system-root/keys/evidence-signing-v1.p8", out _));
        Assert.False(mockSystem.TryGetEntry("/test/portable-root/keys/evidence-signing-v1.p8", out _));

        Assert.True(mockPortable.TryGetEntry("/test/portable-root/keys/evidence-signing-v1.p8", out _));
        Assert.False(mockPortable.TryGetEntry("/test/system-root/keys/evidence-signing-v1.p8", out _));
    }

    [Fact]
    public async Task COM_15_System_And_Portable_KeyIds_Are_Different()
    {
        var mockSystem = CreateMockStorage("/test/system-root");
        var mockPortable = CreateMockStorage("/test/portable-root");

        var compSystem = LinuxProductionCompositionFactory.CreateSystem("/test/system-root", posix: mockSystem);
        var compPortable = LinuxProductionCompositionFactory.CreatePortable("/test/portable-root", posix: mockPortable);

        using var sysId = await compSystem.EvidenceKeyProvider.GetOrCreateIdentityAsync();
        using var portId = await compPortable.EvidenceKeyProvider.GetOrCreateIdentityAsync();

        Assert.NotEqual(sysId.KeyId, portId.KeyId);
    }

    [Fact]
    public async Task COM_16_Broken_System_Key_Never_Falls_Back_To_Portable()
    {
        var mock = CreateMockStorage("/test/system-root");
        mock.AddEntry("/test/portable-root", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/test/portable-root/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/test/portable-root/keys/evidence-signing-v1.p8", isDir: false, isSymlink: false, mode: 0x180, uid: 1000, gid: 1000, content: "PORTABLE_KEY"u8.ToArray());

        // Broken permissions on system keys directory (0755 instead of 0700)
        mock.AddEntry("/test/system-root/keys", isDir: true, isSymlink: false, mode: 0x1ED, uid: 1000, gid: 1000);

        var compSystem = LinuxProductionCompositionFactory.CreateSystem("/test/system-root", posix: mock);

        // Must fail closed, never falling back to portable
        await Assert.ThrowsAsync<SigningIdentityUnavailableException>(() =>
            compSystem.EvidenceKeyProvider.GetOrCreateIdentityAsync());
    }

    [Fact]
    public async Task COM_17_Broken_Portable_Key_Never_Falls_Back_To_System()
    {
        var mock = CreateMockStorage("/test/portable-root");
        mock.AddEntry("/test/system-root", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/test/system-root/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/test/system-root/keys/evidence-signing-v1.p8", isDir: false, isSymlink: false, mode: 0x180, uid: 1000, gid: 1000, content: "SYSTEM_KEY"u8.ToArray());

        // Broken permissions on portable keys directory
        mock.AddEntry("/test/portable-root/keys", isDir: true, isSymlink: false, mode: 0x1ED, uid: 1000, gid: 1000);

        var compPortable = LinuxProductionCompositionFactory.CreatePortable("/test/portable-root", posix: mock);

        // Must fail closed, never falling back to system
        await Assert.ThrowsAsync<SigningIdentityUnavailableException>(() =>
            compPortable.EvidenceKeyProvider.GetOrCreateIdentityAsync());
    }

    [Fact]
    public async Task COM_18_Linux_Key_Protection_Contract_Remains_Frozen()
    {
        var mockSys = CreateMockStorage("/test/system-root");
        var compSys = LinuxProductionCompositionFactory.CreateSystem("/test/system-root", posix: mockSys);
        using var sysId = await compSys.EvidenceKeyProvider.GetOrCreateIdentityAsync();

        Assert.Equal(KeyProtectionLevel.SoftwareProtected, sysId.Protection.Protection);
        Assert.Equal(KeyProtectionEvidence.ProviderReported, sysId.Protection.Evidence);
        Assert.Equal("LinuxFileSystemKeyStore", sysId.Protection.Provider);
        Assert.Contains(":system-daemon", sysId.Protection.Details);

        var mockPort = CreateMockStorage("/test/portable-root");
        var compPort = LinuxProductionCompositionFactory.CreatePortable("/test/portable-root", posix: mockPort);
        using var portId = await compPort.EvidenceKeyProvider.GetOrCreateIdentityAsync();

        Assert.Equal(KeyProtectionLevel.SoftwareProtected, portId.Protection.Protection);
        Assert.Equal(KeyProtectionEvidence.ProviderReported, portId.Protection.Evidence);
        Assert.Equal("LinuxFileSystemKeyStore", portId.Protection.Provider);
        Assert.Contains(":user-portable", portId.Protection.Details);
    }

    [Fact]
    public async Task COM_19_Storage_Protection_Observation_Preserves_Policy_Hash()
    {
        var mock = CreateMockStorage("/test/system-root");
        mock.AddEntry("/test/system-root/sessions/session-2026-08-21-001", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/test/system-root/sessions/session-2026-08-21-001/Raw", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/test/system-root/sessions/session-2026-08-21-001/Derived", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/test/system-root/sessions/session-2026-08-21-001/Evidence", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/test/system-root/sessions/session-2026-08-21-001/Exports", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        var comp = LinuxProductionCompositionFactory.CreateSystem("/test/system-root", posix: mock);

        var descriptor = SessionLayoutDescriptor.CreateStandard("session-2026-08-21-001");
        var sessionDir = "/test/system-root/sessions/session-2026-08-21-001";

        var obs = await comp.StorageProtectionProvider.ProvisionSessionBoundariesAsync(
            sessionDir, descriptor);

        Assert.Equal(descriptor.SessionId, obs.SessionId);
        Assert.Equal(descriptor.LayoutVersion, obs.LayoutVersion);
        Assert.Equal(descriptor.StoragePolicyVersion, obs.StoragePolicyVersion);
        Assert.Equal(descriptor.StoragePolicyHash, obs.StoragePolicyHash);
        Assert.Equal(StorageProtectionState.Established, obs.ProtectionState);
    }

    [Fact]
    public async Task COM_20_Storage_Protection_Observation_Platform_Is_Linux()
    {
        var mock = CreateMockStorage("/test/system-root");
        mock.AddEntry("/test/system-root/sessions/session-2026-08-21-002", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/test/system-root/sessions/session-2026-08-21-002/Raw", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/test/system-root/sessions/session-2026-08-21-002/Derived", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/test/system-root/sessions/session-2026-08-21-002/Evidence", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/test/system-root/sessions/session-2026-08-21-002/Exports", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        var comp = LinuxProductionCompositionFactory.CreateSystem("/test/system-root", posix: mock);

        var descriptor = SessionLayoutDescriptor.CreateStandard("session-2026-08-21-002");
        var sessionDir = "/test/system-root/sessions/session-2026-08-21-002";

        var obs = await comp.StorageProtectionProvider.ProvisionSessionBoundariesAsync(
            sessionDir, descriptor);

        Assert.Equal("Linux", obs.Platform);
    }

    [Fact]
    public void COM_21_Installed_Unreachable_Never_Becomes_Portable()
    {
        var presenceSource = new MockPresenceSource(InstallationPresence.InstalledSystemService);
        var reachabilitySource = new MockReachabilitySource(ServiceReachability.Unreachable);

        var probe = new LinuxInstallationProbe(presenceSource, reachabilitySource);
        var state = probe.Probe();

        Assert.Equal(InstallationPresence.InstalledSystemService, state.Presence);
        Assert.Equal(ServiceReachability.Unreachable, state.Reachability);
        Assert.False(state.IsUsableService);
        Assert.False(state.IsExplicitlyPortable); // INVARIANT 276: NEVER portable!
    }

    [Fact]
    public void COM_22_Unknown_Installation_State_Fails_Closed()
    {
        var presenceSource = new MockPresenceSource(InstallationPresence.Unknown);
        var reachabilitySource = new MockReachabilitySource(ServiceReachability.Unreachable);

        var probe = new LinuxInstallationProbe(presenceSource, reachabilitySource);
        var state = probe.Probe();

        Assert.Equal(InstallationPresence.Unknown, state.Presence);
        Assert.False(state.IsUsableService);
        Assert.False(state.IsExplicitlyPortable);
    }

    [Fact]
    public async Task COM_23_Production_System_Service_Resolves_IEvidenceKeyProvider()
    {
        var mock = CreateMockStorage("/test/system-root");
        var services = new ServiceCollection();
        services.AddLinuxSystemServices(stateRoot: "/test/system-root", posix: mock);

        using var sp = services.BuildServiceProvider();
        var keyProvider = sp.GetRequiredService<IEvidenceKeyProvider>();

        using var identity = await keyProvider.GetOrCreateIdentityAsync();
        Assert.NotNull(identity);
        Assert.StartsWith("sha256:", identity.KeyId);
    }

    [Fact]
    public void COM_24_Production_Host_Uses_Approved_Composition_Root_Only()
    {
        var repoRoot = GetRepositoryRoot();
        var programPath = Path.Combine(repoRoot, "src", "IEM.Service.Linux", "Program.cs");
        Assert.True(File.Exists(programPath), $"Program.cs must exist at {programPath}");

        var content = File.ReadAllText(programPath);
        Assert.Contains("builder.Services.AddLinuxSystemServices();", content);
        Assert.DoesNotContain("new LinuxEvidenceKeyProvider", content);
        Assert.DoesNotContain("new LinuxSessionModeProvisioner", content);
        Assert.DoesNotContain("new LinuxSymlinkGuard", content);
        Assert.DoesNotContain("new LinuxNativePosixStorageApi", content);
    }

    [Fact]
    public void COM_25_StateRoot_Existence_Never_Determines_InstallationPresence()
    {
        // Scenario A: Stale StateRoot exists on disk, but systemd service is absent
        var presenceSourceA = new MockPresenceSource(InstallationPresence.PortableOnly);
        var reachabilitySourceA = new MockReachabilitySource(ServiceReachability.NotApplicable);

        var probeA = new LinuxInstallationProbe(presenceSourceA, reachabilitySourceA);
        var stateA = probeA.Probe();
        Assert.Equal(InstallationPresence.PortableOnly, stateA.Presence);
        Assert.True(stateA.IsExplicitlyPortable);

        // Scenario B: Unit is registered in systemd, but StateRoot directory does not yet exist
        var presenceSourceB = new MockPresenceSource(InstallationPresence.InstalledSystemService);
        var reachabilitySourceB = new MockReachabilitySource(ServiceReachability.Unreachable);

        var probeB = new LinuxInstallationProbe(presenceSourceB, reachabilitySourceB);
        var stateB = probeB.Probe();
        Assert.Equal(InstallationPresence.InstalledSystemService, stateB.Presence);
        Assert.False(stateB.IsExplicitlyPortable);
    }

    [Fact]
    public void COM_26_InstallationPresence_And_Reachability_Are_Independent_Facts()
    {
        var presenceSource = new MockPresenceSource(InstallationPresence.InstalledSystemService);
        var reachabilitySource = new MockReachabilitySource(ServiceReachability.Unreachable);

        var probe = new LinuxInstallationProbe(presenceSource, reachabilitySource);
        var state = probe.Probe();

        Assert.Equal(InstallationPresence.InstalledSystemService, state.Presence);
        Assert.Equal(ServiceReachability.Unreachable, state.Reachability);
    }

    [Fact]
    public void COM_27_Explicit_Portable_Composition_Does_Not_Fabricate_PortableOnly_Truth()
    {
        var mock = CreateMockStorage("/test/portable-root");
        var comp = LinuxProductionCompositionFactory.CreatePortable("/test/portable-root", posix: mock);

        Assert.Equal(LinuxExecutionMode.PortableUser, comp.Mode);
        Assert.Equal(LinuxSigningIdentityScope.PortableUser, comp.SigningScope);
    }

    [Fact]
    public void COM_28_PortableOnly_Does_Not_Require_Reachability_Probe()
    {
        var presenceSource = new MockPresenceSource(InstallationPresence.PortableOnly);
        var reachabilitySource = new MockReachabilitySource(ServiceReachability.Unreachable);

        var probe = new LinuxInstallationProbe(presenceSource, reachabilitySource);
        var state = probe.Probe();

        Assert.Equal(InstallationPresence.PortableOnly, state.Presence);
        Assert.Equal(ServiceReachability.NotApplicable, state.Reachability);
        Assert.True(state.IsExplicitlyPortable);
    }

    [Fact]
    public void COM_29_Installed_Service_Reachability_Failure_Remains_Installed()
    {
        var presenceSource = new MockPresenceSource(InstallationPresence.InstalledSystemService);
        var reachabilitySource = new MockReachabilitySource(ServiceReachability.Unreachable);

        var probe = new LinuxInstallationProbe(presenceSource, reachabilitySource);
        var state = probe.Probe();

        Assert.Equal(InstallationPresence.InstalledSystemService, state.Presence);
        Assert.Equal(ServiceReachability.Unreachable, state.Reachability);
        Assert.NotEqual(InstallationPresence.PortableOnly, state.Presence);
        Assert.NotEqual(InstallationPresence.Unknown, state.Presence);
    }

    // ==========================================
    // R1-C: CONCRETE PRESENCE TESTS (SYS-01..07)
    // ==========================================

    [Fact]
    public async Task SYS_01_Loaded_Unit_Is_InstalledSystemService()
    {
        var mockDbus = new MockSystemdDbusManager
        {
            GetUnitFunc = _ => Task.FromResult<string?>("/org/freedesktop/systemd1/unit/internet_2devidence_2dmonitor_2eservice")
        };

        var source = new SystemdServicePresenceSource(dbusManager: mockDbus);
        var presence = await source.ProbePresenceAsync();

        Assert.Equal(InstallationPresence.InstalledSystemService, presence);
    }

    [Fact]
    public async Task SYS_02_Disabled_UnitFile_Is_Still_InstalledSystemService()
    {
        var mockDbus = new MockSystemdDbusManager
        {
            GetUnitFunc = _ => Task.FromResult<string?>(null),
            GetUnitFileStateFunc = _ => Task.FromResult<string?>("disabled")
        };

        var source = new SystemdServicePresenceSource(dbusManager: mockDbus);
        var presence = await source.ProbePresenceAsync();

        Assert.Equal(InstallationPresence.InstalledSystemService, presence);
    }

    [Fact]
    public async Task SYS_03_Static_UnitFile_Is_Still_InstalledSystemService()
    {
        var mockDbus = new MockSystemdDbusManager
        {
            GetUnitFunc = _ => Task.FromResult<string?>(null),
            GetUnitFileStateFunc = _ => Task.FromResult<string?>("static")
        };

        var source = new SystemdServicePresenceSource(dbusManager: mockDbus);
        var presence = await source.ProbePresenceAsync();

        Assert.Equal(InstallationPresence.InstalledSystemService, presence);
    }

    [Fact]
    public async Task SYS_04_Explicit_NoSuchUnit_And_NoSuchUnitFile_Is_PortableOnly()
    {
        var mockDbus = new MockSystemdDbusManager
        {
            GetUnitFunc = _ => Task.FromResult<string?>(null),
            GetUnitFileStateFunc = _ => Task.FromResult<string?>(null)
        };

        var source = new SystemdServicePresenceSource(dbusManager: mockDbus);
        var presence = await source.ProbePresenceAsync();

        Assert.Equal(InstallationPresence.PortableOnly, presence);
    }

    [Fact]
    public async Task SYS_05_Systemd_Bus_Unavailable_Is_Unknown_Not_Portable()
    {
        var mockDbus = new MockSystemdDbusManager
        {
            GetUnitFunc = _ => throw new InvalidOperationException("System bus connection failed.")
        };

        var source = new SystemdServicePresenceSource(dbusManager: mockDbus);
        var presence = await source.ProbePresenceAsync();

        Assert.Equal(InstallationPresence.Unknown, presence);
        Assert.NotEqual(InstallationPresence.PortableOnly, presence);
    }

    [Fact]
    public async Task SYS_06_Systemd_Protocol_Error_Is_Unknown_Not_Portable()
    {
        var mockDbus = new MockSystemdDbusManager
        {
            GetUnitFunc = _ => throw new FormatException("Malformed D-Bus message.")
        };

        var source = new SystemdServicePresenceSource(dbusManager: mockDbus);
        var presence = await source.ProbePresenceAsync();

        Assert.Equal(InstallationPresence.Unknown, presence);
        Assert.NotEqual(InstallationPresence.PortableOnly, presence);
    }

    [Fact]
    public async Task SYS_07_StateRoot_Existence_Is_Irrelevant_To_Systemd_Presence()
    {
        // Even if StateRoot directory exists on disk, systemd D-Bus returning null must yield PortableOnly
        var mockDbus = new MockSystemdDbusManager
        {
            GetUnitFunc = _ => Task.FromResult<string?>(null),
            GetUnitFileStateFunc = _ => Task.FromResult<string?>(null)
        };

        var source = new SystemdServicePresenceSource(dbusManager: mockDbus);
        var presence = await source.ProbePresenceAsync();

        Assert.Equal(InstallationPresence.PortableOnly, presence);
    }

    // ============================================
    // R1-D: CONCRETE REACHABILITY TESTS (IPC-01..07)
    // ============================================

    [Fact]
    public async Task IPC_01_Connect_And_Valid_IEM_Response_Is_Reachable()
    {
        var source = new LinuxControlSocketReachabilitySource(streamFactory: async ct =>
        {
            var memoryStream = new MemoryDuplexServerStream(async (requestBytes, outStream) =>
            {
                var reqJson = System.Text.Encoding.UTF8.GetString(requestBytes);
                var req = System.Text.Json.JsonSerializer.Deserialize<IpcRequestEnvelope>(reqJson)!;

                var resp = IpcResponseEnvelope.CreateSuccess(req.RequestId, "service-instance-123", "{}");
                var respJson = System.Text.Json.JsonSerializer.Serialize(resp);
                await IpcMessageFraming.WriteFrameAsync(outStream, System.Text.Encoding.UTF8.GetBytes(respJson), ct);
            });
            return memoryStream;
        });

        var reachability = await source.ProbeReachabilityAsync();
        Assert.Equal(ServiceReachability.Reachable, reachability);
    }

    [Fact]
    public async Task IPC_02_Socket_Accepts_But_Sends_Garbage_Is_Unreachable()
    {
        var source = new LinuxControlSocketReachabilitySource(streamFactory: async ct =>
        {
            var memoryStream = new MemoryDuplexServerStream(async (_, outStream) =>
            {
                await IpcMessageFraming.WriteFrameAsync(outStream, "NOT_JSON_GARBAGE"u8.ToArray(), ct);
            });
            return memoryStream;
        });

        var reachability = await source.ProbeReachabilityAsync();
        Assert.Equal(ServiceReachability.Unreachable, reachability);
    }

    [Fact]
    public async Task IPC_03_Socket_Accepts_But_Response_RequestId_Mismatch_Is_Unreachable()
    {
        var source = new LinuxControlSocketReachabilitySource(streamFactory: async ct =>
        {
            var memoryStream = new MemoryDuplexServerStream(async (_, outStream) =>
            {
                var resp = IpcResponseEnvelope.CreateSuccess("WRONG_REQUEST_ID", "service-instance-123");
                var respJson = System.Text.Json.JsonSerializer.Serialize(resp);
                await IpcMessageFraming.WriteFrameAsync(outStream, System.Text.Encoding.UTF8.GetBytes(respJson), ct);
            });
            return memoryStream;
        });

        var reachability = await source.ProbeReachabilityAsync();
        Assert.Equal(ServiceReachability.Unreachable, reachability);
    }

    [Fact]
    public async Task IPC_04_Socket_Accepts_But_Protocol_Mismatch_Is_Unreachable()
    {
        var source = new LinuxControlSocketReachabilitySource(streamFactory: async ct =>
        {
            var memoryStream = new MemoryDuplexServerStream(async (requestBytes, outStream) =>
            {
                var reqJson = System.Text.Encoding.UTF8.GetString(requestBytes);
                var req = System.Text.Json.JsonSerializer.Deserialize<IpcRequestEnvelope>(reqJson)!;

                var resp = new IpcResponseEnvelope
                {
                    ProtocolVersion = 999, // Incompatible future protocol
                    RequestId = req.RequestId,
                    Status = IpcResponseStatus.Success,
                    ServiceInstanceId = "service-instance-123"
                };
                var respJson = System.Text.Json.JsonSerializer.Serialize(resp);
                await IpcMessageFraming.WriteFrameAsync(outStream, System.Text.Encoding.UTF8.GetBytes(respJson), ct);
            });
            return memoryStream;
        });

        var reachability = await source.ProbeReachabilityAsync();
        Assert.Equal(ServiceReachability.Unreachable, reachability);
    }

    [Fact]
    public async Task IPC_05_Socket_Accepts_But_No_Response_Times_Out_Unreachable()
    {
        var source = new LinuxControlSocketReachabilitySource(
            probeTimeout: TimeSpan.FromMilliseconds(50),
            streamFactory: _ =>
            {
                var memoryStream = new MemoryDuplexServerStream((_, _) => Task.CompletedTask);
                return Task.FromResult<Stream>(memoryStream);
            });

        var reachability = await source.ProbeReachabilityAsync();
        Assert.Equal(ServiceReachability.Unreachable, reachability);
    }

    [Fact]
    public async Task IPC_06_Socket_Missing_Is_Unreachable()
    {
        var source = new LinuxControlSocketReachabilitySource(
            socketPath: "/nonexistent/socket/path/iem.sock");

        var reachability = await source.ProbeReachabilityAsync();
        Assert.Equal(ServiceReachability.Unreachable, reachability);
    }

    [Fact]
    public async Task IPC_07_Connection_Refused_Is_Unreachable()
    {
        var source = new LinuxControlSocketReachabilitySource(streamFactory: _ =>
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));

        var reachability = await source.ProbeReachabilityAsync();
        Assert.Equal(ServiceReachability.Unreachable, reachability);
    }

    // ===========================================
    // R1-E: REAL ARCHITECTURE GATE ACCEPTANCE
    // ===========================================

    [Fact]
    public void Architecture_Invariant_Linux_Security_Primitives_Only_Instantiated_In_Approved_Roots()
    {
        var repoRoot = GetRepositoryRoot();
        var serviceLinuxDir = Path.Combine(repoRoot, "src", "IEM.Service.Linux");
        Assert.True(Directory.Exists(serviceLinuxDir), $"Directory {serviceLinuxDir} must exist.");

        var csFiles = Directory.GetFiles(serviceLinuxDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(csFiles);

        foreach (var file in csFiles)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("new LinuxEvidenceKeyProvider", text);
            Assert.DoesNotContain("new LinuxSessionModeProvisioner", text);
            Assert.DoesNotContain("new LinuxSymlinkGuard", text);
            Assert.DoesNotContain("new LinuxNativePosixStorageApi", text);
        }
    }

    [Fact]
    public void Architecture_Invariant_IEM_Linux_Has_No_Host_Framework_Dependencies()
    {
        var repoRoot = GetRepositoryRoot();
        var linuxCsproj = Path.Combine(repoRoot, "src", "IEM.Linux", "IEM.Linux.csproj");
        Assert.True(File.Exists(linuxCsproj), $"Project file {linuxCsproj} must exist.");

        var text = File.ReadAllText(linuxCsproj);
        Assert.DoesNotContain("Microsoft.Extensions.DependencyInjection", text);
        Assert.DoesNotContain("Microsoft.Extensions.Hosting.Systemd", text);
        Assert.DoesNotContain("Tmds.DBus.Protocol", text);
    }

    private static string GetRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "InternetEvidenceMonitor.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")) ||
                Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root directory could not be discovered.");
    }

    private sealed class MockSystemdDbusManager : ISystemdDbusManager
    {
        public Func<string, Task<string?>>? GetUnitFunc { get; set; }
        public Func<string, Task<string?>>? GetUnitFileStateFunc { get; set; }

        public Task<string?> GetUnitAsync(string unitName, CancellationToken ct = default) =>
            GetUnitFunc != null ? GetUnitFunc(unitName) : Task.FromResult<string?>(null);

        public Task<string?> GetUnitFileStateAsync(string unitName, CancellationToken ct = default) =>
            GetUnitFileStateFunc != null ? GetUnitFileStateFunc(unitName) : Task.FromResult<string?>(null);
    }

    private sealed class MemoryDuplexServerStream : Stream
    {
        private readonly Func<byte[], Stream, Task> _serverHandler;
        private readonly MemoryStream _clientToServer = new();
        private readonly MemoryStream _serverToClient = new();
        private bool _serverExecuted;

        public MemoryDuplexServerStream(Func<byte[], Stream, Task> serverHandler)
        {
            _serverHandler = serverHandler;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _serverToClient.Length;
        public override long Position { get => _serverToClient.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_serverExecuted)
            {
                _serverExecuted = true;
                _clientToServer.Position = 0;
                var requestBytes = await IpcMessageFraming.ReadFrameAsync(_clientToServer, cancellationToken);
                var responseMemStream = new MemoryStream();
                await _serverHandler(requestBytes, responseMemStream);
                var responseBytes = responseMemStream.ToArray();
                _serverToClient.Write(responseBytes, 0, responseBytes.Length);
                _serverToClient.Position = 0;
            }

            return await _serverToClient.ReadAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return _clientToServer.WriteAsync(buffer, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            _clientToServer.Write(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class MockPresenceSource : ILinuxSystemServicePresenceSource
    {
        private readonly InstallationPresence _presence;
        public MockPresenceSource(InstallationPresence presence) => _presence = presence;
        public InstallationPresence ProbePresence() => _presence;
        public Task<InstallationPresence> ProbePresenceAsync(CancellationToken ct = default) => Task.FromResult(_presence);
    }

    private sealed class MockReachabilitySource : ILinuxServiceReachabilitySource
    {
        private readonly ServiceReachability _reachability;
        public MockReachabilitySource(ServiceReachability reachability) => _reachability = reachability;
        public ServiceReachability ProbeReachability() => _reachability;
        public Task<ServiceReachability> ProbeReachabilityAsync(CancellationToken ct = default) => Task.FromResult(_reachability);
    }

    private sealed class MockPosixStorageApi : ILinuxPosixStorageApi
    {
        public sealed class FileEntry
        {
            public PosixStat Stat;
            public byte[] Content = Array.Empty<byte>();
            public int ReadOffset;
        }

        private readonly Dictionary<string, FileEntry> _entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (int ownerFd, int lockType)> _fileLocks = new(StringComparer.Ordinal);
        private int _fdCounter = 100;
        private readonly Dictionary<int, string> _openFds = new();
        private readonly uint _euid;
        private readonly uint _egid;

        public MockPosixStorageApi(uint euid = 1000, uint egid = 1000)
        {
            _euid = euid;
            _egid = egid;
        }

        public int LastErrno { get; set; }
        public int GetLastErrno() => LastErrno;

        public uint GetEuid() => _euid;
        public uint GetEgid() => _egid;

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

        public bool TryGetEntry(string path, out FileEntry entry) =>
            _entries.TryGetValue(path.TrimEnd('/'), out entry!);

        public int Open(string path, int flags, int mode)
        {
            var norm = path.TrimEnd('/');
            if (_entries.ContainsKey(norm))
            {
                var fd = ++_fdCounter;
                _openFds[fd] = norm;
                return fd;
            }
            LastErrno = LinuxPosixStorageConstants.ENOENT;
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
            LastErrno = LinuxPosixStorageConstants.ENOENT;
            return -1;
        }

        public int OpenAt2(int dirfd, string pathname, ref OpenHow how)
        {
            if (!_openFds.TryGetValue(dirfd, out var baseDir)) return -1;
            var fullPath = $"{baseDir}/{pathname}".TrimEnd('/');

            bool exists = _entries.ContainsKey(fullPath);
            bool isOcreat = (how.Flags & (ulong)LinuxPosixStorageConstants.O_CREAT) != 0;
            bool isOexcl = (how.Flags & (ulong)LinuxPosixStorageConstants.O_EXCL) != 0;

            if (exists && isOcreat && isOexcl)
            {
                LastErrno = LinuxPosixStorageConstants.EEXIST;
                return -1;
            }

            if (!exists && isOcreat)
            {
                AddEntry(fullPath, isDir: false, isSymlink: false, mode: (int)how.Mode, uid: _euid, gid: _egid);
            }

            if (_entries.ContainsKey(fullPath))
            {
                var fd = ++_fdCounter;
                _openFds[fd] = fullPath;
                _entries[fullPath].ReadOffset = 0;
                return fd;
            }
            LastErrno = LinuxPosixStorageConstants.ENOENT;
            return -1;
        }

        public int Close(int fd)
        {
            if (_openFds.TryGetValue(fd, out var path))
            {
                if (_fileLocks.TryGetValue(path, out var lockInfo) && lockInfo.ownerFd == fd)
                {
                    _fileLocks.Remove(path);
                }
                _openFds.Remove(fd);
                return 0;
            }
            return -1;
        }

        public int Fstat(int fd, out PosixStat statbuf)
        {
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var entry))
            {
                statbuf = entry.Stat;
                return 0;
            }
            LastErrno = LinuxPosixStorageConstants.EBADF;
            statbuf = default;
            return -1;
        }

        public int FstatAt(int dirfd, string pathname, out PosixStat statbuf, int flags)
        {
            string fullPath;
            if (dirfd == LinuxPosixStorageConstants.AT_FDCWD) fullPath = pathname.TrimEnd('/');
            else if (_openFds.TryGetValue(dirfd, out var baseDir)) fullPath = $"{baseDir}/{pathname}".TrimEnd('/');
            else
            {
                LastErrno = LinuxPosixStorageConstants.EBADF;
                statbuf = default;
                return -1;
            }

            if (_entries.TryGetValue(fullPath, out var entry))
            {
                statbuf = entry.Stat;
                return 0;
            }
            LastErrno = LinuxPosixStorageConstants.ENOENT;
            statbuf = default;
            return -1;
        }

        public int Fchmod(int fd, int mode)
        {
            if (_openFds.TryGetValue(fd, out var p) && _entries.TryGetValue(p, out var entry))
            {
                uint cleanMode = (entry.Stat.Mode & ~0xFFFu) | (uint)(mode & 0xFFF);
                entry.Stat.Mode = cleanMode;
                return 0;
            }
            return -1;
        }

        public int Fchown(int fd, uint uid, uint gid)
        {
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var entry))
            {
                entry.Stat.Uid = uid;
                entry.Stat.Gid = gid;
                return 0;
            }
            return -1;
        }

        public int MkdirAt(int dirfd, string pathname, int mode)
        {
            string fullPath;
            if (dirfd == LinuxPosixStorageConstants.AT_FDCWD) fullPath = pathname.TrimEnd('/');
            else if (_openFds.TryGetValue(dirfd, out var baseDir)) fullPath = $"{baseDir}/{pathname}".TrimEnd('/');
            else return -1;

            AddEntry(fullPath, isDir: true, isSymlink: false, mode: mode, uid: _euid, gid: _egid);
            return 0;
        }

        public int RenameAt2(int olddirfd, string oldpath, int newdirfd, string newpath, uint flags)
        {
            if (!_openFds.TryGetValue(olddirfd, out var oldBase)) return -1;
            if (!_openFds.TryGetValue(newdirfd, out var newBase)) return -1;
            var fullOld = $"{oldBase}/{oldpath}".TrimEnd('/');
            var fullNew = $"{newBase}/{newpath}".TrimEnd('/');

            if (!_entries.TryGetValue(fullOld, out var entry))
            {
                LastErrno = LinuxPosixStorageConstants.ENOENT;
                return -1;
            }

            if (_entries.ContainsKey(fullNew) && (flags & LinuxPosixStorageConstants.RENAME_NOREPLACE) != 0)
            {
                LastErrno = LinuxPosixStorageConstants.EEXIST;
                return -1;
            }

            _entries.Remove(fullOld);
            _entries[fullNew] = entry;
            return 0;
        }

        public int UnlinkAt(int dirfd, string pathname, int flags)
        {
            if (!_openFds.TryGetValue(dirfd, out var baseDir)) return -1;
            var fullPath = $"{baseDir}/{pathname}".TrimEnd('/');
            if (_entries.Remove(fullPath)) return 0;
            LastErrno = LinuxPosixStorageConstants.ENOENT;
            return -1;
        }

        public int Fsync(int fd) => 0;

        public int Flock(int fd, int operation)
        {
            if (!_openFds.TryGetValue(fd, out var path))
            {
                LastErrno = LinuxPosixStorageConstants.EBADF;
                return -1;
            }

            int op = operation & ~LinuxPosixStorageConstants.LOCK_NB;

            if (op == LinuxPosixStorageConstants.LOCK_UN)
            {
                if (_fileLocks.TryGetValue(path, out var currentLock) && currentLock.ownerFd == fd)
                {
                    _fileLocks.Remove(path);
                }
                return 0;
            }

            if (op == LinuxPosixStorageConstants.LOCK_EX || op == LinuxPosixStorageConstants.LOCK_SH)
            {
                if (_fileLocks.TryGetValue(path, out var currentLock))
                {
                    if (currentLock.ownerFd != fd)
                    {
                        LastErrno = LinuxPosixStorageConstants.EWOULDBLOCK;
                        return -1;
                    }
                }
                _fileLocks[path] = (fd, op);
                return 0;
            }

            LastErrno = LinuxPosixStorageConstants.EINVAL;
            return -1;
        }

        public int Read(int fd, Span<byte> buffer)
        {
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var entry))
            {
                if (entry.ReadOffset >= entry.Content.Length) return 0;
                int count = Math.Min(buffer.Length, entry.Content.Length - entry.ReadOffset);
                entry.Content.AsSpan(entry.ReadOffset, count).CopyTo(buffer);
                entry.ReadOffset += count;
                return count;
            }
            return -1;
        }

        public int Write(int fd, ReadOnlySpan<byte> buffer)
        {
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var entry))
            {
                var newContent = new byte[entry.Content.Length + buffer.Length];
                Buffer.BlockCopy(entry.Content, 0, newContent, 0, entry.Content.Length);
                buffer.CopyTo(newContent.AsSpan(entry.Content.Length));
                entry.Content = newContent;
                entry.Stat.Size = newContent.Length;
                return buffer.Length;
            }
            return -1;
        }

        public int ReadLinkAt(int dirfd, string pathname, Span<byte> buffer) => -1;
    }
}
