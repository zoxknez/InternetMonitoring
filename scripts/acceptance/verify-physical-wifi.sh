#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# 3.1-7B · Physical Wi-Fi Acceptance Runner
# Strict, audit-grade verification on a real bare-metal Linux host with real Wi-Fi adapter:
#
# Proves:
# 1. Unprivileged user iem runs IEM.WifiRunner via production LinuxProbeFactory (CapEff=0, CapAmb=0)
# 2. Production LinuxWifiLinkInspector + LinuxNl80211Radio composition path executed
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

COMMIT_SHA="${GITHUB_SHA:-$(git rev-parse HEAD 2>/dev/null || echo 'UNKNOWN')}"
KERNEL_INFO=$(uname -r)
ARCH_INFO=$(uname -m)
DISTRO_NAME="Linux"
DISTRO_VER="Unknown"
if [ -f /etc/os-release ]; then
    DISTRO_NAME=$(grep -E '^ID=' /etc/os-release | cut -d= -f2 | tr -d '"')
    DISTRO_VER=$(grep -E '^VERSION_ID=' /etc/os-release | cut -d= -f2 | tr -d '"')
fi

# Detect first wireless interface if not specified
IFACE="${1:-}"
if [ -z "${IFACE}" ]; then
    if command -v iw >/dev/null 2>&1; then
        IFACE=$(iw dev 2>/dev/null | grep Interface | awk '{print $2}' | head -n 1 || echo "")
    fi
    if [ -z "${IFACE}" ] && [ -d /sys/class/net ]; then
        for f in /sys/class/net/*; do
            if [ -d "$f/wireless" ] || [ -d "$f/phy80211" ]; then
                IFACE=$(basename "$f")
                break
            fi
        done
    fi
fi

if [ -z "${IFACE}" ]; then
    echo "WARNING: No wireless interface detected automatically. Defaulting to 'wlan0'."
    IFACE="wlan0"
else
    echo "Target wireless interface: ${IFACE}"
fi

# 1. Capture Host and Driver Provenance
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

# 2. Capture iw reference diagnostics (cross-check only, not IEM authority)
if command -v iw >/dev/null 2>&1; then
    echo "Capturing iw reference diagnostics..."
    iw dev "${IFACE}" info > "${ACCEPTANCE_DIR}/iw-dev-info.txt" 2>&1 || true
    iw dev "${IFACE}" link > "${ACCEPTANCE_DIR}/iw-link-before.txt" 2>&1 || true
    iw dev "${IFACE}" station dump > "${ACCEPTANCE_DIR}/iw-station.txt" 2>&1 || true
fi

# 3. Build and Publish IEM.WifiRunner
INSTALL_DIR="/usr/lib/internet-evidence-monitor"
WIFI_RUNNER="${INSTALL_DIR}/tools/IEM.WifiRunner"

echo "Building and publishing IEM.WifiRunner..."
mkdir -p "${INSTALL_DIR}/tools"
dotnet publish tools/IEM.WifiRunner/IEM.WifiRunner.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o "${INSTALL_DIR}/tools"
chmod 0755 "${WIFI_RUNNER}"

# 4. Ensure canonical iem user exists
if [ "$(id -u)" -eq 0 ]; then
    getent group iem-users >/dev/null 2>&1 || groupadd -r iem-users
    getent group iem >/dev/null 2>&1 || groupadd -r iem
    getent passwd iem >/dev/null 2>&1 || useradd -r -g iem -G iem-users -d /var/lib/internet-evidence-monitor -s /usr/sbin/nologin iem
    chown -R iem:iem "${ACCEPTANCE_DIR}" 2>/dev/null || true
    chmod 0777 "${ACCEPTANCE_DIR}" 2>/dev/null || true
fi

# 5. Run IEM.WifiRunner
echo "Executing IEM.WifiRunner with zero capabilities..."
if [ "$(id -u)" -eq 0 ]; then
    su -s /bin/bash iem -c "${WIFI_RUNNER} --interface ${IFACE} --json ${REPORT_JSON} --markdown ${REPORT_MD} --traffic-seconds 2"
else
    "${WIFI_RUNNER}" --interface "${IFACE}" --json "${REPORT_JSON}" --markdown "${REPORT_MD}" --traffic-seconds 2
fi

# 6. Capture iw reference after
if command -v iw >/dev/null 2>&1; then
    iw dev "${IFACE}" link > "${ACCEPTANCE_DIR}/iw-link-after.txt" 2>&1 || true
fi

echo ""
echo "=============================================================================="
echo "ACCEPTANCE RUN COMPLETE"
echo "Evidence artifacts generated in ${ACCEPTANCE_DIR}/"
echo "=============================================================================="
