#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Lane C: Real systemd PID 1 Live Acceptance Runner for Phase 3.1-2
# Strict, audit-grade verification of:
# - PID 1 == systemd (strict)
# - Type=notify & Result=success lifecycle (strict)
# - Numeric /proc/$PID/status UID, GID, supplementary GID in Groups: line
# - Capability bounding (CapAmb=0, no CAP_NET_ADMIN / CAP_NET_RAW)
# - StateDirectory (iem:iem 0700) persistence through stop/restart
# - RuntimeDirectory (iem:iem-users 0750) ephemeral cleanup on stop
# - ProtectSystem=strict causal 3-point test (DAC allowed -> Namespace denied -> State allowed)
# - FatalExitCode == 3 strict live proof via real pre-flight failure condition
# - systemd Restart=on-failure with bounded 20s polling and metric capture
# - Start without network truthful evaluation (NOT_TESTED on hosted runners)
# - Guaranteed JSON & Markdown evidence generation even on early failure
# ==============================================================================

ACCEPTANCE_DIR="artifacts/acceptance/3.1-2"
mkdir -p "${ACCEPTANCE_DIR}"
REPORT_JSON="${ACCEPTANCE_DIR}/systemd-live.json"
REPORT_MD="${ACCEPTANCE_DIR}/acceptance-report.md"
JOURNAL_LOG="${ACCEPTANCE_DIR}/journal-internet-evidence-monitor.log"

PASS_COUNT=0
FAIL_COUNT=0
NOT_TESTED_COUNT=0

CURRENT_STAGE="INITIALIZATION"
FAILED_STAGE=""

# Per-gate status variables
STATUS_PID1="NOT_TESTED"
STATUS_BUILD="NOT_TESTED"
STATUS_ACCOUNTS="NOT_TESTED"
STATUS_UNIT_INSTALL="NOT_TESTED"
STATUS_TYPE_NOTIFY="NOT_TESTED"
STATUS_PROCESS_IDENTITY="NOT_TESTED"
STATUS_CAPABILITIES="NOT_TESTED"
STATUS_STATE_DIRECTORY="NOT_TESTED"
STATUS_RUNTIME_DIRECTORY="NOT_TESTED"
STATUS_STOP_LIFECYCLE="NOT_TESTED"
STATUS_RESTART_LIFECYCLE="NOT_TESTED"
STATUS_PROTECT_SYSTEM="NOT_TESTED"
STATUS_FAILURE_RESTART="NOT_TESTED"
STATUS_FATAL_EXIT_CODE="NOT_TESTED"
STATUS_START_WITHOUT_NETWORK="NOT_TESTED"

# Environment metadata
COMMIT_SHA="${GITHUB_SHA:-$(git rev-parse HEAD 2>/dev/null || echo 'UNKNOWN')}"
KERNEL_INFO=$(uname -r)
ARCH_INFO=$(uname -m)
DISTRO_NAME="Linux"
DISTRO_VER="Unknown"
if [ -f /etc/os-release ]; then
    DISTRO_NAME=$(grep -E '^ID=' /etc/os-release | cut -d= -f2 | tr -d '"')
    DISTRO_VER=$(grep -E '^VERSION_ID=' /etc/os-release | cut -d= -f2 | tr -d '"')
fi
SYSTEMD_VER=$(systemd --version 2>/dev/null | head -n1 || echo "Unknown")
DOTNET_VER=$(dotnet --version 2>/dev/null || echo "Unknown")

MAIN_PID=0
PROC_USER=""
PROC_GROUP=""
PROC_NUM_UID=""
PROC_NUM_GID=""
PROC_NUM_SUPP_GIDS=""
CAP_EFF=""
CAP_AMB=""
STATE_STAT=""
RUNTIME_STAT=""
EXEC_MAIN_CODE=""
EXEC_MAIN_STATUS=""

record_pass() {
    echo ">> [PASS] $1"
    PASS_COUNT=$((PASS_COUNT + 1))
}

record_fail() {
    echo ">> [FAIL] $1"
    FAIL_COUNT=$((FAIL_COUNT + 1))
    if [ -z "${FAILED_STAGE}" ]; then
        FAILED_STAGE="${CURRENT_STAGE}: $1"
    fi
}

record_not_tested() {
    echo ">> [NOT_TESTED] $1"
    NOT_TESTED_COUNT=$((NOT_TESTED_COUNT + 1))
}

write_evidence_reports() {
    echo "Writing acceptance evidence artifacts to ${ACCEPTANCE_DIR}..."

    # Capture journal logs
    journalctl -u internet-evidence-monitor.service -n 200 --no-pager > "${JOURNAL_LOG}" 2>/dev/null || true

    local final_verdict="PASS"
    if [ "${FAIL_COUNT}" -gt 0 ]; then
        final_verdict="FAIL"
    elif [ "${NOT_TESTED_COUNT}" -gt 0 ]; then
        final_verdict="GATE INCOMPLETE (PARTIAL PASS - NOT ALL GATES TESTABLE)"
    fi

    # Write systemd-live.json with dynamic per-gate status
    cat << EOF > "${REPORT_JSON}"
{
  "acceptanceVersion": "3.1.2-live",
  "timestampUtc": "$(date -u +"%Y-%m-%dT%H:%M:%SZ")",
  "commitSha": "${COMMIT_SHA}",
  "distro": "${DISTRO_NAME}",
  "distroVersion": "${DISTRO_VER}",
  "architecture": "${ARCH_INFO}",
  "kernel": "${KERNEL_INFO}",
  "systemdVersion": "${SYSTEMD_VER}",
  "dotnetVersion": "${DOTNET_VER}",
  "failedStage": "${FAILED_STAGE}",
  "gates": {
    "pid1Strictness": "${STATUS_PID1}",
    "buildAndPublish": "${STATUS_BUILD}",
    "accountProvisioning": "${STATUS_ACCOUNTS}",
    "unitInstallation": "${STATUS_UNIT_INSTALL}",
    "typeNotify": "${STATUS_TYPE_NOTIFY}",
    "processIdentity": "${STATUS_PROCESS_IDENTITY}",
    "capabilities": "${STATUS_CAPABILITIES}",
    "stateDirectory": "${STATUS_STATE_DIRECTORY}",
    "runtimeDirectory": "${STATUS_RUNTIME_DIRECTORY}",
    "stopLifecycle": "${STATUS_STOP_LIFECYCLE}",
    "restartLifecycle": "${STATUS_RESTART_LIFECYCLE}",
    "protectSystemStrict": "${STATUS_PROTECT_SYSTEM}",
    "failureRestart": "${STATUS_FAILURE_RESTART}",
    "fatalExitCode3": "${STATUS_FATAL_EXIT_CODE}",
    "startWithoutNetwork": "${STATUS_START_WITHOUT_NETWORK}"
  },
  "processEvidence": {
    "mainPid": ${MAIN_PID},
    "user": "${PROC_USER}",
    "group": "${PROC_GROUP}",
    "uid": "${PROC_NUM_UID}",
    "gid": "${PROC_NUM_GID}",
    "supplementaryGids": "${PROC_NUM_SUPP_GIDS}",
    "capEff": "${CAP_EFF}",
    "capAmb": "${CAP_AMB}",
    "execMainCode": "${EXEC_MAIN_CODE}",
    "execMainStatus": "${EXEC_MAIN_STATUS}"
  },
  "directoryEvidence": {
    "stateDirectoryStat": "${STATE_STAT}",
    "runtimeDirectoryStat": "${RUNTIME_STAT}"
  },
  "summary": {
    "passCount": ${PASS_COUNT},
    "failCount": ${FAIL_COUNT},
    "notTestedCount": ${NOT_TESTED_COUNT},
    "finalVerdict": "${final_verdict}"
  }
}
EOF

    # Write Markdown summary report
    cat << EOF > "${REPORT_MD}"
# 3.1-2 · Linux Host & systemd Lane C Live Acceptance Report

- **Timestamp**: $(date -u +"%Y-%m-%d %H:%M:%S UTC")
- **Commit**: \`${COMMIT_SHA}\`
- **Distro**: ${DISTRO_NAME} ${DISTRO_VER} (${ARCH_INFO})
- **Kernel**: ${KERNEL_INFO}
- **systemd**: ${SYSTEMD_VER}
- **.NET SDK**: ${DOTNET_VER}
- **Failed Stage**: ${FAILED_STAGE:-"None"}

## Process & Directory Facts
- **MainPID**: ${MAIN_PID}
- **User / UID**: ${PROC_USER} (${PROC_NUM_UID})
- **Group / GID**: ${PROC_GROUP} (${PROC_NUM_GID})
- **Supplementary Groups in Process**: \`${PROC_NUM_SUPP_GIDS}\`
- **Capabilities**: \`CapEff=${CAP_EFF}\`, \`CapAmb=${CAP_AMB}\`
- **StateDirectory Stat**: \`${STATE_STAT}\`
- **RuntimeDirectory Stat**: \`${RUNTIME_STAT}\`

## Gate Status Matrix
| Gate | Status |
|---|---|
| PID 1 Strictness | **${STATUS_PID1}** |
| Build & Publish | **${STATUS_BUILD}** |
| Service Account Provisioning | **${STATUS_ACCOUNTS}** |
| Unit Installation | **${STATUS_UNIT_INSTALL}** |
| Type=notify Readiness | **${STATUS_TYPE_NOTIFY}** |
| Process Identity & Supplementary Groups | **${STATUS_PROCESS_IDENTITY}** |
| Capability Bounding | **${STATUS_CAPABILITIES}** |
| StateDirectory Persistence | **${STATUS_STATE_DIRECTORY}** |
| RuntimeDirectory Ephemeral Lifecycle | **${STATUS_RUNTIME_DIRECTORY}** |
| STOP Persistence & Cleanup | **${STATUS_STOP_LIFECYCLE}** |
| RESTART Persistence & Restore | **${STATUS_RESTART_LIFECYCLE}** |
| ProtectSystem=strict Mount Namespace | **${STATUS_PROTECT_SYSTEM}** |
| Failure / Restart Propagation | **${STATUS_FAILURE_RESTART}** |
| FatalExitCode == 3 Verification | **${STATUS_FATAL_EXIT_CODE}** |
| Start Without Network | **${STATUS_START_WITHOUT_NETWORK}** |

## Summary
- **PASS**: ${PASS_COUNT}
- **FAIL**: ${FAIL_COUNT}
- **NOT_TESTED**: ${NOT_TESTED_COUNT}
- **Final Verdict**: **${final_verdict}**
EOF
}

cleanup_and_exit() {
    local orig_exit=$?
    echo "Running finalization and cleanup (original exit: ${orig_exit})..."
    write_evidence_reports

    # Cleanup systemd service resources safely
    systemctl stop internet-evidence-monitor.service 2>/dev/null || true
    rm -f /etc/systemd/system/internet-evidence-monitor.service 2>/dev/null || true
    rm -rf /etc/systemd/system/internet-evidence-monitor.service.d 2>/dev/null || true
    systemctl daemon-reload 2>/dev/null || true
    rm -rf /usr/lib/internet-evidence-monitor 2>/dev/null || true
    rm -rf /var/lib/internet-evidence-monitor 2>/dev/null || true
    rm -rf /run/internet-evidence-monitor 2>/dev/null || true
    rm -rf /opt/iem-hardening-control-dir 2>/dev/null || true

    # Invariant: If FAIL_COUNT > 0, process MUST exit with non-zero (1)
    if [ "${FAIL_COUNT}" -gt 0 ]; then
        exit 1
    elif [ "${orig_exit}" -ne 0 ]; then
        exit "${orig_exit}"
    fi

    exit 0
}
trap cleanup_and_exit EXIT

echo "=============================================================================="
echo "1. RUNNER ENVIRONMENT & PID 1 STRICT PROOF"
echo "=============================================================================="
CURRENT_STAGE="STAGE_1_PID1"

PID1_COMM=$(ps -p 1 -o comm= 2>/dev/null | tr -d ' ' || echo "Unknown")
echo "PID 1 comm: '${PID1_COMM}'"

if [ "${PID1_COMM}" = "systemd" ]; then
    STATUS_PID1="PASS"
    record_pass "PID 1 is strictly systemd"
else
    STATUS_PID1="FAIL"
    record_fail "PID 1 is not systemd (Detected: '${PID1_COMM}')"
    exit 2
fi

echo "=============================================================================="
echo "2. BUILD AND PUBLISH IEM.Service.Linux"
echo "=============================================================================="
CURRENT_STAGE="STAGE_2_BUILD"

INSTALL_DIR="/usr/lib/internet-evidence-monitor"
mkdir -p "${INSTALL_DIR}"

if dotnet publish src/IEM.Service.Linux/IEM.Service.Linux.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o "${INSTALL_DIR}"; then
    chmod 0755 "${INSTALL_DIR}/IEM.Service.Linux"
    STATUS_BUILD="PASS"
    record_pass "Published binary to ${INSTALL_DIR}/IEM.Service.Linux"
else
    STATUS_BUILD="FAIL"
    record_fail "Failed to build/publish IEM.Service.Linux"
    exit 1
fi

echo "=============================================================================="
echo "3. SERVICE ACCOUNT PROVISIONING"
echo "=============================================================================="
CURRENT_STAGE="STAGE_3_ACCOUNTS"

getent group iem-users >/dev/null 2>&1 || groupadd -r iem-users
getent group iem >/dev/null 2>&1 || groupadd -r iem
getent passwd iem >/dev/null 2>&1 || useradd -r -g iem -G iem-users -d /var/lib/internet-evidence-monitor -s /usr/sbin/nologin iem

EXPECTED_UID=$(id -u iem)
EXPECTED_GID=$(id -g iem)
EXPECTED_SUPP_GID=$(getent group iem-users | cut -d: -f3)

echo "Provisioned user iem (UID: ${EXPECTED_UID}, GID: ${EXPECTED_GID}, Supp GID: ${EXPECTED_SUPP_GID})"
STATUS_ACCOUNTS="PASS"
record_pass "Service accounts provisioned (iem:iem, supplementary iem-users)"

echo "=============================================================================="
echo "4. INSTALL SYSTEMD UNIT"
echo "=============================================================================="
CURRENT_STAGE="STAGE_4_UNIT_INSTALL"

UNIT_SRC="packaging/systemd/internet-evidence-monitor.service"
UNIT_DST="/etc/systemd/system/internet-evidence-monitor.service"
cp "${UNIT_SRC}" "${UNIT_DST}"
chmod 0644 "${UNIT_DST}"
systemctl daemon-reload

STATUS_UNIT_INSTALL="PASS"
record_pass "Canonical unit file installed and daemon reloaded"

echo "=============================================================================="
echo "4.5 DIRECT DIAGNOSTIC EXECUTION AS USER iem"
echo "=============================================================================="
mkdir -p /run/internet-evidence-monitor /var/lib/internet-evidence-monitor
chown -R iem:iem /run/internet-evidence-monitor /var/lib/internet-evidence-monitor
chmod 0750 /run/internet-evidence-monitor
chmod 0700 /var/lib/internet-evidence-monitor
sudo -u iem NOTIFY_SOCKET=/tmp/test_notify.sock timeout 2s /usr/lib/internet-evidence-monitor/IEM.Service.Linux || true

echo "=============================================================================="
echo "5. START SERVICE AND TYPE=notify LIVE GATE"
echo "=============================================================================="
CURRENT_STAGE="STAGE_5_TYPE_NOTIFY"

systemctl start internet-evidence-monitor.service

# Bounded poll for active state (up to 15s)
for i in {1..30}; do
    ACTIVE_STATE=$(systemctl show -p ActiveState --value internet-evidence-monitor.service 2>/dev/null || echo "")
    SUB_STATE=$(systemctl show -p SubState --value internet-evidence-monitor.service 2>/dev/null || echo "")
    if [ "${ACTIVE_STATE}" = "active" ] && [ "${SUB_STATE}" = "running" ]; then
        break
    fi
    sleep 0.5
done

UNIT_TYPE=$(systemctl show -p Type --value internet-evidence-monitor.service 2>/dev/null || echo "")
ACTIVE_STATE=$(systemctl show -p ActiveState --value internet-evidence-monitor.service 2>/dev/null || echo "")
SUB_STATE=$(systemctl show -p SubState --value internet-evidence-monitor.service 2>/dev/null || echo "")
UNIT_RESULT=$(systemctl show -p Result --value internet-evidence-monitor.service 2>/dev/null || echo "")
MAIN_PID=$(systemctl show -p MainPID --value internet-evidence-monitor.service 2>/dev/null || echo "0")

echo "Unit Type: ${UNIT_TYPE}"
echo "ActiveState: ${ACTIVE_STATE}, SubState: ${SUB_STATE}, Result: ${UNIT_RESULT}, MainPID: ${MAIN_PID}"

# Strict assertion: Type MUST be notify, Result MUST be success, State active/running, MainPID > 1
if [ "${UNIT_TYPE}" = "notify" ] && [ "${ACTIVE_STATE}" = "active" ] && [ "${SUB_STATE}" = "running" ] && [ "${MAIN_PID}" -gt 1 ] && [ "${UNIT_RESULT}" = "success" ]; then
    STATUS_TYPE_NOTIFY="PASS"
    record_pass "Service reached ActiveState=active, SubState=running via Type=notify with Result=success"
else
    STATUS_TYPE_NOTIFY="FAIL"
    record_fail "Service failed Type=notify readiness (Type=${UNIT_TYPE}, Active=${ACTIVE_STATE}, Sub=${SUB_STATE}, Result=${UNIT_RESULT})"
    exit 1
fi

echo "=============================================================================="
echo "6. PROCESS IDENTITY & SUPPLEMENTARY GROUPS LIVE ASSERTIONS"
echo "=============================================================================="
CURRENT_STAGE="STAGE_6_PROCESS_IDENTITY"

PROC_USER=$(ps -o user= -p "${MAIN_PID}" 2>/dev/null | tr -d ' ' || echo "")
PROC_GROUP=$(ps -o group= -p "${MAIN_PID}" 2>/dev/null | tr -d ' ' || echo "")

# Read exact numeric UIDs/GIDs and Groups line from /proc/PID/status
PROC_NUM_UID=$(grep '^Uid:' "/proc/${MAIN_PID}/status" | awk '{print $2}')
PROC_NUM_GID=$(grep '^Gid:' "/proc/${MAIN_PID}/status" | awk '{print $2}')
PROC_NUM_SUPP_GIDS=$(grep '^Groups:' "/proc/${MAIN_PID}/status" | cut -d: -f2- | tr -d '\r\n')

echo "Process: User='${PROC_USER}' (UID: ${PROC_NUM_UID}), Primary GID: ${PROC_NUM_GID}"
echo "Process /proc/${MAIN_PID}/status Groups line: '${PROC_NUM_SUPP_GIDS}'"

# Strict assertions
IDENTITY_OK=true

if [ "${PROC_NUM_UID}" = "0" ] || [ "${PROC_USER}" = "root" ]; then
    echo "ERROR: Process is running as root (UID 0)!"
    IDENTITY_OK=false
fi

if [ "${PROC_NUM_UID}" != "${EXPECTED_UID}" ] || [ "${PROC_NUM_GID}" != "${EXPECTED_GID}" ]; then
    echo "ERROR: Process UID/GID mismatch (Got ${PROC_NUM_UID}:${PROC_NUM_GID}, Expected ${EXPECTED_UID}:${EXPECTED_GID})!"
    IDENTITY_OK=false
fi

# Hard assertion: Groups in /proc/PID/status MUST contain numeric GID of iem-users
if ! echo " ${PROC_NUM_SUPP_GIDS} " | grep -q " ${EXPECTED_SUPP_GID} "; then
    echo "ERROR: Process Groups list ('${PROC_NUM_SUPP_GIDS}') does NOT contain iem-users GID (${EXPECTED_SUPP_GID})!"
    IDENTITY_OK=false
fi

if [ "${IDENTITY_OK}" = "true" ]; then
    STATUS_PROCESS_IDENTITY="PASS"
    record_pass "Process identity strictly verified: UID=${PROC_NUM_UID}, GID=${PROC_NUM_GID}, Groups contains iem-users (${EXPECTED_SUPP_GID})"
else
    STATUS_PROCESS_IDENTITY="FAIL"
    record_fail "Process identity check failed"
    exit 1
fi

echo "=============================================================================="
echo "7. CAPABILITY BOUNDING LIVE ASSERTIONS"
echo "=============================================================================="
CURRENT_STAGE="STAGE_7_CAPABILITIES"

CAP_EFF=$(grep '^CapEff:' "/proc/${MAIN_PID}/status" | awk '{print $2}')
CAP_AMB=$(grep '^CapAmb:' "/proc/${MAIN_PID}/status" | awk '{print $2}')
echo "CapEff=${CAP_EFF}, CapAmb=${CAP_AMB}"

CAPS_OK=true
if [ "${CAP_AMB}" != "0000000000000000" ]; then
    echo "ERROR: Non-zero ambient capabilities: ${CAP_AMB}"
    CAPS_OK=false
fi

if [ "${CAP_EFF}" != "0000000000000000" ]; then
    CAP_HEX=$((16#${CAP_EFF}))
    CAP_NET_ADMIN_MASK=$((1 << 12))
    CAP_NET_RAW_MASK=$((1 << 13))
    if [ $((CAP_HEX & CAP_NET_ADMIN_MASK)) -ne 0 ] || [ $((CAP_HEX & CAP_NET_RAW_MASK)) -ne 0 ]; then
        echo "ERROR: Forbidden CAP_NET_ADMIN/RAW in CapEff: ${CAP_EFF}"
        CAPS_OK=false
    fi
fi

if [ "${CAPS_OK}" = "true" ]; then
    STATUS_CAPABILITIES="PASS"
    record_pass "Capability bounding verified (CapAmb=0, no CAP_NET_ADMIN/RAW)"
else
    STATUS_CAPABILITIES="FAIL"
    record_fail "Capability bounding check failed"
    exit 1
fi

echo "=============================================================================="
echo "8. STATEDIRECTORY LIVE ACCEPTANCE"
echo "=============================================================================="
CURRENT_STAGE="STAGE_8_STATE_DIRECTORY"

STATE_DIR="/var/lib/internet-evidence-monitor"
if [ ! -d "${STATE_DIR}" ]; then
    STATUS_STATE_DIRECTORY="FAIL"
    record_fail "StateDirectory '${STATE_DIR}' does not exist"
    exit 1
fi

STATE_STAT=$(stat -c "%U:%G %a" "${STATE_DIR}")
echo "StateDirectory stat: ${STATE_STAT}"

if [ "${STATE_STAT}" = "iem:iem 700" ]; then
    STATUS_STATE_DIRECTORY="PASS"
    record_pass "StateDirectory ownership/mode is 'iem:iem 700'"
else
    STATUS_STATE_DIRECTORY="FAIL"
    record_fail "StateDirectory stat expected 'iem:iem 700', got '${STATE_STAT}'"
    exit 1
fi

STATE_SENTINEL="${STATE_DIR}/state-survival-sentinel"
echo "persistent-state-token-$(date +%s)" > "${STATE_SENTINEL}"
chown iem:iem "${STATE_SENTINEL}"
chmod 0600 "${STATE_SENTINEL}"

echo "=============================================================================="
echo "9. RUNTIMEDIRECTORY LIVE ACCEPTANCE"
echo "=============================================================================="
CURRENT_STAGE="STAGE_9_RUNTIME_DIRECTORY"

RUNTIME_DIR="/run/internet-evidence-monitor"
if [ ! -d "${RUNTIME_DIR}" ]; then
    STATUS_RUNTIME_DIRECTORY="FAIL"
    record_fail "RuntimeDirectory '${RUNTIME_DIR}' does not exist"
    exit 1
fi

RUNTIME_STAT=$(stat -c "%U:%G %a" "${RUNTIME_DIR}")
echo "RuntimeDirectory stat: ${RUNTIME_STAT}"

if [ "${RUNTIME_STAT}" = "iem:iem-users 750" ] && [ ! -e "${RUNTIME_DIR}/control.sock" ]; then
    STATUS_RUNTIME_DIRECTORY="PASS"
    record_pass "RuntimeDirectory is 'iem:iem-users 750' and control.sock is absent"
else
    STATUS_RUNTIME_DIRECTORY="FAIL"
    record_fail "RuntimeDirectory check failed (Stat: ${RUNTIME_STAT})"
    exit 1
fi

EPHEMERAL_SENTINEL="${RUNTIME_DIR}/ephemeral-sentinel-1"
touch "${EPHEMERAL_SENTINEL}"

echo "=============================================================================="
echo "10. STOP LIFECYCLE ACCEPTANCE"
echo "=============================================================================="
CURRENT_STAGE="STAGE_10_STOP_LIFECYCLE"

systemctl stop internet-evidence-monitor.service

if [ -d "${STATE_DIR}" ] && [ -f "${STATE_SENTINEL}" ] && [ ! -d "${RUNTIME_DIR}" ]; then
    STATUS_STOP_LIFECYCLE="PASS"
    record_pass "STOP lifecycle: StateDirectory persisted, RuntimeDirectory cleaned up"
else
    STATUS_STOP_LIFECYCLE="FAIL"
    record_fail "STOP lifecycle failed (StateExists: $([ -d "${STATE_DIR}" ] && echo true || echo false), RuntimeExists: $([ -d "${RUNTIME_DIR}" ] && echo true || echo false))"
fi

echo "=============================================================================="
echo "11. RESTART LIFECYCLE ACCEPTANCE"
echo "=============================================================================="
CURRENT_STAGE="STAGE_11_RESTART_LIFECYCLE"

systemctl start internet-evidence-monitor.service

EPHEMERAL_SENTINEL_2="${RUNTIME_DIR}/ephemeral-sentinel-2"
touch "${EPHEMERAL_SENTINEL_2}"

systemctl restart internet-evidence-monitor.service

RUNTIME_STAT_RESTART=$(stat -c "%U:%G %a" "${RUNTIME_DIR}" 2>/dev/null || echo "")

if [ -f "${STATE_SENTINEL}" ] && [ ! -f "${EPHEMERAL_SENTINEL}" ] && [ ! -f "${EPHEMERAL_SENTINEL_2}" ] && [ "${RUNTIME_STAT_RESTART}" = "iem:iem-users 750" ]; then
    STATUS_RESTART_LIFECYCLE="PASS"
    record_pass "RESTART lifecycle: State persisted, old sentinels wiped, fresh 0750 directory provided"
else
    STATUS_RESTART_LIFECYCLE="FAIL"
    record_fail "RESTART lifecycle failed (Stat: ${RUNTIME_STAT_RESTART})"
fi

echo "=============================================================================="
echo "12. PROTECTSYSTEM=STRICT CAUSAL CONTROL TEST (3-POINT PROOF)"
echo "=============================================================================="
CURRENT_STAGE="STAGE_12_PROTECT_SYSTEM"

# Set up an outside test directory that is DAC-writable for iem
CONTROL_OUTSIDE_DIR="/opt/iem-hardening-control-dir"
mkdir -p "${CONTROL_OUTSIDE_DIR}"
chown iem:iem "${CONTROL_OUTSIDE_DIR}"
chmod 0750 "${CONTROL_OUTSIDE_DIR}"

UNHARDENED_PID=$(systemctl show -p MainPID --value internet-evidence-monitor.service 2>/dev/null || echo "0")

if command -v nsenter >/dev/null 2>&1; then
    # Fact 1: Outside write BEFORE hardening must SUCCEED (Proves DAC allows write)
    FACT_1_DAC_WRITABLE=false
    if nsenter -t "${UNHARDENED_PID}" -m -- su -s /bin/bash iem -c "touch ${CONTROL_OUTSIDE_DIR}/control-test" 2>/dev/null; then
        FACT_1_DAC_WRITABLE=true
        rm -f "${CONTROL_OUTSIDE_DIR}/control-test" 2>/dev/null || true
    fi
    echo "Fact 1: Outside write before hardening succeeded: ${FACT_1_DAC_WRITABLE}"

    # Apply ProtectSystem=strict drop-in
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

    HARDENED_PID=$(systemctl show -p MainPID --value internet-evidence-monitor.service 2>/dev/null || echo "0")
    PROTECT_SYS_VAL=$(systemctl show -p ProtectSystem --value internet-evidence-monitor.service 2>/dev/null || echo "")

    # Fact 2: Under ProtectSystem=strict, SAME outside write must be DENIED (Read-only file system)
    FACT_2_OUTSIDE_DENIED=false
    if ! nsenter -t "${HARDENED_PID}" -m -- su -s /bin/bash iem -c "touch ${CONTROL_OUTSIDE_DIR}/control-test" 2>/dev/null; then
        FACT_2_OUTSIDE_DENIED=true
    fi
    echo "Fact 2: Outside write under ProtectSystem=strict denied: ${FACT_2_OUTSIDE_DENIED}"

    # Fact 3: Under ProtectSystem=strict, StateDirectory write must SUCCEED
    FACT_3_STATE_WRITABLE=false
    if nsenter -t "${HARDENED_PID}" -m -- su -s /bin/bash iem -c "touch /var/lib/internet-evidence-monitor/ns-write-test" 2>/dev/null; then
        FACT_3_STATE_WRITABLE=true
        rm -f /var/lib/internet-evidence-monitor/ns-write-test 2>/dev/null || true
    fi
    echo "Fact 3: StateDirectory write under ProtectSystem=strict succeeded: ${FACT_3_STATE_WRITABLE}"

    if [ "${FACT_1_DAC_WRITABLE}" = "true" ] && [ "${FACT_2_OUTSIDE_DENIED}" = "true" ] && [ "${FACT_3_STATE_WRITABLE}" = "true" ]; then
        STATUS_PROTECT_SYSTEM="PASS"
        record_pass "ProtectSystem=strict causal 3-point proof verified (DAC allowed -> Namespace denied -> State allowed)"
    else
        STATUS_PROTECT_SYSTEM="FAIL"
        record_fail "ProtectSystem=strict causal proof failed (Fact1: ${FACT_1_DAC_WRITABLE}, Fact2: ${FACT_2_OUTSIDE_DENIED}, Fact3: ${FACT_3_STATE_WRITABLE})"
    fi

    # Clean up hardening drop-in and restore service
    rm -rf "${DROPIN_DIR}"
    systemctl daemon-reload
    systemctl restart internet-evidence-monitor.service
else
    STATUS_PROTECT_SYSTEM="NOT_TESTED"
    record_not_tested "ProtectSystem=strict namespace injection (nsenter command not available on runner)"
fi

rm -rf "${CONTROL_OUTSIDE_DIR}" 2>/dev/null || true

echo "=============================================================================="
echo "13. FATAL EXIT CODE == 3 STRICT VERIFICATION"
echo "=============================================================================="
CURRENT_STAGE="STAGE_13_FATAL_EXIT_CODE"

# Stop service temporarily to test standalone binary failure condition
systemctl stop internet-evidence-monitor.service

# Real failure condition: Create /run/internet-evidence-monitor as a regular FILE (or symlink)
# so LinuxRuntimeDirectoryPreparer.Prepare fails closed, and Program.cs returns MonitorWorker.FatalExitCode (3)
rm -rf /run/internet-evidence-monitor
touch /run/internet-evidence-monitor # Invalid: file instead of directory

set +e
"${INSTALL_DIR}/IEM.Service.Linux" 2>/dev/null
CLI_EXIT=$?
set -e

# Cleanup invalid path
rm -f /run/internet-evidence-monitor

echo "Pre-flight failure CLI test exit code: ${CLI_EXIT}"

# Strict assertion: MUST BE EXACTLY 3
if [ "${CLI_EXIT}" -eq 3 ]; then
    STATUS_FATAL_EXIT_CODE="PASS"
    record_pass "Fatal pre-flight startup failure strictly returned exit code 3 (FatalExitCode == 3)"
else
    STATUS_FATAL_EXIT_CODE="FAIL"
    record_fail "Fatal startup exit code was not 3 (Got: ${CLI_EXIT})"
fi

# Restart service for next tests
systemctl start internet-evidence-monitor.service

echo "=============================================================================="
echo "14. SYSTEMD RESTART=ON-FAILURE LIVE PROPAGATION TEST"
echo "=============================================================================="
CURRENT_STAGE="STAGE_14_FAILURE_RESTART"

CURRENT_PID=$(systemctl show -p MainPID --value internet-evidence-monitor.service 2>/dev/null || echo "0")
NRESTARTS_BEFORE=$(systemctl show -p NRestarts --value internet-evidence-monitor.service 2>/dev/null || echo "0")
echo "Before failure kill: PID=${CURRENT_PID}, NRestarts=${NRESTARTS_BEFORE}"

kill -9 "${CURRENT_PID}" 2>/dev/null || true

RESTART_OK=false
NEW_PID=0
for i in {1..30}; do
    sleep 1
    NRESTARTS_AFTER=$(systemctl show -p NRestarts --value internet-evidence-monitor.service 2>/dev/null || echo "0")
    ACTIVE_AFTER=$(systemctl show -p ActiveState --value internet-evidence-monitor.service 2>/dev/null || echo "")
    NEW_PID=$(systemctl show -p MainPID --value internet-evidence-monitor.service 2>/dev/null || echo "0")

    if [ "${ACTIVE_AFTER}" = "active" ] && [ "${NEW_PID}" -gt 1 ] && [ "${NEW_PID}" -ne "${CURRENT_PID}" ]; then
        RESTART_OK=true
        echo "Restart succeeded after ${i}s: NewPID=${NEW_PID}, NRestarts=${NRESTARTS_AFTER}"
        break
    fi
done

EXEC_MAIN_CODE=$(systemctl show -p ExecMainCode --value internet-evidence-monitor.service 2>/dev/null || echo "")
EXEC_MAIN_STATUS=$(systemctl show -p ExecMainStatus --value internet-evidence-monitor.service 2>/dev/null || echo "")

if [ "${RESTART_OK}" = "true" ]; then
    STATUS_FAILURE_RESTART="PASS"
    record_pass "systemd Restart=on-failure verified with bounded polling (ExecMainCode=${EXEC_MAIN_CODE}, ExecMainStatus=${EXEC_MAIN_STATUS}, NewPID=${NEW_PID})"
else
    STATUS_FAILURE_RESTART="FAIL"
    record_fail "systemd Restart=on-failure timed out after 20s (NRestarts: before=${NRESTARTS_BEFORE}, after=${NRESTARTS_AFTER:-0})"
fi

echo "=============================================================================="
echo "15. START WITHOUT NETWORK (TRUTHFUL EVALUATION)"
echo "=============================================================================="
CURRENT_STAGE="STAGE_15_NETWORK_OFF"

STATUS_START_WITHOUT_NETWORK="NOT_TESTED"
record_not_tested "Start without network live network-off boot (Shared GitHub Actions VM runner cannot drop primary NIC without killing CI connection; static absence of network-online.target is verified deterministically in IEM.Core.Tests)"

echo "=============================================================================="
echo "LANE C ACCEPTANCE EXECUTION FINISHED"
echo "=============================================================================="
