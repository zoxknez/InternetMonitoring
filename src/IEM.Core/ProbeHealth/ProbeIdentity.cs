using IEM.Core.Probes;

namespace IEM.Core.ProbeHealth;

/// <summary>
/// Scoped identity of a probe engine/implementation and its context.
/// Invariant 64: PROBE_HEALTH_IS_SCOPED_TO_PROBE_IMPLEMENTATION_AND_RELEVANT_CONTEXT.
/// </summary>
public sealed record ProbeIdentity(
    string ProbeId,
    TargetProbeType ProbeType,
    string ImplementationId,
    string ImplementationVersion,
    TargetAddressFamily AddressFamily)
{
    public string UniqueKey => $"{ProbeType}:{ImplementationId}:{AddressFamily}:{ProbeId}";
}
