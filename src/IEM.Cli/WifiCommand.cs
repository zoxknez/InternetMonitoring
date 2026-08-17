using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Windows;

namespace IEM.Cli;

/// <summary>
/// Reports what the wireless layer can see, and says so plainly when it can see nothing.
/// <para>
/// Useful on its own: someone whose Wi-Fi drops needs to know whether this tool can read
/// the signal, the access point and the scan at all, because those are what separate "the
/// router stopped broadcasting" from "you walked out of range" - and without them an
/// outage over Wi-Fi cannot be attributed to anything.
/// </para>
/// <para>
/// It also exercises the whole wireless path on demand, including on machines that have no
/// wireless hardware and no WLAN service running. That is the harshest environment this
/// code meets and the most common one on a desktop, so being able to run it deliberately
/// beats discovering the behaviour when someone's two-day test crashes.
/// </para>
/// </summary>
public static class WifiCommand
{
    public static async Task<bool> RunAsync(string? interfaceName)
    {
        Console.WriteLine();
        Console.WriteLine("  BEŽIČNI SLOJ");
        Console.WriteLine("  ─────────────────────────────────────────────");
        Console.WriteLine();

        await using var inspection = WindowsLinkInspection.Create(interfaceName);
        var link = inspection.Inspector.Inspect();

        Console.WriteLine($"  Nadzirani adapter: {link.InterfaceName}");
        Console.WriteLine($"  Medij:             {Medium(link.Medium)}");
        Console.WriteLine($"  Stanje:            {(link.IsUp ? "aktivan" : "nije aktivan")}");
        Console.WriteLine();

        if (link.Medium != LinkMedium.Wireless)
        {
            Console.WriteLine("  Nadzirani adapter nije bežični, pa se bežični podaci ne čitaju.");
            Console.WriteLine();
        }

        // Read regardless of the monitored medium: the question here is whether the layer
        // works at all on this machine, not whether it happens to be in use right now.
        var radios = WirelessDiagnostics.Read();

        if (radios.Count == 0)
        {
            Console.WriteLine("  Nije pronađen nijedan bežični adapter.");
            Console.WriteLine();
            Console.WriteLine("  To nije greška: na računaru bez Wi-Fi kartice, ili sa isključenim");
            Console.WriteLine("  Wi-Fi-jem, ovaj sloj nema šta da pročita. Nadzor prekida radi");
            Console.WriteLine("  normalno i bez njega - bežični podaci samo omogućavaju da se");
            Console.WriteLine("  kvar rutera razlikuje od izlaska iz dometa.");
            Console.WriteLine();
            return true;
        }

        foreach (var radio in radios)
        {
            Console.WriteLine($"  Adapter:  {radio.Description}");
            Console.WriteLine($"  Stanje:   {radio.State}");
            Console.WriteLine($"  Mreža:    {radio.Ssid ?? "nije povezan"}");
            Console.WriteLine($"  Tačka:    {radio.Bssid ?? "-"}");
            Console.WriteLine($"  Signal:   {(radio.SignalQuality is { } q ? $"{q} %" : "-")}");
            Console.WriteLine($"  Vidljivih mreža u skeniranju: {radio.VisibleNetworks}");
            Console.WriteLine();
        }

        return true;
    }

    private static string Medium(LinkMedium medium) => medium switch
    {
        LinkMedium.Ethernet => "kabl",
        LinkMedium.Wireless => "Wi-Fi",
        _ => "nepoznat",
    };
}
