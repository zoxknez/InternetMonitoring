namespace IEM.Presentation.Models;

using IEM.Presentation.Semantics;

/// <summary>
/// Platform-neutral presentation state of a probe family check.
/// Invariant 159: UNKNOWN_UI_VALUE_NEVER_BECOMES_ZERO_SUCCESS_FAILURE_OR_UNSUPPORTED.
/// </summary>
/// <param name="Name">Probe family name (e.g. "Ruter", "Ping", "DNS").</param>
/// <param name="Detail">Success/attempt count (e.g. "3/3") or unattempted indication.</param>
/// <param name="Tone">Platform-neutral tone (e.g. Good, Warning, Bad, Neutral/Info) for renderer brush binding.</param>
public sealed record ProbePresentationState(string Name, string Detail, SemanticTone Tone);
