using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using IEM.Linux.Network.Netlink;

namespace IEM.Linux.Wifi;

/// <summary>
/// Production rfkill reader that scopes positive block/unblock facts to a specific wiphy.
/// Reads /dev/rfkill as primary authority with sysfs correlation and fallback.
/// Invariants 249, 252.
/// </summary>
public sealed class LinuxRfkillReader : ILinuxRfkillReader
{
    public const byte RFKILL_TYPE_ALL = 0;
    public const byte RFKILL_TYPE_WLAN = 1;

    public const byte RFKILL_OP_ADD = 0;
    public const byte RFKILL_OP_DEL = 1;
    public const byte RFKILL_OP_CHANGE = 2;

    public static LinuxRfkillReader Instance { get; } = new();

    public LinuxRfkillObservation? ReadObservationForWiphy(uint wiphyIndex, string? ifname = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return null;
        }

        // 1. Try reading /dev/rfkill as primary authority
        var devObs = TryReadFromDevRfkill(wiphyIndex);
        if (devObs != null)
        {
            return devObs;
        }

        // 2. Try sysfs under /sys/class/net/{ifname}/phy80211/rfkill*
        if (!string.IsNullOrEmpty(ifname))
        {
            var netSysfsObs = TryReadFromNetSysfs(wiphyIndex, ifname);
            if (netSysfsObs != null)
            {
                return netSysfsObs;
            }
        }

        // 3. Try global sysfs /sys/class/rfkill/rfkill*
        return TryReadFromClassSysfs(wiphyIndex);
    }

    private static LinuxRfkillObservation? TryReadFromDevRfkill(uint wiphyIndex)
    {
        const string devPath = "/dev/rfkill";
        if (!File.Exists(devPath))
        {
            return null;
        }

        const int O_RDONLY = 0;
        const int O_NONBLOCK = 0x800;
        const int O_CLOEXEC = 0x80000;

        int fd = -1;
        try
        {
            fd = LinuxNativeNetlinkSocket.NativeMethods.open(devPath, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
            if (fd < 0)
            {
                return null;
            }

            var buffer = new byte[8];
            LinuxRfkillObservation? matched = null;

            unsafe
            {
                fixed (byte* pBuf = buffer)
                {
                    while (true)
                    {
                        nint read = LinuxNativeNetlinkSocket.NativeMethods.read(fd, pBuf, 8);
                        if (read < 8)
                        {
                            // EAGAIN / EOF / short read indicates end of current snapshot
                            break;
                        }

                        uint idx = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, 4));
                        byte type = buffer[4];
                        byte op = buffer[5];
                        byte soft = buffer[6];
                        byte hard = buffer[7];

                        if (type != RFKILL_TYPE_WLAN && type != RFKILL_TYPE_ALL)
                        {
                            continue;
                        }

                        if (op == RFKILL_OP_DEL)
                        {
                            if (matched != null && matched.RfkillIndex == (int)idx)
                            {
                                matched = null;
                            }
                            continue;
                        }

                        // Correlate rfkill index to wiphyIndex
                        if (IsRfkillBoundToWiphy((int)idx, wiphyIndex))
                        {
                            matched = new LinuxRfkillObservation(
                                (int)idx,
                                wiphyIndex,
                                HardBlocked: hard == 1,
                                SoftBlocked: soft == 1,
                                LinuxRfkillEvidenceBasis.DevRfkill);
                        }
                    }
                }
            }

            return matched;
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
        finally
        {
            if (fd >= 0)
            {
                LinuxNativeNetlinkSocket.NativeMethods.close(fd);
            }
        }
    }

    private static bool IsRfkillBoundToWiphy(int rfkillIndex, uint wiphyIndex)
    {
        string rfkillDir = $"/sys/class/rfkill/rfkill{rfkillIndex}";
        if (!Directory.Exists(rfkillDir))
        {
            return false;
        }

        try
        {
            // 1. Check if name is "phy{wiphyIndex}"
            string namePath = Path.Combine(rfkillDir, "name");
            if (File.Exists(namePath))
            {
                string name = File.ReadAllText(namePath).Trim();
                if (name.Equals($"phy{wiphyIndex}", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // 2. Check device/phy80211 symlink target or phy_name
            string phyNamePath = Path.Combine(rfkillDir, "device", "phy_name");
            if (File.Exists(phyNamePath))
            {
                string phyName = File.ReadAllText(phyNamePath).Trim();
                if (phyName.Equals($"phy{wiphyIndex}", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return false;
    }

    private static LinuxRfkillObservation? TryReadFromNetSysfs(uint wiphyIndex, string ifname)
    {
        string netPhyDir = $"/sys/class/net/{ifname}/phy80211";
        if (!Directory.Exists(netPhyDir))
        {
            return null;
        }

        try
        {
            var rfkillDirs = Directory.GetDirectories(netPhyDir, "rfkill*");
            foreach (var rkDir in rfkillDirs)
            {
                var obs = ReadRfkillDir(rkDir, wiphyIndex, LinuxRfkillEvidenceBasis.SysfsPhy);
                if (obs != null)
                {
                    return obs;
                }
            }
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return null;
    }

    private static LinuxRfkillObservation? TryReadFromClassSysfs(uint wiphyIndex)
    {
        const string classDir = "/sys/class/rfkill";
        if (!Directory.Exists(classDir))
        {
            return null;
        }

        try
        {
            var rfkillDirs = Directory.GetDirectories(classDir, "rfkill*");
            foreach (var rkDir in rfkillDirs)
            {
                string dirName = Path.GetFileName(rkDir);
                if (dirName.StartsWith("rfkill", StringComparison.Ordinal) &&
                    int.TryParse(dirName.AsSpan(6), out int idx) &&
                    IsRfkillBoundToWiphy(idx, wiphyIndex))
                {
                    var obs = ReadRfkillDir(rkDir, wiphyIndex, LinuxRfkillEvidenceBasis.SysfsClass);
                    if (obs != null)
                    {
                        return obs;
                    }
                }
            }
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return null;
    }

    private static LinuxRfkillObservation? ReadRfkillDir(string rkDir, uint wiphyIndex, LinuxRfkillEvidenceBasis basis)
    {
        string softPath = Path.Combine(rkDir, "soft");
        string hardPath = Path.Combine(rkDir, "hard");
        string indexPath = Path.Combine(rkDir, "index");

        if (!File.Exists(softPath) || !File.Exists(hardPath))
        {
            return null;
        }

        try
        {
            int softVal = int.Parse(File.ReadAllText(softPath).Trim());
            int hardVal = int.Parse(File.ReadAllText(hardPath).Trim());
            int rkIdx = File.Exists(indexPath) && int.TryParse(File.ReadAllText(indexPath).Trim(), out int i) ? i : -1;

            return new LinuxRfkillObservation(
                rkIdx,
                wiphyIndex,
                HardBlocked: hardVal == 1,
                SoftBlocked: softVal == 1,
                basis);
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
