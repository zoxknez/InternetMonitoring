using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using IEM.Verification.Safety;

namespace IEM.Core.Tests;

/// <summary>
/// Architecture boundary and layering enforcement tests for Phase 3.1-0 and 3.1-1.
/// Enforces structural invariants to prevent platform-coupling leakage into canonical layers:
/// - IEM.Core must not reference IEM.Windows or IEM.Linux
/// - IEM.Evidence must not reference IEM.Windows or IEM.Linux
/// - IEM.Verification must not reference UI or platform hosts
/// - IEM.Legal must not reference platform hosts
/// - IEM.Service.Runtime must not reference IEM.Windows, IEM.Linux, WPF, or Avalonia
/// - IEM.Presentation must not reference IEM.Windows, IEM.Linux, IEM.Service, WPF, or Avalonia
/// - Canonical layers & Service.Runtime & Presentation must not contain P/Invoke, OS execution dependencies, or native Windows API imports
/// - Inventory manifest consistency: all baseline types mapped 1:1 without duplicates or orphans.
/// </summary>
public sealed class ArchitectureBoundaryTests
{
    private static readonly Assembly CoreAssembly = typeof(IEM.Core.MonitorEngine).Assembly;
    private static readonly Assembly EvidenceAssembly = typeof(IEM.Evidence.EvidencePackage).Assembly;
    private static readonly Assembly LegalAssembly = typeof(IEM.Legal.LegalRegistry).Assembly;
    private static readonly Assembly VerificationAssembly = typeof(IEM.Verification.Engine.PackageVerifier).Assembly;
    private static readonly Assembly ServiceRuntimeAssembly = typeof(IEM.Service.Runtime.MonitorWorker).Assembly;
    private static readonly Assembly PresentationAssembly = typeof(IEM.Presentation.Hosting.IMonitorHost).Assembly;

    [Fact]
    public void Canonical_Core_assembly_must_not_reference_platform_adapters()
    {
        var referencedAssemblies = CoreAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.DoesNotContain("IEM.Windows", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.Linux", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.App", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.Service", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canonical_Evidence_assembly_must_not_reference_platform_adapters()
    {
        var referencedAssemblies = EvidenceAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.DoesNotContain("IEM.Windows", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.Linux", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.App", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.Service", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canonical_Verification_assembly_must_not_reference_UI_or_platform_adapters()
    {
        var referencedAssemblies = VerificationAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.DoesNotContain("IEM.Windows", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.Linux", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.App", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.Service", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canonical_Legal_assembly_must_not_reference_platform_adapters()
    {
        var referencedAssemblies = LegalAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.DoesNotContain("IEM.Windows", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.Linux", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.App", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.Service", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Service_Runtime_assembly_must_not_reference_platform_adapters_or_UI()
    {
        var referencedAssemblies = ServiceRuntimeAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.DoesNotContain("IEM.Windows", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.Linux", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.App", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.Service", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("PresentationFramework", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("PresentationCore", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Avalonia", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Presentation_assembly_must_not_reference_platform_adapters_or_Service_host()
    {
        var referencedAssemblies = PresentationAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.DoesNotContain("IEM.Windows", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.Linux", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.App", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEM.Service", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("PresentationFramework", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("PresentationCore", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Avalonia", referencedAssemblies, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canonical_Runtime_and_Presentation_source_files_must_not_contain_platform_leakage()
    {
        var repoRoot = FindRepoRoot();
        var targetDirs = new[]
        {
            Path.Combine(repoRoot, "src", "IEM.Core"),
            Path.Combine(repoRoot, "src", "IEM.Evidence"),
            Path.Combine(repoRoot, "src", "IEM.Legal"),
            Path.Combine(repoRoot, "src", "IEM.Verification"),
            Path.Combine(repoRoot, "src", "IEM.Verifier"),
            Path.Combine(repoRoot, "src", "IEM.Storage"),
            Path.Combine(repoRoot, "src", "IEM.Service.Runtime"),
            Path.Combine(repoRoot, "src", "IEM.Presentation"),
        };

        var forbiddenPatterns = new[]
        {
            @"\[\s*DllImport",
            @"\[\s*LibraryImport",
            @"Microsoft\.Win32",
            @"(?<!Legal)Registry\.(LocalMachine|CurrentUser|ClassesRoot|Users|CurrentConfig)",
            @"RegistryKey",
            @"Environment\.SpecialFolder",
            @"ServiceController",
            @"ServiceBase",
            @"IemWindowsServiceLifetime",
            @"IEM\.Windows",
            @"IEM\.Linux",
            @"iphlpapi\.dll",
            @"wlanapi\.dll",
            @"kernel32\.dll",
            @"user32\.dll",
            @"advapi32\.dll",
        };

        var violations = new List<string>();

        foreach (var dir in targetDirs)
        {
            if (!Directory.Exists(dir)) continue;

            var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                            !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

            foreach (var file in files)
            {
                var isManifestBuilder = Path.GetFileName(file) == "ManifestBuilder.cs";
                var isVerificationSafety = Path.GetDirectoryName(file)?.EndsWith(Path.Combine("IEM.Verification", "Safety")) == true;

                if (isVerificationSafety)
                {
                    // Verification.Safety is the platform-neutral native confinement layer (Invariant 28/29)
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (line.StartsWith("//") || line.StartsWith("/*") || line.StartsWith("*"))
                    {
                        continue;
                    }

                    // Check OS checks outside ManifestBuilder provenance field
                    if (!isManifestBuilder && (line.Contains("OperatingSystem.IsWindows") || line.Contains("OperatingSystem.IsLinux") || line.Contains("OperatingSystem.IsMacOS")))
                    {
                        var relPath = Path.GetRelativePath(repoRoot, file);
                        violations.Add($"{relPath}:{i + 1} -> {line} (Forbidden OS check in shared code)");
                    }

                    foreach (var pat in forbiddenPatterns)
                    {
                        if (Regex.IsMatch(line, pat))
                        {
                            var relPath = Path.GetRelativePath(repoRoot, file);
                            violations.Add($"{relPath}:{i + 1} -> {line}");
                        }
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Inventory_and_repository_manifest_must_be_consistent_and_complete()
    {
        var repoRoot = FindRepoRoot();
        var manifestPath = Path.Combine(repoRoot, "artifacts", "3.1-0", "platform-inventory.json");
        Assert.True(File.Exists(manifestPath), "Inventory manifest missing at " + manifestPath);

        var json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("3.1.0-baseline", root.GetProperty("inventoryVersion").GetString());
        Assert.Equal("LOCKED", root.GetProperty("status").GetString());

        var unmapped = root.GetProperty("unmappedCoupling");
        Assert.Equal(0, unmapped.GetArrayLength());

        var modules = root.GetProperty("modules");
        var allTypes = new List<string>();

        foreach (var module in modules.EnumerateArray())
        {
            var types = module.GetProperty("types");
            foreach (var typeObj in types.EnumerateArray())
            {
                var typeName = typeObj.GetProperty("type").GetString()!;
                var fileRel = typeObj.GetProperty("file").GetString()!;
                var targetContract = typeObj.GetProperty("targetContract").GetString()!;
                var targetPhase = typeObj.GetProperty("targetPhase").GetString()!;

                Assert.False(string.IsNullOrWhiteSpace(typeName));
                Assert.False(string.IsNullOrWhiteSpace(fileRel));
                Assert.False(string.IsNullOrWhiteSpace(targetContract));
                Assert.False(string.IsNullOrWhiteSpace(targetPhase));

                // Verify file exists on disk
                var fullPath = Path.Combine(repoRoot, fileRel.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(fullPath), $"File in inventory does not exist: {fileRel}");

                allTypes.Add(typeName);
            }
        }

        // Verify no duplicate mappings
        var duplicates = allTypes.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        Assert.Empty(duplicates);

        // Verify baseline inventory files exist in src/IEM.Windows
        var windowsModule = modules.EnumerateArray().First(m => m.GetProperty("name").GetString() == "IEM.Windows");
        var inventoryFiles = windowsModule.GetProperty("types").EnumerateArray()
            .Select(t => t.GetProperty("file").GetString()!)
            .ToArray();

        foreach (var invFile in inventoryFiles)
        {
            var fullPath = Path.Combine(repoRoot, invFile.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"Baseline file does not exist on disk: {invFile}");
        }
    }

    private static string FindRepoRoot([CallerFilePath] string callerPath = "")
    {
        var candidates = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            callerPath
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidate.StartsWith("/_")) continue;

            var current = Path.IsPathRooted(candidate) && File.Exists(candidate)
                ? Path.GetDirectoryName(candidate)
                : candidate;

            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "InternetEvidenceMonitor.slnx")) ||
                    Directory.Exists(Path.Combine(current, ".git")) ||
                    (Directory.Exists(Path.Combine(current, "src")) && Directory.Exists(Path.Combine(current, "tests"))))
                {
                    return current;
                }

                current = Directory.GetParent(current)?.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found from candidates: " + string.Join(", ", candidates));
    }

    [Fact]
    public void ConfinedPackageFileReader_Reads_Root_And_Subfile_Successfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "confined_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var subDir = Path.Combine(tempDir, "Sub");
            Directory.CreateDirectory(subDir);
            var rootFile = Path.Combine(tempDir, "root.txt");
            var subFile = Path.Combine(subDir, "sub.txt");
            File.WriteAllText(rootFile, "ROOT_DATA");
            File.WriteAllText(subFile, "SUB_DATA");

            var status1 = IEM.Verification.Safety.ConfinedPackageFileReader.TryOpenRead(tempDir, "root.txt", out var s1, out var err1);
            Assert.True(status1 == IEM.Verification.Safety.ConfinedPackageFileReader.ReadResultStatus.Success, $"root.txt failed: {err1}");
            s1?.Dispose();

            var status2 = IEM.Verification.Safety.ConfinedPackageFileReader.TryOpenRead(tempDir, "Sub/sub.txt", out var s2, out var err2);
            Assert.True(status2 == IEM.Verification.Safety.ConfinedPackageFileReader.ReadResultStatus.Success, $"Sub/sub.txt failed: {err2}");
            s2?.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ConfinedPackageFileReader_SourceCode_Enforces_ObjDontReparse_And_FailClosed_Checks()
    {
        var repoRoot = FindRepoRoot();
        var readerFile = Path.Combine(repoRoot, "src", "IEM.Verification", "Safety", "ConfinedPackageFileReader.cs");
        Assert.True(File.Exists(readerFile), $"ConfinedPackageFileReader.cs not found at {readerFile}");

        var readerSource = File.ReadAllText(readerFile);
        Assert.Contains("Attributes = NativePackageConfinementInterop.OBJ_CASE_INSENSITIVE", readerSource, StringComparison.Ordinal);
        Assert.Contains("FILE_FLAG_OPEN_REPARSE_POINT", readerSource, StringComparison.Ordinal);
        Assert.Contains("FILE_ATTRIBUTE_REPARSE_POINT", readerSource, StringComparison.Ordinal);
        Assert.Contains("!NativePackageConfinementInterop.GetFileInformationByHandle(hRootDir", readerSource, StringComparison.Ordinal);
        Assert.Contains("len >= (uint)sbFinal.Capacity", readerSource, StringComparison.Ordinal);
        Assert.Contains("GetFinalPathNameByHandle", readerSource, StringComparison.Ordinal);
    }
}
