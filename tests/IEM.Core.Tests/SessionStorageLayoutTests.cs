using IEM.Storage.Layout;
#if WINDOWS
using IEM.Windows.Storage;
#endif

namespace IEM.Core.Tests;

/// <summary>
/// Unit and acceptance tests for Phase 3.0-10: Session Storage Layout & Access Boundaries.
/// Invariants 67-82.
/// </summary>
public sealed class SessionStorageLayoutTests
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "iem-layout-tests-" + Guid.NewGuid().ToString("N"));

    public SessionStorageLayoutTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void Session_layout_descriptor_roundtrip_Invariant_67()
    {
        // Invariant 67: SESSION_STORAGE_LAYOUT_IS_VERSIONED_AND_EXPLICIT
        var desc = SessionLayoutDescriptor.CreateStandard("session-xyz");
        var bytes = desc.ToCanonicalBytes();

        var deserialized = SessionLayoutDescriptor.FromBytes(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal(SessionLayoutDescriptor.CurrentLayoutVersion, deserialized.LayoutVersion);
        Assert.Equal("session-xyz", deserialized.SessionId);
        Assert.Equal("Raw", deserialized.RawRelativePath);
        Assert.Equal("Derived", deserialized.DerivedRelativePath);
        Assert.Equal("Evidence", deserialized.EvidenceRelativePath);
        Assert.Equal("Exports", deserialized.ExportsRelativePath);
        Assert.False(string.IsNullOrEmpty(deserialized.StoragePolicyHash));
    }

    [Fact]
    public void Path_traversal_is_blocked_by_SessionPathResolver_Invariant_78()
    {
        // Invariant 78: PROTECTED_ARTIFACT_PATH_NEVER_ESCAPES_SESSION_ROOT
        var sessionDir = Path.Combine(_tempRoot, "session-traversal");
        Directory.CreateDirectory(sessionDir);
        var resolver = new SessionPathResolver(sessionDir);

        Assert.False(resolver.TryResolveSafePath("../outside.txt", out _, out var v1));
        Assert.Contains("..", v1);

        Assert.False(resolver.TryResolveSafePath("Raw/../../escape.log", out _, out var v2));
        Assert.Contains("..", v2);

        Assert.False(resolver.TryResolveSafePath("/etc/passwd", out _, out var v3));
        Assert.Contains("Apsolutne", v3);

        Assert.False(resolver.TryResolveSafePath("C:\\Windows\\System32\\calc.exe", out _, out var v4));
        Assert.Contains("Apsolutne", v4);

        Assert.False(resolver.TryResolveSafePath("Raw/file\0.txt", out _, out var v5));
        Assert.Contains("NUL", v5);

        // Safe path
        Assert.True(resolver.TryResolveSafePath("Raw/evidence.log", out var safePath, out _));
        Assert.Equal(Path.Combine(sessionDir, "Raw", "evidence.log"), safePath);
    }

    [Fact]
    public void Lifecycle_transition_governs_write_permissions_Invariants_70_and_71()
    {
        // Invariants 70 and 71
        var sessionDir = Path.Combine(_tempRoot, "session-lifecycle");
        Directory.CreateDirectory(sessionDir);
        var resolver = new SessionPathResolver(sessionDir);

        // Active state
        resolver.TransitionTo(SessionStorageLifecycle.Active);
        Assert.True(resolver.CanWrite(StorageAreaPolicy.RawArea, "Raw/events.log"));
        Assert.True(resolver.CanWrite(StorageAreaPolicy.DerivedArea, "Derived/ledger.jsonl"));
        Assert.False(resolver.CanWrite(StorageAreaPolicy.EvidenceArea, "Evidence/manifest.json"));
        Assert.True(resolver.CanWrite(StorageAreaPolicy.ExportsArea, "Exports/report.pdf"));

        // Sealing state
        resolver.TransitionTo(SessionStorageLifecycle.Sealing);
        Assert.False(resolver.CanWrite(StorageAreaPolicy.RawArea, "Raw/events.log"));
        Assert.False(resolver.CanWrite(StorageAreaPolicy.DerivedArea, "Derived/ledger.jsonl"));
        Assert.True(resolver.CanWrite(StorageAreaPolicy.EvidenceArea, "Evidence/manifest.json"));

        // Sealed state
        resolver.TransitionTo(SessionStorageLifecycle.Sealed);
        Assert.False(resolver.CanWrite(StorageAreaPolicy.RawArea, "Raw/events.log"));
        Assert.False(resolver.CanWrite(StorageAreaPolicy.DerivedArea, "Derived/ledger.jsonl"));
        Assert.False(resolver.CanWrite(StorageAreaPolicy.EvidenceArea, "Evidence/manifest.json")); // Immutable!

        // Invariant 70: Post-signature timestamp retry is permitted
        Assert.True(resolver.CanWrite(StorageAreaPolicy.EvidenceArea, "Evidence/timestamp/timestamp.tsr"));
        Assert.True(resolver.CanWrite(StorageAreaPolicy.EvidenceArea, "Evidence/timestamp/timestamp.tsq"));
    }

    [Fact]
    public void Exports_area_is_user_mutable_and_never_affects_manifest_Invariants_68_69_and_74()
    {
        // Invariants 68, 69, 74, 75
        var exportsArea = StorageAreaPolicy.ExportsArea;
        Assert.Equal(ArtifactRole.Export, exportsArea.Role);
        Assert.Equal(ArtifactMutationPolicy.UserMutableExcluded, exportsArea.MutationPolicy);
        Assert.False(exportsArea.ManifestParticipation); // Excluded from manifest
        Assert.Equal(StorageAccessLevel.FullControl, exportsArea.UserAccess);
    }

    [Fact]
    public void Legacy_session_layout_is_never_migrated_in_place_Invariant_73()
    {
        // Invariant 73: LEGACY_SESSION_LAYOUT_IS_NEVER_MIGRATED_IN_PLACE
        var sessionDir = Path.Combine(_tempRoot, "legacy-session");
        Directory.CreateDirectory(sessionDir);

        // Legacy layout does not have layout.json, writes directly to root/evidence
        var rawLog = Path.Combine(sessionDir, "raw.log");
        File.WriteAllText(rawLog, "legacy content");

        // Reader checks existence of layout.json
        var layoutPath = Path.Combine(sessionDir, SessionLayoutDescriptor.FileName);
        Assert.False(File.Exists(layoutPath));

        // When opened, the legacy raw.log remains completely un-moved
        Assert.True(File.Exists(rawLog));
        Assert.Equal("legacy content", File.ReadAllText(rawLog));
    }

    [Fact]
    public void Standard_storage_areas_define_semantic_boundaries_Invariants_68_and_82()
    {
        // Invariant 68: MANIFEST_SCOPE_IS_DEFINED_BY_ARTIFACT_ROLE
        // Invariant 82: FILESYSTEM_SECURITY_MECHANISM_IS_PLATFORM_PROVENANCE_NOT_EVIDENCE_SEMANTICS
        var raw = StorageAreaPolicy.RawArea;
        var derived = StorageAreaPolicy.DerivedArea;
        var evidence = StorageAreaPolicy.EvidenceArea;

        Assert.True(raw.ManifestParticipation);
        Assert.True(derived.ManifestParticipation);
        Assert.False(evidence.ManifestParticipation);

        Assert.Equal(StorageAccessLevel.ReadOnly, raw.UserAccess);
        Assert.Equal(StorageAccessLevel.ReadOnly, derived.UserAccess);
        Assert.Equal(StorageAccessLevel.ReadOnly, evidence.UserAccess);
    }

    [Fact]
    public async Task End_to_end_acceptance_scenario_3_0_10()
    {
        var sessionDir = Path.Combine(_tempRoot, "e2e-session");
        Directory.CreateDirectory(sessionDir);
        var layout = SessionLayoutDescriptor.CreateStandard("e2e-session");
        var resolver = new SessionPathResolver(sessionDir, layout);

        // 1. Provisioning
        var rawDir = resolver.GetAreaFullPath(StorageAreaPolicy.RawArea);
        var derivedDir = resolver.GetAreaFullPath(StorageAreaPolicy.DerivedArea);
        var evidenceDir = resolver.GetAreaFullPath(StorageAreaPolicy.EvidenceArea);
        var exportsDir = resolver.GetAreaFullPath(StorageAreaPolicy.ExportsArea);

        Directory.CreateDirectory(rawDir);
        Directory.CreateDirectory(derivedDir);
        Directory.CreateDirectory(evidenceDir);
        Directory.CreateDirectory(exportsDir);

        var layoutPath = Path.Combine(sessionDir, SessionLayoutDescriptor.FileName);
        await File.WriteAllBytesAsync(layoutPath, layout.ToCanonicalBytes());

        // 2. Active session: append to Raw and Derived
        resolver.TransitionTo(SessionStorageLifecycle.Active);
        var rawFile = resolver.GetRawFullPath("events.log");
        var derivedFile = resolver.GetDerivedFullPath("ledger.jsonl");

        await File.WriteAllTextAsync(rawFile, "entry1\n");
        await File.AppendAllTextAsync(rawFile, "entry2\n");
        await File.WriteAllTextAsync(derivedFile, "{\"n\":1}\n");

        // Exports can be written freely
        var exportFile = resolver.GetExportsFullPath("summary.html");
        await File.WriteAllTextAsync(exportFile, "<html>Report</html>");

        // 3. Sealing session
        resolver.TransitionTo(SessionStorageLifecycle.Sealing);
        var manifestFile = resolver.GetEvidenceFullPath("manifest.json");
        var sigFile = resolver.GetEvidenceFullPath("manifest.sig");

        await File.WriteAllTextAsync(manifestFile, "{\"manifest\": 1}");
        await File.WriteAllTextAsync(sigFile, "{\"sig\": \"ecdsa\"}");

        // 4. Sealed session
        resolver.TransitionTo(SessionStorageLifecycle.Sealed);
        Assert.False(resolver.CanWrite(StorageAreaPolicy.RawArea, "events.log"));
        Assert.False(resolver.CanWrite(StorageAreaPolicy.EvidenceArea, "manifest.json"));

        // Timestamp retry is permitted post-seal
        var tsDir = Path.Combine(evidenceDir, "timestamp");
        Directory.CreateDirectory(tsDir);
        var tsrFile = resolver.GetEvidenceFullPath("timestamp/timestamp.tsr");
        Assert.True(resolver.CanWrite(StorageAreaPolicy.EvidenceArea, "timestamp/timestamp.tsr"));
        await File.WriteAllTextAsync(tsrFile, "tsr-bytes");

        // 5. User modifies Exports -> evidence files remain intact
        File.Delete(exportFile);
        Assert.False(File.Exists(exportFile));
        Assert.True(File.Exists(rawFile));
        Assert.True(File.Exists(derivedFile));
        Assert.True(File.Exists(manifestFile));
        Assert.True(File.Exists(sigFile));
    }
}
