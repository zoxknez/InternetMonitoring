namespace IEM.Presentation.Models;

/// <summary>
/// A single formatted presentation metric with label, value, and optional context hint.
/// </summary>
public sealed record MetricPresentationItem(string Label, string Value, string? Hint = null);
