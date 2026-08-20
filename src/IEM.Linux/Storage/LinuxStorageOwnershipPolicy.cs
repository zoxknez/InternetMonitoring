namespace IEM.Linux.Storage;

/// <summary>
/// Policy defining expected user and group ownership for canonical Linux evidence storage.
/// Invariant 80 / Roadmap §6:
/// System installation requires iem:iem ownership (or specified daemon UID/GID).
/// Portable execution expects the current user's UID/GID.
/// </summary>
public sealed record LinuxStorageOwnershipPolicy(
    uint? ExpectedUid,
    uint? ExpectedGid,
    bool EnforceExactOwnership,
    string PolicyName)
{
    public static readonly LinuxStorageOwnershipPolicy SystemDefault =
        new(ExpectedUid: null, ExpectedGid: null, EnforceExactOwnership: false, PolicyName: "SystemInstallationDefault");

    public static LinuxStorageOwnershipPolicy CreateSystem(uint uid, uint gid) =>
        new(ExpectedUid: uid, ExpectedGid: gid, EnforceExactOwnership: true, PolicyName: "SystemInstallationExact");

    public static LinuxStorageOwnershipPolicy CreatePortable(uint uid, uint gid) =>
        new(ExpectedUid: uid, ExpectedGid: gid, EnforceExactOwnership: true, PolicyName: "PortableUser");
}
