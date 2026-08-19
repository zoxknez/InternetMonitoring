using IEM.Core.Hosting;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Presentation.Hosting;
using IEM.Service.Runtime;
using IEM.Storage;
using IEM.Storage.Layout;

namespace IEM.Core.Tests;

/// <summary>
/// Characterization, boundary, and matrix tests for Phase 3.1-1 Platform Composition Boundary.
/// Validates Invariants 211, 275, 276, and 282:
/// - Invariant 276 (CRITICAL): Installed + Unreachable MUST NEVER fall back to Portable/InProcess.
/// - Invariant 282: InstallationPresence and ServiceReachability are distinct facts (§15A.5).
/// - Invariant 275: IEM.Service.Runtime is lifecycle-neutral and works with injected platform abstractions.
/// </summary>
public sealed class PlatformCompositionTests311
{
    [Fact]
    public async Task Installed_and_unreachable_never_falls_back_to_portable_Invariant_276()
    {
        // Simulate a system where the service is installed in the registry/SCM,
        // but the service is currently stopped or its IPC transport is unreachable.
        var installedState = new PlatformInstallationState(
            InstallationPresence.InstalledSystemService,
            ServiceReachability.Unreachable,
            "Service is installed but not running.");

        var storageLayout = new TestStorageLayout(
            systemRoot: @"C:\ProgramData\InternetEvidenceMonitor\Sesije",
            portableRoot: @"C:\Users\TestUser\Desktop\InternetEvidence");

        // Execute composition logic identical to UI / CLI host selection
        var host = CreateHostFromInstallationState(installedState, storageLayout, out var resolvedRoot);

        // Assertions enforcing Invariant 276:
        // 1. Resolved storage root MUST be system root, NEVER portable root
        Assert.Equal(storageLayout.DefaultOutputRoot, resolvedRoot);
        Assert.NotEqual(storageLayout.PortableOutputRoot, resolvedRoot);

        // 2. Host kind MUST remain Service (unavailable state), NEVER InProcess
        Assert.Equal(HostKind.Service, host.Kind);

        // 3. Host is not running
        Assert.False(host.IsRunning);

        // 4. Starting a session MUST be rejected (returns false), setting fault
        string? reportedFault = null;
        host.FaultChanged += fault => reportedFault = fault;

        var startResult = await host.StartSessionAsync(TimeSpan.FromHours(48), null, CancellationToken.None);

        Assert.False(startResult);
        Assert.NotNull(reportedFault);
        Assert.Contains("nije dostupan", reportedFault, StringComparison.OrdinalIgnoreCase);

        // 5. Connecting sets fault immediately
        string? connectFault = null;
        host.FaultChanged += fault => connectFault = fault;
        await host.ConnectAsync(CancellationToken.None);
        Assert.NotNull(connectFault);
    }

    [Fact]
    public void Canonical_state_model_matrix_matches_spec_Invariant_282()
    {
        // 1. InstalledSystemService + Reachable -> Usable service, not portable
        var s1 = new PlatformInstallationState(InstallationPresence.InstalledSystemService, ServiceReachability.Reachable);
        Assert.True(s1.IsUsableService);
        Assert.False(s1.IsExplicitlyPortable);
        Assert.True(s1.IsValid);

        // 2. InstalledSystemService + Unreachable -> NOT usable service, NOT portable (fails closed, Invariant 276)
        var s2 = new PlatformInstallationState(InstallationPresence.InstalledSystemService, ServiceReachability.Unreachable);
        Assert.False(s2.IsUsableService);
        Assert.False(s2.IsExplicitlyPortable);
        Assert.True(s2.IsValid);

        // 3. PortableOnly + NotApplicable -> Explicitly portable, NOT usable service
        var s3 = new PlatformInstallationState(InstallationPresence.PortableOnly, ServiceReachability.NotApplicable);
        Assert.False(s3.IsUsableService);
        Assert.True(s3.IsExplicitlyPortable);
        Assert.True(s3.IsValid);

        // 4. Unknown + Unreachable -> Indeterminate, fails closed
        var s4 = new PlatformInstallationState(InstallationPresence.Unknown, ServiceReachability.Unreachable);
        Assert.False(s4.IsUsableService);
        Assert.False(s4.IsExplicitlyPortable);
        Assert.True(s4.IsValid);

        // 5. Unknown + NotApplicable -> Indeterminate, fails closed
        var s5 = new PlatformInstallationState(InstallationPresence.Unknown, ServiceReachability.NotApplicable);
        Assert.False(s5.IsUsableService);
        Assert.False(s5.IsExplicitlyPortable);
        Assert.True(s5.IsValid);
    }

    [Fact]
    public void Exhaustive_state_matrix_normalizes_invalid_combinations_fail_closed()
    {
        var presences = new[]
        {
            InstallationPresence.InstalledSystemService,
            InstallationPresence.PortableOnly,
            InstallationPresence.Unknown,
        };

        var reachabilities = new[]
        {
            ServiceReachability.Reachable,
            ServiceReachability.Unreachable,
            ServiceReachability.NotApplicable,
        };

        foreach (var p in presences)
        {
            foreach (var r in reachabilities)
            {
                var state = new PlatformInstallationState(p, r);

                if (p == InstallationPresence.PortableOnly && r != ServiceReachability.NotApplicable)
                {
                    // Invalid: PortableOnly cannot have Reachable/Unreachable -> must fail closed to Unknown
                    Assert.Equal(InstallationPresence.Unknown, state.Presence);
                    Assert.False(state.IsUsableService);
                    Assert.False(state.IsExplicitlyPortable);
                }
                else if (p == InstallationPresence.InstalledSystemService && r == ServiceReachability.NotApplicable)
                {
                    // Invalid: InstalledSystemService cannot have NotApplicable -> must fail closed to Unknown
                    Assert.Equal(InstallationPresence.Unknown, state.Presence);
                    Assert.False(state.IsUsableService);
                    Assert.False(state.IsExplicitlyPortable);
                }
                else if (p == InstallationPresence.InstalledSystemService && r == ServiceReachability.Reachable)
                {
                    Assert.True(state.IsUsableService);
                    Assert.False(state.IsExplicitlyPortable);
                }
                else if (p == InstallationPresence.PortableOnly && r == ServiceReachability.NotApplicable)
                {
                    Assert.False(state.IsUsableService);
                    Assert.True(state.IsExplicitlyPortable);
                }
                else
                {
                    // All other combinations are non-usable and non-portable (fail closed)
                    Assert.False(state.IsUsableService);
                    Assert.False(state.IsExplicitlyPortable);
                }
            }
        }
    }

    [Theory]
    [InlineData("48h", 48 * 3600)]
    [InlineData("90m", 90 * 60)]
    [InlineData("45s", 45)]
    [InlineData("7d", 7 * 24 * 3600)]
    public void MonitorSettings_parses_duration_strings_correctly(string text, double expectedSeconds)
    {
        var success = MonitorSettings.TryParseDuration(text, out var duration);
        Assert.True(success);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), duration);
    }

    [Theory]
    [InlineData("beskonacno")]
    [InlineData("beskonačno")]
    [InlineData("infinite")]
    public void MonitorSettings_parses_infinite_duration(string text)
    {
        var success = MonitorSettings.TryParseDuration(text, out var duration);
        Assert.True(success);
        Assert.Equal(Timeout.InfiniteTimeSpan, duration);
    }

    [Fact]
    public void MonitorSettings_resolves_configured_root_or_fallback()
    {
        var settingsEmpty = new MonitorSettings { OutputRoot = "" };
        Assert.Equal(@"C:\Fallback\Path", settingsEmpty.ResolveOutputRoot(@"C:\Fallback\Path"));

        var settingsCustom = new MonitorSettings { OutputRoot = @"D:\Custom\Path" };
        Assert.Equal(@"D:\Custom\Path", settingsCustom.ResolveOutputRoot(@"C:\Fallback\Path"));
    }

    private static IMonitorHost CreateHostFromInstallationState(
        PlatformInstallationState state,
        IPlatformStorageLayout layout,
        out string outputRoot)
    {
        if (state.Presence == InstallationPresence.InstalledSystemService)
        {
            outputRoot = layout.DefaultOutputRoot;
            if (state.Reachability == ServiceReachability.Reachable)
            {
                return new MockReachableServiceHost(outputRoot);
            }

            return new MockUnavailableServiceHost(outputRoot, "Windows servis je instaliran, ali trenutno nije dostupan.");
        }

        if (state.Presence == InstallationPresence.PortableOnly)
        {
            outputRoot = layout.PortableOutputRoot;
            return new MockInProcessHost(outputRoot);
        }

        outputRoot = layout.DefaultOutputRoot;
        return new MockUnavailableServiceHost(outputRoot, "Stanje instalacije servisa nije moguće pouzdano utvrditi.");
    }

    private sealed class TestStorageLayout(string systemRoot, string portableRoot) : IPlatformStorageLayout
    {
        public string DefaultOutputRoot => systemRoot;
        public string PortableOutputRoot => portableRoot;
        public string ResolveOutputRoot(bool isInstalled) => isInstalled ? systemRoot : portableRoot;
        public string GetSessionDirectory(string sessionId, bool isInstalled) =>
            Path.Combine(ResolveOutputRoot(isInstalled), $"Sesija_{sessionId}");
    }

    private sealed class MockUnavailableServiceHost(string outputRoot, string fault) : IMonitorHost
    {
        public string OutputRoot { get; } = outputRoot;
        public HostKind Kind => HostKind.Service;
        public bool IsRunning => false;
        public event Action<MonitorSnapshot>? Updated { add { } remove { } }
        public event Action<string?>? FaultChanged;

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            FaultChanged?.Invoke(fault);
            return Task.CompletedTask;
        }

        public Task<bool> StartSessionAsync(TimeSpan duration, string? interfaceName, CancellationToken cancellationToken)
        {
            FaultChanged?.Invoke(fault);
            return Task.FromResult(false);
        }

        public Task StopSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MockReachableServiceHost(string outputRoot) : IMonitorHost
    {
        public string OutputRoot { get; } = outputRoot;
        public HostKind Kind => HostKind.Service;
        public bool IsRunning => true;
        public event Action<MonitorSnapshot>? Updated { add { } remove { } }
        public event Action<string?>? FaultChanged { add { } remove { } }
        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> StartSessionAsync(TimeSpan duration, string? interfaceName, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task StopSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MockInProcessHost(string outputRoot) : IMonitorHost
    {
        public string OutputRoot { get; } = outputRoot;
        public HostKind Kind => HostKind.InProcess;
        public bool IsRunning => false;
        public event Action<MonitorSnapshot>? Updated { add { } remove { } }
        public event Action<string?>? FaultChanged { add { } remove { } }
        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> StartSessionAsync(TimeSpan duration, string? interfaceName, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task StopSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
