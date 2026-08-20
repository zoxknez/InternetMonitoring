namespace IEM.Linux.Crypto;

/// <summary>
/// Distinguishes the security and storage isolation scope for Linux signing identities.
/// </summary>
public enum LinuxSigningIdentityScope
{
    /// <summary>
    /// System-wide daemon identity stored in /var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8,
    /// isolated to the iem service user (exact UID/GID, 0700/0600).
    /// </summary>
    SystemInstallation,

    /// <summary>
    /// User-owned portable identity stored in $XDG_STATE_HOME/internet-evidence-monitor/keys/evidence-signing-v1.p8,
    /// isolated to the executing user (exact UID/GID, 0700/0600).
    /// </summary>
    PortableUser
}
