using IEM.Core.Probes;
using IEM.Evidence.Crypto;
using IEM.Linux.Composition;
using IEM.Linux.Storage;
using IEM.Storage.Layout;
using Microsoft.Extensions.DependencyInjection;

namespace IEM.Service.Linux.Composition;

/// <summary>
/// Service collection extension methods adapting Linux production composition to Microsoft DI.
/// Invariants 8E-D, 8E-K:
/// - Provides centralized AddLinuxSystemServices registration for Linux system daemons.
/// - Delegates all platform/security construction strictly to LinuxProductionCompositionFactory.
/// - Ensures single shared POSIX and security graph across all registered interfaces.
/// </summary>
public static class LinuxServiceCollectionExtensions
{
    /// <summary>
    /// Registers all platform adapters, storage layouts, security provisioners, and cryptographic key providers
    /// for a Linux system service using an authoritative SystemInstallation composition graph.
    /// </summary>
    public static IServiceCollection AddLinuxSystemServices(
        this IServiceCollection services,
        string? stateRoot = null,
        uint? expectedUid = null,
        uint? expectedGid = null,
        ILinuxPosixStorageApi? posix = null,
        IPlatformProbeFactory? probeFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var composition = LinuxProductionCompositionFactory.CreateSystem(
            stateRoot,
            expectedUid,
            expectedGid,
            posix,
            probeFactory);

        services.AddSingleton(composition);
        services.AddSingleton(composition.PosixApi);
        services.AddSingleton(composition.OwnershipPolicy);
        services.AddSingleton<IPlatformStorageLayout>(composition.StorageLayout);
        services.AddSingleton<ISymlinkSafetyGuard>(composition.SymlinkGuard);
        services.AddSingleton<IStorageProtectionProvider>(composition.StorageProtectionProvider);
        services.AddSingleton<IEvidenceKeyProvider>(composition.EvidenceKeyProvider);
        services.AddSingleton<IPlatformProbeFactory>(composition.ProbeFactory);

        return services;
    }
}
