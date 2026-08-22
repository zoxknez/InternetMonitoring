namespace IEM.Core.Model;

/// <summary>
/// Mode of network interface selection.
/// </summary>
public enum InterfaceSelectionMode
{
    /// <summary>Auto-discovery: selects best physical default-route carrier once before session start.</summary>
    Auto,

    /// <summary>Explicit user or configuration request.</summary>
    Explicit,

    /// <summary>Resumption of an existing session using its canonical persisted interface ID.</summary>
    ResumeCanonical,

    /// <summary>Resumption of a legacy session (Schema &lt;= 3) where only friendly name was recorded.</summary>
    LegacyResume,
}

/// <summary>
/// Platform-neutral request for monitored network interface resolution.
/// </summary>
public sealed record InterfaceSelectionRequest(
    string? InterfaceId,
    string? InterfaceName,
    InterfaceSelectionMode Mode)
{
    public static InterfaceSelectionRequest ForAuto() =>
        new(null, null, InterfaceSelectionMode.Auto);

    public static InterfaceSelectionRequest ForExplicit(string? interfaceIdOrName) =>
        new(interfaceIdOrName, interfaceIdOrName, InterfaceSelectionMode.Explicit);

    public static InterfaceSelectionRequest ForResume(string? interfaceId, string? interfaceName, int schemaVersion) =>
        schemaVersion >= 4 && !string.IsNullOrWhiteSpace(interfaceId)
            ? new(interfaceId, interfaceName, InterfaceSelectionMode.ResumeCanonical)
            : new(null, interfaceName, InterfaceSelectionMode.LegacyResume);
}

/// <summary>
/// Authoritative pinned identity of the monitored network interface for a session.
/// Invariants:
/// WIN_SESSION_INTERFACE_IMMUTABLE: Monitored interface ID must not change during session.
/// WIN_RESUME_RESTORES_CANONICAL_INTERFACE_IDENTITY: Resumption restores canonical identity.
/// </summary>
public sealed record MonitoredInterfaceIdentity(
    string InterfaceId,
    string InterfaceName);
