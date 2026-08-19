using IEM.Verification.Engine;
using IEM.Verification.Models;

namespace IEM.Verifier;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return (int)OverallStatus.InputError;
        }

        string? packagePath = null;
        var jsonOutput = false;
        var options = new VerificationOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--json")
            {
                jsonOutput = true;
            }
            else if (arg == "--offline")
            {
                options.Offline = true;
            }
            else if (arg == "--expected-key-id" && i + 1 < args.Length)
            {
                options.ExpectedKeyId = args[++i];
            }
            else if (arg == "--trusted-key" && i + 1 < args.Length)
            {
                options.TrustedKeyPath = args[++i];
            }
            else if (!arg.StartsWith("-", StringComparison.Ordinal) && packagePath is null)
            {
                packagePath = arg;
            }
        }

        if (string.IsNullOrWhiteSpace(packagePath))
        {
            Console.Error.WriteLine("Greška: Putanja do paketa evidencije nije navedena.");
            PrintHelp();
            return (int)OverallStatus.InputError;
        }

        if (!Directory.Exists(packagePath))
        {
            Console.Error.WriteLine($"Greška: Direktorijum '{packagePath}' ne postoji.");
            return (int)OverallStatus.InputError;
        }

        var report = await PackageVerifier.VerifyPackageAsync(packagePath, options);

        if (jsonOutput)
        {
            Console.WriteLine(report.ToJson());
        }
        else
        {
            Console.WriteLine(report.ToConsoleReport());
        }

        return report.ExitCode;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("IEM Verifier — Nezavisni forenzički verifikator paketa dokaza (IEM 3.0)");
        Console.WriteLine();
        Console.WriteLine("Upotreba:");
        Console.WriteLine("  iem-verifier <direktorijum-paketa> [opcije]");
        Console.WriteLine();
        Console.WriteLine("Opcije:");
        Console.WriteLine("  --json                    Ispis izveštaja u strukturiranom JSON formatu");
        Console.WriteLine("  --offline                 Garantuje rad bez ikakvog mrežnog pristupa (0 mrežnih poziva)");
        Console.WriteLine("  --expected-key-id <id>    Očekivani identifikator ključa (npr. sha256:abcd...)");
        Console.WriteLine("  --trusted-key <fajl>      Putanja do SPKI DER datoteke poverenog javnog ključa");
        Console.WriteLine("  --help, -h                Prikazuje ovu pomoćnu poruku");
        Console.WriteLine();
        Console.WriteLine("Izlazni kodovi (Exit codes):");
        Console.WriteLine("  0   VERIFIED (Integritet i koren poverenja dokazani)");
        Console.WriteLine("  10  VALID — TRUST NOT ESTABLISHED (Kriptografski validan, ali koren nije nezavisno potvrđen)");
        Console.WriteLine("  20  INCOMPLETE (Paket nepotpun, npr. Pending vremenski žig)");
        Console.WriteLine("  30  INVALID (Kriptografska ili strukturna neispravnost, izmenjen sadržaj)");
        Console.WriteLine("  40  UNSUPPORTED (Nepodržana ili novija verzija šeme)");
        Console.WriteLine("  50  INPUT_ERROR (Pogrešni parametri ili nepostojeći folder)");
    }
}
