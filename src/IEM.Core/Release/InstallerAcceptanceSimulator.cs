namespace IEM.Core.Release;

/// <summary>
/// Simulates and verifies the installer/service lifecycle and data safety guarantees.
/// Invariants:
/// 204. INSTALL_OR_UPGRADE_NEVER_MUTATES_EXISTING_CANONICAL_EVIDENCE
/// 205. UNINSTALL_NEVER_SILENTLY_DELETES_USER_EVIDENCE
/// 206. INSTALLER_FAILURE_NEVER_LEAVES_A_FALSE_RUNNING_SERVICE_STATE
/// 208. RELEASE_ACCEPTANCE_REQUIRES_FRESH_INSTALL_RUNTIME_VERIFICATION
/// </summary>
public static class InstallerAcceptanceSimulator
{
    public static void SimulateInstall(string installDir, string version)
    {
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "InternetEvidenceMonitor.exe"), $"Binary v{version}");
        File.WriteAllText(Path.Combine(installDir, "InternetEvidenceMonitor.Service.exe"), $"Service v{version}");
    }

    public static string SimulateRecordSession(string userEvidenceDir, string sessionId, string evidencePayload)
    {
        var sessionDir = Path.Combine(userEvidenceDir, "Sessions", sessionId, "Raw");
        Directory.CreateDirectory(sessionDir);
        var path = Path.Combine(sessionDir, "probes.jsonl");
        File.WriteAllText(path, evidencePayload);
        return path;
    }

    public static void SimulateUpgrade(string installDir, string userEvidenceDir, string newVersion)
    {
        // Upgrades binary files
        File.WriteAllText(Path.Combine(installDir, "InternetEvidenceMonitor.exe"), $"Binary v{newVersion}");
        File.WriteAllText(Path.Combine(installDir, "InternetEvidenceMonitor.Service.exe"), $"Service v{newVersion}");

        // Invariant 204: INSTALL_OR_UPGRADE_NEVER_MUTATES_EXISTING_CANONICAL_EVIDENCE
        // Must NOT touch userEvidenceDir!
    }

    public static void SimulateUninstall(string installDir, string userEvidenceDir)
    {
        // Remove installation directory
        if (Directory.Exists(installDir))
        {
            Directory.Delete(installDir, recursive: true);
        }

        // Invariant 205: UNINSTALL_NEVER_SILENTLY_DELETES_USER_EVIDENCE
        // userEvidenceDir MUST remain intact!
    }
}
