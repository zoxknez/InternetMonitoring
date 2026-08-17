using System.Net;
using IEM.Core.Probes;
using IEM.Windows;

namespace IEM.Cli;

/// <summary>
/// Prints the adapter and source address traffic to each probe target will leave through.
/// <para>
/// A check worth running before committing to a two-day test. Windows decides this per
/// destination, so a customer monitoring "the Wi-Fi" while a docking station or a VPN
/// quietly carries the traffic would otherwise collect two days of evidence about a link
/// that was never under test - and would only discover it when the operator pointed it out.
/// </para>
/// </summary>
public static class PathCommand
{
    /// <param name="single">One address to resolve instead of the usual probe targets.</param>
    public static bool Run(string? single = null, ProbeOptions? options = null)
    {
        var probes = options ?? ProbeOptions.Default;
        var resolver = new RouteResolver();

        Console.WriteLine();
        Console.WriteLine("  MREŽNA PUTANJA DO META");
        Console.WriteLine("  ─────────────────────────────────────────────");
        Console.WriteLine();

        var adapters = System.Net.NetworkInformation.NetworkInterface
            .GetAllNetworkInterfaces()
            .ToDictionary(a => a.Id, a => a.Name, StringComparer.Ordinal);

        var used = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = 0;

        foreach (var target in single is null ? Targets(probes) : [single])
        {
            if (!IPAddress.TryParse(target, out var address))
            {
                continue;
            }

            var path = resolver.Resolve(address);

            // ProvesLink rather than Resolved: a route whose adapter cannot be identified
            // still tells us a source address, but it cannot say which link carried the
            // traffic - and that is the only thing this command exists to establish.
            if (!path.ProvesLink)
            {
                unresolved++;

                Console.WriteLine(path.Resolved
                    ? $"  {target,-24}  adapter nije prepoznat  ({path.SourceAddress})"
                    : $"  {target,-24}  putanja nije utvrđena");

                continue;
            }

            var name = adapters.TryGetValue(path.InterfaceId!, out var adapter) ? adapter : path.InterfaceId!;

            used.Add(name);
            Console.WriteLine($"  {target,-24}  {name}  ({path.SourceAddress})");
        }

        Console.WriteLine();

        if (unresolved > 0)
        {
            WriteWarning($"  Za {unresolved} meta putanja nije utvrđena.");
            Console.WriteLine();
        }

        switch (used.Count)
        {
            case 0:
                WriteWarning("  Nijedna putanja nije utvrđena. Da li je mreža uopšte dostupna?");
                break;

            case 1:
                Console.WriteLine($"  Sav saobraćaj izlazi kroz: {used.First()}");
                Console.WriteLine("  Ovo je stanje u kom nadzor daje čist dokaz o jednoj vezi.");
                break;

            default:
                WriteWarning("  Saobraćaj izlazi kroz više adaptera:");

                foreach (var name in used.Order(StringComparer.CurrentCulture))
                {
                    Console.WriteLine($"    • {name}");
                }

                Console.WriteLine();
                Console.WriteLine("  Nadzor će i dalje raditi, ali prekid izmeren u ovakvom stanju");
                Console.WriteLine("  ne dokazuje kvar ni na jednoj konkretnoj vezi. Za prigovor");
                Console.WriteLine("  operateru isključite VPN i ostale aktivne adaptere.");
                break;
        }

        Console.WriteLine();
        return used.Count == 1 && unresolved == 0;
    }

    /// <summary>
    /// Every distinct address the monitor will probe. IPv6 targets are listed only when the
    /// machine actually has IPv6 - otherwise the command would report a routing problem for
    /// a protocol that was never in use, on the overwhelming majority of Serbian connections.
    /// </summary>
    private static IEnumerable<string> Targets(ProbeOptions options)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var candidates = options.IcmpV4Targets
            .Concat(options.TcpTargets.Select(t => t[..t.LastIndexOf(':')]))
            .Append(options.PublicResolver);

        if (Ipv6Availability.IsAvailable())
        {
            candidates = candidates.Concat(options.IcmpV6Targets);
        }

        foreach (var target in candidates)
        {
            if (seen.Add(target))
            {
                yield return target;
            }
        }
    }

    private static void WriteWarning(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }
}
