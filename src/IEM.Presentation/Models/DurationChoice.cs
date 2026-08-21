namespace IEM.Presentation.Models;

/// <summary>
/// Pre-configured duration choice for planned monitoring sessions.
/// </summary>
/// <param name="Label">User-facing duration label (e.g. "24 sata").</param>
/// <param name="Duration">TimeSpan duration, or Timeout.InfiniteTimeSpan for indefinite.</param>
/// <param name="Note">Descriptive purpose note (e.g. "ceo dan i noć").</param>
public sealed record DurationChoice(string Label, TimeSpan Duration, string Note);
