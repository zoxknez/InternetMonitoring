namespace IEM.Linux.Network.Netlink;

/// <summary>
/// Linux Netlink & rtnetlink constants from linux/netlink.h and linux/rtnetlink.h.
/// Invariants 271-275.
/// </summary>
public static class NetlinkConstants
{
    public const int AF_NETLINK = 16;
    public const int NETLINK_ROUTE = 0;

    // nlmsghdr types
    public const ushort NLMSG_NOOP = 1;
    public const ushort NLMSG_ERROR = 2;
    public const ushort NLMSG_DONE = 3;
    public const ushort NLMSG_OVERRUN = 4;

    public const ushort RTM_NEWROUTE = 24;
    public const ushort RTM_DELROUTE = 25;
    public const ushort RTM_GETROUTE = 26;

    // nlmsghdr flags
    public const ushort NLM_F_REQUEST = 0x01;
    public const ushort NLM_F_MULTI = 0x02;
    public const ushort NLM_F_ACK = 0x04;
    public const ushort NLM_F_ECHO = 0x08;

    // rtmsg rtm_table
    public const byte RT_TABLE_MAIN = 254;

    // rtattr types (linux/rtnetlink.h)
    public const ushort RTA_UNSPEC = 0;
    public const ushort RTA_DST = 1;
    public const ushort RTA_SRC = 2;
    public const ushort RTA_IIF = 3;
    public const ushort RTA_OIF = 4;
    public const ushort RTA_GATEWAY = 5;
    public const ushort RTA_PRIORITY = 6;
    public const ushort RTA_PREFSRC = 7;
    public const ushort RTA_METRICS = 8;
    public const ushort RTA_MULTIPATH = 10;
    public const ushort RTA_PROTOINFO = 11;
    public const ushort RTA_FLOW = 12;
    public const ushort RTA_CACHEINFO = 13;
    public const ushort RTA_TABLE = 15;
    public const ushort RTA_MARK = 16;
    public const ushort RTA_MFC_STATS = 17;
    public const ushort RTA_VIA = 18;
    public const ushort RTA_NEWDST = 19;
    public const ushort RTA_PREF = 20;
    public const ushort RTA_ENCAP_TYPE = 21;
    public const ushort RTA_ENCAP = 22;
    public const ushort RTA_EXPIRES = 23;
    public const ushort RTA_PAD = 24;
    public const ushort RTA_UID = 25;
    public const ushort RTA_TTL_PROPAGATE = 26;
    public const ushort RTA_IP_PROTO = 27;
    public const ushort RTA_SPORT = 28;
    public const ushort RTA_DPORT = 29;
    public const ushort RTA_NH_ID = 30;

    // Standard Linux alignment macro helpers
    public static int NlmsgAlign(int len) => (len + 3) & ~3;
    public static int RtaAlign(int len) => (len + 3) & ~3;

    public const int NlmsgHeaderSize = 16;
    public const int RtmsgHeaderSize = 12;
    public const int RtattrHeaderSize = 4;
}
