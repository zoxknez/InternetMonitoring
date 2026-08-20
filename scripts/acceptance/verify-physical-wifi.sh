#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# 3.1-7B · Physical Wi-Fi Acceptance Runner (Fail-Closed Gate)
# Strict, audit-grade verification on a real bare-metal Linux host with real Wi-Fi adapter:
#
# Proves:
# 1. Root-orchestrated execution running unprivileged user iem (CapEff=0, CapAmb=0)
# 2. Production LinuxProbeFactory -> LinuxLinkInspectionScope -> LinuxWifiLinkInspector composition
# 3. Exact interface identity matching kernel (IFINDEX, WDEV, WIPHY, MAC)
# 4. Association truth (Associated, SSID, raw BSSID, frequency, signal)
# 5. Cached BSS truth from passive GET_SCAN dump (NO TRIGGER_SCAN active scanning)
# 6. Station peer truth (scoped to IFINDEX + WDEV + raw peer MAC)
# 7. Numeric counter monotonicity over live traffic interval (RX/TX bytes & packets)
# 8. Access point evidence resolution (BSSID, channel, RSSI)
# 9. iw reference cross-check captured for independent verification
# ==============================================================================

ACCEPTANCE_DIR="artifacts/acceptance/3.1-7B"
mkdir -p "${ACCEPTANCE_DIR}"
REPORT_JSON="${ACCEPTANCE_DIR}/wifi-physical.json"
REPORT_MD="${ACCEPTANCE_DIR}/wifi-physical.md"

echo "=============================================================================="
echo "3.1-7B · PHYSICAL WI-FI ACCEPTANCE RUNNER"
echo "=============================================================================="

if [ "$(id -u)" -ne 0 ]; then
    echo "ERROR: Acceptance runner must be run as root for system and service orchestration." >&2
    exit 1
fi

COMMIT_SHA="${GITHUB_SHA:-$(git rev-parse HEAD 2>/dev/null || echo 'UNKNOWN')}"
KERNEL_INFO=$(uname -r)
ARCH_INFO=$(uname -m)
DISTRO_NAME="Linux"
DISTRO_VER="Unknown"
if [ -f /etc/os-release ]; then
    DISTRO_NAME=$(grep -E '^ID=' /etc/os-release | cut -d= -f2 | tr -d '"')
    DISTRO_VER=$(grep -E '^VERSION_ID=' /etc/os-release | cut -d= -f2 | tr -d '"')
fi

# Detect wireless interfaces
IFACE="${1:-}"
if [ -z "${IFACE}" ]; then
    DETECTED_IFACES=()
    if command -v iw >/dev/null 2>&1; then
        while IFS= read -r ifname; do
            [ -n "${ifname}" ] && DETECTED_IFACES+=("${ifname}")
        done < <(iw dev 2>/dev/null | grep Interface | awk '{print $2}' || true)
    fi

    if [ ${#DETECTED_IFACES[@]} -eq 0 ] && [ -d /sys/class/net ]; then
        for f in /sys/class/net/*; do
            if [ -d "$f/wireless" ] || [ -d "$f/phy80211" ]; then
                DETECTED_IFACES+=("$(basename "$f")")
            fi
        done
    fi

    if [ ${#DETECTED_IFACES[@]} -eq 0 ]; then
        echo "ERROR: No physical wireless interfaces detected on this host." >&2
        echo "OverallVerdict: NOT_TESTED (No wireless hardware)" >&2
        exit 2
    elif [ ${#DETECTED_IFACES[@]} -gt 1 ]; then
        echo "ERROR: Multiple wireless interfaces found (${DETECTED_IFACES[*]}). Please specify target interface explicitly: $0 <interface>" >&2
        exit 2
    else
        IFACE="${DETECTED_IFACES[0]}"
        echo "Auto-detected single wireless interface: ${IFACE}"
    fi
else
    echo "Target wireless interface explicitly specified: ${IFACE}"
fi

# 1. Setup Canonical Service Accounts and Permissions (Lane C pattern)
getent group iem-users >/dev/null 2>&1 || groupadd -r iem-users
getent group iem >/dev/null 2>&1 || groupadd -r iem
getent passwd iem >/dev/null 2>&1 || useradd -r -g iem -G iem-users -d /var/lib/internet-evidence-monitor -s /usr/sbin/nologin iem
chown -R iem:iem "${ACCEPTANCE_DIR}"
chmod 0750 "${ACCEPTANCE_DIR}"

# 2. Capture Host and Driver Provenance
echo "Capturing host provenance..."
{
    echo "Commit SHA: ${COMMIT_SHA}"
    echo "Kernel: ${KERNEL_INFO}"
    echo "Architecture: ${ARCH_INFO}"
    echo "Distro: ${DISTRO_NAME} ${DISTRO_VER}"
    echo "Date UTC: $(date -u +'%Y-%m-%d %H:%M:%S UTC')"
    echo "Interface: ${IFACE}"
    echo ""
    echo "=== ip link show ==="
    ip link show "${IFACE}" 2>&1 || true
    echo ""
    echo "=== ethtool -i ==="
    ethtool -i "${IFACE}" 2>&1 || true
    echo ""
    echo "=== Loaded wireless modules ==="
    lsmod | grep -E 'cfg80211|mac80211|iwl|ath|rtw|mt7' 2>&1 || true
    echo ""
    echo "=== /proc/self/status ==="
    grep -E 'Cap(Inh|Prm|Eff|Bnd|Amb):' /proc/self/status 2>&1 || true
} > "${ACCEPTANCE_DIR}/host-manifest.txt"

# 3. Capture iw reference diagnostics (cross-check only; passive queries, NO active scan)
if command -v iw >/dev/null 2>&1; then
    echo "Capturing iw reference diagnostics..."
    iw dev "${IFACE}" info > "${ACCEPTANCE_DIR}/iw-dev-info.txt" 2>&1 || true
    iw dev "${IFACE}" link > "${ACCEPTANCE_DIR}/iw-link-before.txt" 2>&1 || true
    iw dev "${IFACE}" station dump > "${ACCEPTANCE_DIR}/iw-station.txt" 2>&1 || true
fi

# 4. Build and Publish IEM.WifiRunner
INSTALL_DIR="/usr/lib/internet-evidence-monitor"
WIFI_RUNNER="${INSTALL_DIR}/tools/IEM.WifiRunner"

echo "Building and publishing IEM.WifiRunner..."
mkdir -p "${INSTALL_DIR}/tools"
dotnet publish tools/IEM.WifiRunner/IEM.WifiRunner.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o "${INSTALL_DIR}/tools"
chmod 0755 "${WIFI_RUNNER}"

# 5. Run IEM.WifiRunner Strictly as Unprivileged User iem
echo "Executing IEM.WifiRunner strictly as user 'iem' (zero capabilities)..."
RUNNER_EXIT=0
su -s /bin/bash iem -c "${WIFI_RUNNER} --interface ${IFACE} --json ${REPORT_JSON} --markdown ${REPORT_MD} --traffic-seconds 2" || RUNNER_EXIT=$?

# 6. Capture iw reference after
if command -v iw >/dev/null 2>&1; then
    iw dev "${IFACE}" link > "${ACCEPTANCE_DIR}/iw-link-after.txt" 2>&1 || true
fi

echo ""
echo "=============================================================================="
echo "ACCEPTANCE RUN COMPLETE (Exit Code: ${RUNNER_EXIT})"
echo "Evidence artifacts generated in ${ACCEPTANCE_DIR}/"
echo "=============================================================================="

exit "${RUNNER_EXIT}"
