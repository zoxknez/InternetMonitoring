namespace IEM.Presentation.Semantics;

/// <summary>
/// Platform-neutral visual tone indicating evaluation status.
/// Renderers map these tokens to their respective styling primitives (WPF Brushes, Avalonia resources).
/// Invariant 170: VISUAL_STYLE_NEVER_CHANGES_OR_COLLAPSES_SEMANTIC_STATE.
/// </summary>
public enum SemanticTone
{
    Unknown,
    Neutral,
    Good,
    Info,
    Warning,
    Bad
}
