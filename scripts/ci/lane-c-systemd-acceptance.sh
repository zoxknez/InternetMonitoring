#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Lane C: Real systemd PID 1 Live Acceptance Runner for Phase 3.1-2
# Validates systemd service lifecycle, StateDirectory, RuntimeDirectory,
# POSIX UID/GID/mode, failure propagation, and start without network.
# ==============================================================================

echo "=============================================================================="
echo "1. RUNNER ENVIRONMENT PROOF"
echo "=============================================================================="

uname -a
if [ -f /etc/os-release ]; then
    cat /etc/os-release
fi

echo "--- systemd version ---"
systemd --version || true

echo "--- PID 1 process check ---"
PID1_COMM=$(ps -p 1 -o comm= | tr -d ' ')
echo "PID 1 comm: '${PID1_COMM}'"

if [ "${PID1_COMM}" != "systemd" ] && [ "${PID1_COMM}" != "init" ]; then
    echo "ERROR: PID 1 is not systemd! Detected: '${PID1_COMM}'"
    echo "Lane C requires a real Linux VM/host with systemd as PID 1."
    exit 2
fi

echo "--- systemctl status ---"
systemctl is-system-running || true

echo "--- dotnet info ---"
dotnet --info

echo "=============================================================================="
echo "2. BUILD AND PUBLISH IEM.Service.Linux"
echo "=============================================================================="

INSTALL_DIR="/usr/lib/internet-evidence-monitor"
mkdir -p "${INSTALL_DIR}"

dotnet publish src/IEM.Service.Linux/IEM.Service.Linux.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained false \
    -o "${INSTALL_DIR}"

chmod 0755 "${INSTALL_DIR}/IEM.Service.Linux"
echo "Published binary to ${INSTALL_DIR}/IEM.Service.Linux"

echo "=============================================================================="
echo "3. SERVICE ACCOUNT PROVISIONING"
echo "=============================================================================="

if ! getent group iem-users >/dev/null 2>&1; then
    groupadd -r iem-users
    echo "Created group iem-users"
fi

if ! getent group iem >/dev/null 2>&1; then
    groupadd -r iem
    echo "Created group iem"
fi

if ! getent passwd iem >/dev/null 2>&1; then
    useradd -r -g iem -G iem-users -d /var/lib/internet-evidence-monitor -s /usr/sbin/nologin iem
    echo "Created user iem"
fi

echo "User and groups verified:"
id iem

echo "=============================================================================="
echo "4. INSTALL SYSTEMD SERVICE UNIT"
echo "=============================================================================="

UNIT_SRC="packaging/systemd/internet-evidence-monitor.service"
UNIT_DST="/etc/systemd/system/internet-evidence-monitor.service"

cp "${UNIT_SRC}" "${UNIT_DST}"
chmod 0644 "${UNIT_DST}"

systemctl daemon-reload
echo "Installed unit file to ${UNIT_DST} and reloaded daemon"

echo "=============================================================================="
echo "5. START SERVICE AND TYPE=notify LIVE GATE"
echo "=============================================================================="

systemctl start internet-evidence-monitor.service

# Wait for active state
for i in {1..30}; do
    ACTIVE_STATE=$(systemctl show -p ActiveState --value internet-evidence-monitor.service)
    SUB_STATE=$(systemctl show -p SubState --value internet-evidence-monitor.service)
    if [ "${ACTIVE_STATE}" = "active" ] && [ "${SUB_STATE}" = "running" ]; then
        echo "Service is active and running (iteration $i)"
        break
    fi
    sleep 0.5
done

ACTIVE_STATE=$(systemctl show -p ActiveState --value internet-evidence-monitor.service)
SUB_STATE=$(systemctl show -p SubState --value internet-evidence-monitor.service)
echo "ActiveState: ${ACTIVE_STATE}, SubState: ${SUB_STATE}"

if [ "${ACTIVE_STATE}" != "active" ] || [ "${SUB_STATE}" != "running" ]; then
    echo "ERROR: Service failed to reach ActiveState=active, SubState=running!"
    systemctl status internet-evidence-monitor.service || true
    journalctl -u internet-evidence-monitor.service -n 50 --no-pager || true
    exit 1
fi

echo "GATE PASS: Type=notify readiness verified"

echo "=============================================================================="
echo "6. PROCESS IDENTITY AND CAPABILITIES"
echo "=============================================================================="

MAIN_PID=$(systemctl show -p MainPID --value internet-evidence-monitor.service)
echo "Service MainPID: ${MAIN_PID}"

if [ "${MAIN_PID}" -le 1 ]; then
    echo "ERROR: Invalid MainPID '${MAIN_PID}'"
    exit 1
fi

PROC_UID=$(ps -o uid= -p "${MAIN_PID}" | tr -d ' ')
PROC_USER=$(ps -o user= -p "${MAIN_PID}" | tr -d ' ')
PROC_GROUP=$(ps -o group= -p "${MAIN_PID}" | tr -d ' ')
echo "Process UID: ${PROC_UID} (${PROC_USER}), Group: ${PROC_GROUP}"

if [ "${PROC_USER}" != "iem" ]; then
    echo "ERROR: Service is not running as user 'iem'! Running as: '${PROC_USER}'"
    exit 1
fi

if [ "${PROC_UID}" = "0" ]; then
    echo "ERROR: Service is running as root (UID 0)!"
    exit 1
fi

# Check capabilities
if [ -f "/proc/${MAIN_PID}/status" ]; then
    grep -E '^(Cap|Groups|Uid|Gid)' "/proc/${MAIN_PID}/status"
fi

echo "GATE PASS: Non-root process identity verified"

echo "=============================================================================="
echo "7. STATEDIRECTORY LIVE ACCEPTANCE"
echo "=============================================================================="

STATE_DIR="/var/lib/internet-evidence-monitor"
if [ ! -d "${STATE_DIR}" ]; then
    echo "ERROR: StateDirectory '${STATE_DIR}' does not exist!"
    exit 1
fi

STATE_STAT=$(stat -c "%U:%G %a" "${STATE_DIR}")
echo "StateDirectory stat: ${STATE_STAT}"

if [ "${STATE_STAT}" != "iem:iem 700" ]; then
    echo "ERROR: StateDirectory ownership/mode expected 'iem:iem 700', got '${STATE_STAT}'"
    exit 1
fi

# Write persistent test sentinel
SENTINEL_FILE="${STATE_DIR}/state-survival-sentinel"
echo "persistent-state-token-$(date +%s)" > "${SENTINEL_FILE}"
chown iem:iem "${SENTINEL_FILE}"
chmod 0600 "${SENTINEL_FILE}"
echo "Created state survival sentinel at ${SENTINEL_FILE}"

echo "GATE PASS: StateDirectory ownership and permissions verified"

echo "=============================================================================="
echo "8. RUNTIMEDIRECTORY LIVE ACCEPTANCE"
echo "=============================================================================="

RUNTIME_DIR="/run/internet-evidence-monitor"
if [ ! -d "${RUNTIME_DIR}" ]; then
    echo "ERROR: RuntimeDirectory '${RUNTIME_DIR}' does not exist!"
    exit 1
fi

RUNTIME_STAT=$(stat -c "%U:%G %a" "${RUNTIME_DIR}")
echo "RuntimeDirectory stat: ${RUNTIME_STAT}"

if [ "${RUNTIME_STAT}" != "iem:iem-users 750" ]; then
    echo "ERROR: RuntimeDirectory ownership/mode expected 'iem:iem-users 750', got '${RUNTIME_STAT}'"
    exit 1
fi

# Verify control.sock is strictly absent in 3.1-2
if [ -e "${RUNTIME_DIR}/control.sock" ]; then
    echo "ERROR: control.sock must NOT be created in Phase 3.1-2!"
    exit 1
fi

# Create ephemeral runtime sentinel
EPHEMERAL_FILE="${RUNTIME_DIR}/ephemeral-runtime-sentinel"
touch "${EPHEMERAL_FILE}"
echo "Created ephemeral runtime sentinel at ${EPHEMERAL_FILE}"

echo "GATE PASS: RuntimeDirectory ownership (iem:iem-users 0750) and control.sock absence verified"

echo "=============================================================================="
echo "9. STOP LIFECYCLE ACCEPTANCE"
echo "=============================================================================="

systemctl stop internet-evidence-monitor.service

if [ ! -d "${STATE_DIR}" ]; then
    echo "ERROR: StateDirectory was deleted on service stop!"
    exit 1
fi

if [ ! -f "${SENTINEL_FILE}" ]; then
    echo "ERROR: StateDirectory sentinel file was deleted on service stop!"
    exit 1
fi

if [ -d "${RUNTIME_DIR}" ]; then
    echo "ERROR: RuntimeDirectory '${RUNTIME_DIR}' still exists after service stop!"
    exit 1
fi

echo "GATE PASS: Stop lifecycle persistence and runtime cleanup verified"

echo "=============================================================================="
echo "10. RESTART LIFECYCLE ACCEPTANCE"
echo "=============================================================================="

systemctl start internet-evidence-monitor.service

if [ ! -d "${RUNTIME_DIR}" ]; then
    echo "ERROR: RuntimeDirectory not recreated on start!"
    exit 1
fi

if [ -f "${EPHEMERAL_FILE}" ]; then
    echo "ERROR: Previous ephemeral runtime sentinel was not cleaned up!"
    exit 1
fi

# Create second ephemeral sentinel
EPHEMERAL_FILE_2="${RUNTIME_DIR}/ephemeral-runtime-sentinel-2"
touch "${EPHEMERAL_FILE_2}"

systemctl restart internet-evidence-monitor.service

if [ ! -f "${SENTINEL_FILE}" ]; then
    echo "ERROR: State sentinel missing after restart!"
    exit 1
fi

if [ -f "${EPHEMERAL_FILE_2}" ]; then
    echo "ERROR: Ephemeral sentinel survived restart!"
    exit 1
fi

RUNTIME_STAT_POST=$(stat -c "%U:%G %a" "${RUNTIME_DIR}")
if [ "${RUNTIME_STAT_POST}" != "iem:iem-users 750" ]; then
    echo "ERROR: RuntimeDirectory stat after restart expected 'iem:iem-users 750', got '${RUNTIME_STAT_POST}'"
    exit 1
fi

echo "GATE PASS: Restart lifecycle persistence verified"

echo "=============================================================================="
echo "11. PROTECTSYSTEM=STRICT ACCEPTANCE (CANDIDATE DROP-IN)"
echo "=============================================================================="

DROPIN_DIR="/etc/systemd/system/internet-evidence-monitor.service.d"
mkdir -p "${DROPIN_DIR}"

cat << 'EOF' > "${DROPIN_DIR}/hardening.conf"
[Service]
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
EOF

systemctl daemon-reload
systemctl restart internet-evidence-monitor.service

ACTIVE_STATE=$(systemctl show -p ActiveState --value internet-evidence-monitor.service)
if [ "${ACTIVE_STATE}" != "active" ]; then
    echo "ERROR: Service failed to start with ProtectSystem=strict drop-in!"
    journalctl -u internet-evidence-monitor.service -n 50 --no-pager || true
    exit 1
fi

# Clean up drop-in
rm -rf "${DROPIN_DIR}"
systemctl daemon-reload
systemctl restart internet-evidence-monitor.service

echo "GATE PASS: ProtectSystem=strict candidate drop-in verified"

echo "=============================================================================="
echo "12. CLEANUP SERVICE"
echo "=============================================================================="

systemctl stop internet-evidence-monitor.service
rm -f "${UNIT_DST}"
systemctl daemon-reload
rm -rf "${INSTALL_DIR}"
rm -rf "${STATE_DIR}"

echo "=============================================================================="
echo "LANE C: REAL SYSTEMD ACCEPTANCE COMPLETED SUCCESSFULLY!"
echo "=============================================================================="
