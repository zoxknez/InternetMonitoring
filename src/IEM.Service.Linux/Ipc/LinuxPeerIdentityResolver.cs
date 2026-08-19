using System.Net.Sockets;
using System.Runtime.InteropServices;
using IEM.Core.Ipc;

namespace IEM.Service.Linux.Ipc;

/// <summary>
/// Authoritative peer identity extractor using Linux SO_PEERCRED, SO_PEERGROUPS,
/// and hardened /proc/&lt;pid&gt;/status fallback with PID reuse validation.
/// Invariants 84, 94, 95, 261-268.
/// </summary>
public static class LinuxPeerIdentityResolver
{
    public static PlatformPeerIdentity Resolve(Socket clientSocket)
    {
        ArgumentNullException.ThrowIfNull(clientSocket);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return PlatformPeerIdentity.Unknown;
        }

        try
        {
            var handle = clientSocket.Handle;

            // 1. Query SO_PEERCRED
            var cred = new LinuxSocketInterop.UCred();
            var credLen = Marshal.SizeOf<LinuxSocketInterop.UCred>();
            if (LinuxSocketInterop.getsockopt(handle, LinuxSocketInterop.SOL_SOCKET, LinuxSocketInterop.SO_PEERCRED, out cred, ref credLen) != 0)
            {
                return PlatformPeerIdentity.Unknown;
            }

            var pid = cred.pid;
            var uid = cred.uid;
            var gid = cred.gid;

            var peerGroups = new HashSet<uint> { gid };

            // 2. Query SO_PEERGROUPS (Kernel UAPI primary authority)
            var groupsBuf = new byte[1024];
            var groupsLen = groupsBuf.Length;
            var peerGroupsRet = LinuxSocketInterop.getsockopt(handle, LinuxSocketInterop.SOL_SOCKET, LinuxSocketInterop.SO_PEERGROUPS, groupsBuf, ref groupsLen);

            if (peerGroupsRet == 0 && groupsLen >= sizeof(uint))
            {
                var count = groupsLen / sizeof(uint);
                for (var i = 0; i < count; i++)
                {
                    var groupGid = BitConverter.ToUInt32(groupsBuf, i * sizeof(uint));
                    peerGroups.Add(groupGid);
                }
            }
            else if (pid > 0)
            {
                // 3. Hardened /proc/<pid>/status fallback with strict UID/GID validation against PID reuse
                if (!TryReadProcStatusGroups(pid, uid, gid, peerGroups))
                {
                    // PID mismatch, process vanished, or unreadable -> Fail closed
                    return PlatformPeerIdentity.Unknown;
                }
            }

            // 4. Map GIDs to canonical roles
            var claims = new List<string>();
            var iemAdminGid = LookupGid("iem-admin");
            var iemUsersGid = LookupGid("iem-users");

            var isSystemAdmin = uid == 0;
            var isIemAdmin = iemAdminGid.HasValue && peerGroups.Contains(iemAdminGid.Value);
            var isIemUser = iemUsersGid.HasValue && peerGroups.Contains(iemUsersGid.Value);

            if (isSystemAdmin || isIemAdmin)
            {
                claims.Add(PlatformPeerIdentity.RoleAdmin);
                claims.Add(PlatformPeerIdentity.RoleOperator);
            }
            else if (isIemUser)
            {
                claims.Add(PlatformPeerIdentity.RoleOperator);
            }

            return PlatformPeerIdentity.CreateUnix((int)uid, (int)gid, pid, claims);
        }
        catch
        {
            return PlatformPeerIdentity.Unknown;
        }
    }

    private static bool TryReadProcStatusGroups(int pid, uint expectedUid, uint expectedGid, HashSet<uint> targetGroups)
    {
        var procPath = $"/proc/{pid}/status";
        if (!File.Exists(procPath))
        {
            return false;
        }

        try
        {
            var lines = File.ReadAllLines(procPath);
            bool uidMatched = false;
            bool gidMatched = false;

            foreach (var line in lines)
            {
                if (line.StartsWith("Uid:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length > 1 && uint.TryParse(parts[1], out var procRealUid) && procRealUid == expectedUid)
                    {
                        uidMatched = true;
                    }
                }
                else if (line.StartsWith("Gid:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length > 1 && uint.TryParse(parts[1], out var procRealGid) && procRealGid == expectedGid)
                    {
                        gidMatched = true;
                    }
                }
                else if (line.StartsWith("Groups:", StringComparison.OrdinalIgnoreCase))
                {
                    var groupsStr = line["Groups:".Length..].Trim();
                    var gids = groupsStr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var g in gids)
                    {
                        if (uint.TryParse(g, out var parsedGid))
                        {
                            targetGroups.Add(parsedGid);
                        }
                    }
                }
            }

            // Must match both UID and GID to prevent PID reuse attack
            return uidMatched && gidMatched;
        }
        catch
        {
            return false;
        }
    }

    private static uint? LookupGid(string groupName)
    {
        try
        {
            var grpPtr = LinuxSocketInterop.getgrnam(groupName);
            if (grpPtr == IntPtr.Zero)
            {
                return null;
            }

            var gidOffset = 2 * IntPtr.Size;
            var gid = Marshal.ReadInt32(grpPtr, gidOffset);
            return gid >= 0 ? (uint)gid : null;
        }
        catch
        {
            return null;
        }
    }
}
