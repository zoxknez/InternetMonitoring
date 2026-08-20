namespace IEM.Storage.Layout;

/// <summary>
/// Result of a path symlink and boundary safety evaluation.
/// Invariant 77 / Roadmap §6:
/// Privileged evidence operations never follow untrusted reparse points or symlinks.
/// </summary>
public sealed record SymlinkSafetyResult(
    bool IsSafe,
    StorageProtectionState State,
    string? ViolationMessage = null);

/// <summary>
/// Platform-neutral contract for validating that a path traversal contains no unsafe symlinks,
/// reparse points, or boundary escapes from a trusted root.
/// </summary>
public interface ISymlinkSafetyGuard
{
    /// <summary>
    /// Validates that all path segments between <paramref name="trustedRoot"/> and <paramref name="targetPath"/>
    /// are safe from symlinks, reparse points, and directory traversal escapes.
    /// </summary>
    SymlinkSafetyResult ValidatePath(string trustedRoot, string targetPath);
}
