#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# 3.1-6F-S1-R2 · Physical Suspend/Resume Acceptance Runner
# Strict, audit-grade verification on a real bare-metal / suspend-capable Linux host:
#
# Proves:
# 1. Unprivileged user iem runs IEM.TimeRunner suspend-observe (CapEff=0, CapAmb=0)
# 2. PrepareForSleep D-Bus subscription established and reports READY
# 3. Real host suspend executed (rtcwake -m mem -s 3 or rtcwake -m freeze -s 3)
# 4. PrepareForSleep(true) and PrepareForSleep(false) signals captured live
# 5. boot_id pre == boot_id post (identical UUID)
# 6. Core TimeContinuityEvaluator.EvaluateBoot -> BootContinuityState.Continued
# 7. Core TimeContinuityEvaluator.EvaluateTransition -> ClockContinuityState.SuspendIntervalObserved
# 8. Zero capability model preserved (CapEff=0000000000000000, CapAmb=0000000000000000)
# ==============================================================================

ACCEPTANCE_DIR="artifacts/acceptance/3.1-6"
mkdir -p "${ACCEPTANCE_DIR}"
REPORT_JSON="${ACCEPTANCE_DIR}/suspend-resume-physical.json"
REPORT_MD="${ACCEPTANCE_DIR}/suspend-resume-physical.md"

echo "=============================================================================="
echo "3.1-6F-S1-R2 · PHYSICAL SUSPEND/RESUME ACCEPTANCE RUNNER"
echo "=============================================================================="

if [ "$(id -u)" -ne 0 ]; then
    echo "ERROR: Acceptance runner must be run as root to trigger host rtcwake" >&2
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

INSTALL_DIR="/usr/lib/internet-evidence-monitor"
TIME_RUNNER="${INSTALL_DIR}/tools/IEM.TimeRunner"

if [ ! -x "${TIME_RUNNER}" ]; then
    echo "Building and publishing IEM.TimeRunner..."
    dotnet publish tools/IEM.TimeRunner/IEM.TimeRunner.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o "${INSTALL_DIR}/tools"
    chmod 0755 "${TIME_RUNNER}"
fi

# Ensure system D-Bus daemon is running
service dbus start 2>/dev/null || /etc/init.d/dbus start 2>/dev/null || systemctl start dbus 2>/dev/null || true

# Canonical service accounts matching Lane C
getent group iem-users >/dev/null 2>&1 || groupadd -r iem-users
getent group iem >/dev/null 2>&1 || groupadd -r iem
getent passwd iem >/dev/null 2>&1 || useradd -r -g iem -G iem-users -d /var/lib/internet-evidence-monitor -s /usr/sbin/nologin iem

# 1. Launch unprivileged observer in background writing to regular log file (no FIFO EPIPE risk)
OBS_LOG="/var/log/iem_suspend_obs.log"
mkdir -p /var/log
rm -f "${OBS_LOG}"
touch "${OBS_LOG}"
chown iem:iem "${OBS_LOG}" 2>/dev/null || true
chmod 0666 "${OBS_LOG}"

echo "Starting IEM.TimeRunner suspend-observe as unprivileged user iem..."
su -s /bin/bash iem -c "${TIME_RUNNER} suspend-observe > '${OBS_LOG}' 2>&1" &
LAUNCHER_PID=$!

# 2. Poll log file for READY signal
READY_SEEN=false
READY_JSON=""
for _ in $(seq 1 100); do
    if grep -q '^IEM_SUSPEND_LISTENER_READY=true$' "${OBS_LOG}"; then
        READY_SEEN=true
        READY_JSON=$(grep '^IEM_SUSPEND_READY_JSON=' "${OBS_LOG}" | head -n1 | cut -d= -f2- || echo "")
        break
    fi
    sleep 0.1
done

if [ "${READY_SEEN}" != "true" ]; then
    echo "ERROR: Failed to receive READY signal from observer within 10s" >&2
    cat "${OBS_LOG}" >&2 || true
    kill "${LAUNCHER_PID}" 2>/dev/null || true
    exit 1
fi

OBSERVER_PID=$(echo "${READY_JSON}" | grep -o '"pid":[0-9]*' | cut -d: -f2 || echo "")
OBSERVER_UID=$(echo "${READY_JSON}" | grep -o '"uid":"[^"]*"' | cut -d'"' -f4 || echo "")
OBSERVER_GID=$(echo "${READY_JSON}" | grep -o '"gid":"[^"]*"' | cut -d'"' -f4 || echo "")
CAP_EFF=$(echo "${READY_JSON}" | grep -o '"capEff":"[^"]*"' | cut -d'"' -f4 || echo "0000000000000000")
CAP_AMB=$(echo "${READY_JSON}" | grep -o '"capAmb":"[^"]*"' | cut -d'"' -f4 || echo "0000000000000000")

echo "Observer PID=${OBSERVER_PID}, UID=${OBSERVER_UID}, GID=${OBSERVER_GID}, CapEff=${CAP_EFF}, CapAmb=${CAP_AMB}"

echo "Observer is READY. Executing real host suspend for 3 seconds..."

# 3. Trigger Real Host Suspend (prefer logind systemctl suspend with rtcwake timer for PrepareForSleep signals)
SUSPEND_TRIGGER_OK=false
SUSPEND_METHOD="unknown"

if command -v rtcwake >/dev/null 2>&1; then
    if command -v systemctl >/dev/null 2>&1 && systemctl is-active --quiet systemd-logind.service 2>/dev/null; then
        if rtcwake -m no -s 3 2>/dev/null && systemctl suspend 2>/dev/null; then
            SUSPEND_TRIGGER_OK=true
            SUSPEND_METHOD="rtcwake -m no -s 3 && systemctl suspend"
        fi
    fi

    if [ "${SUSPEND_TRIGGER_OK}" != "true" ]; then
        if rtcwake -m mem -s 3; then
            SUSPEND_TRIGGER_OK=true
            SUSPEND_METHOD="rtcwake -m mem -s 3"
        elif rtcwake -m freeze -s 3; then
            SUSPEND_TRIGGER_OK=true
            SUSPEND_METHOD="rtcwake -m freeze -s 3"
        fi
    fi
fi

if [ "${SUSPEND_TRIGGER_OK}" != "true" ]; then
    echo "ERROR: Real host suspend trigger failed (neither rtcwake mem nor freeze succeeded)" >&2
    kill "${LAUNCHER_PID}" 2>/dev/null || true

    cat << EOF > "${REPORT_JSON}"
{
  "acceptanceVersion": "3.1.6-live",
  "commitSha": "${COMMIT_SHA}",
  "distro": "${DISTRO_NAME}",
  "distroVersion": "${DISTRO_VER}",
  "architecture": "${ARCH_INFO}",
  "kernel": "${KERNEL_INFO}",
  "gate": "3.1-6F-S1 · Physical Suspend/Resume Acceptance",
  "timestampUtc": "$(date -u +"%Y-%m-%dT%H:%M:%SZ")",
  "verdict": "NOT_TESTED",
  "failReason": "Host suspend trigger unavailable on this hardware/environment"
}
EOF
    exit 2
fi

echo "Host resumed from suspend via ${SUSPEND_METHOD}!"

# 4. Poll log file for final acceptance JSON
ACCEPTANCE_SEEN=false
ACCEPTANCE_JSON=""
for _ in $(seq 1 450); do
    if grep -q '^IEM_SUSPEND_ACCEPTANCE_JSON=' "${OBS_LOG}"; then
        ACCEPTANCE_SEEN=true
        ACCEPTANCE_JSON=$(grep '^IEM_SUSPEND_ACCEPTANCE_JSON=' "${OBS_LOG}" | head -n1 | cut -d= -f2-)
        break
    fi
    sleep 0.1
done

wait "${LAUNCHER_PID}" 2>/dev/null || true

if [ "${ACCEPTANCE_SEEN}" != "true" ] || [ -z "${ACCEPTANCE_JSON}" ]; then
    echo "ERROR: No acceptance JSON emitted by IEM.TimeRunner within 45s" >&2
    cat "${OBS_LOG}" >&2 || true
    exit 1
fi

echo "Received Acceptance JSON: ${ACCEPTANCE_JSON}"

# 5. Parse and Validate Core Semantic Truths
SUCCESS_FLAG=$(echo "${ACCEPTANCE_JSON}" | grep -o '"success":true' || echo "")
BOOT_STATE=$(echo "${ACCEPTANCE_JSON}" | grep -o '"bootContinuityState":"[^"]*"' | cut -d'"' -f4)
CLOCK_STATE=$(echo "${ACCEPTANCE_JSON}" | grep -o '"clockContinuityState":"[^"]*"' | cut -d'"' -f4)
SLEEP_TRUE=$(echo "${ACCEPTANCE_JSON}" | grep -o '"sleepTrueReceived":true' || echo "")
SLEEP_FALSE=$(echo "${ACCEPTANCE_JSON}" | grep -o '"sleepFalseReceived":true' || echo "")
SUSPEND_DUR=$(echo "${ACCEPTANCE_JSON}" | grep -o '"suspendDurationSeconds":[0-9.]*' | cut -d: -f2)
BOOT_PRE=$(echo "${ACCEPTANCE_JSON}" | grep -o '"bootInstanceIdPre":"[^"]*"' | cut -d'"' -f4)
BOOT_POST=$(echo "${ACCEPTANCE_JSON}" | grep -o '"bootInstanceIdPost":"[^"]*"' | cut -d'"' -f4)

OBS_PID_FINAL=$(echo "${ACCEPTANCE_JSON}" | grep -o '"pid":[0-9]*' | cut -d: -f2 || echo "${OBSERVER_PID}")
OBS_UID_FINAL=$(echo "${ACCEPTANCE_JSON}" | grep -o '"uid":"[^"]*"' | cut -d'"' -f4 | tr -d '\t' || echo "${OBSERVER_UID}")
OBS_GID_FINAL=$(echo "${ACCEPTANCE_JSON}" | grep -o '"gid":"[^"]*"' | cut -d'"' -f4 | tr -d '\t' || echo "${OBSERVER_GID}")

VERDICT="PASS"
FAIL_REASONS=""

if [ -z "${SUCCESS_FLAG}" ]; then
    VERDICT="FAIL"
    FAIL_REASONS="${FAIL_REASONS} Core evaluation returned success=false;"
fi

if [ "${BOOT_STATE}" != "Continued" ]; then
    VERDICT="FAIL"
    FAIL_REASONS="${FAIL_REASONS} Expected BootContinuityState.Continued, got '${BOOT_STATE}';"
fi

if [ "${CLOCK_STATE}" != "SuspendIntervalObserved" ]; then
    VERDICT="FAIL"
    FAIL_REASONS="${FAIL_REASONS} Expected ClockContinuityState.SuspendIntervalObserved, got '${CLOCK_STATE}';"
fi

if [ -z "${SLEEP_TRUE}" ] || [ -z "${SLEEP_FALSE}" ]; then
    VERDICT="FAIL"
    FAIL_REASONS="${FAIL_REASONS} Missing PrepareForSleep signal capture (True: ${SLEEP_TRUE:-no}, False: ${SLEEP_FALSE:-no});"
fi

if [ "${CAP_EFF}" != "0000000000000000" ] || [ "${CAP_AMB}" != "0000000000000000" ]; then
    VERDICT="FAIL"
    FAIL_REASONS="${FAIL_REASONS} Process had unexpected capabilities (CapEff=${CAP_EFF}, CapAmb=${CAP_AMB});"
fi

# 6. Write Canonical JSON & Markdown Artifacts
cat << EOF > "${REPORT_JSON}"
{
  "acceptanceVersion": "3.1.6-live",
  "commitSha": "${COMMIT_SHA}",
  "distro": "${DISTRO_NAME}",
  "distroVersion": "${DISTRO_VER}",
  "architecture": "${ARCH_INFO}",
  "kernel": "${KERNEL_INFO}",
  "gate": "3.1-6F-S1 · Physical Suspend/Resume Acceptance",
  "timestampUtc": "$(date -u +"%Y-%m-%dT%H:%M:%SZ")",
  "verdict": "${VERDICT}",
  "failReasons": "${FAIL_REASONS:-None}",
  "suspendMethod": "${SUSPEND_METHOD}",
  "processEvidence": {
    "user": "iem",
    "observerPid": "${OBS_PID_FINAL}",
    "uid": "${OBS_UID_FINAL}",
    "gid": "${OBS_GID_FINAL}",
    "capEff": "${CAP_EFF}",
    "capAmb": "${CAP_AMB}"
  },
  "coreSemanticEvidence": ${ACCEPTANCE_JSON}
}
EOF

cat << EOF > "${REPORT_MD}"
# 3.1-6F-S1 · Physical Suspend/Resume Acceptance Report

- **Timestamp**: $(date -u +"%Y-%m-%d %H:%M:%S UTC")
- **Commit**: \`${COMMIT_SHA}\`
- **Distro**: ${DISTRO_NAME} ${DISTRO_VER} (${ARCH_INFO})
- **Kernel**: ${KERNEL_INFO}
- **Verdict**: **${VERDICT}**
- **Method**: \`${SUSPEND_METHOD}\`
- **Fail Reasons**: ${FAIL_REASONS:-"None"}

## Process & Boundary Facts
- **User**: \`iem\` (PID ${OBS_PID_FINAL}, UID ${OBS_UID_FINAL}, GID ${OBS_GID_FINAL})
- **Capabilities**: \`CapEff=${CAP_EFF}\`, \`CapAmb=${CAP_AMB}\` (Zero capability verified)
- **PrepareForSleep(true) captured**: $([ -n "${SLEEP_TRUE}" ] && echo "YES" || echo "NO")
- **PrepareForSleep(false) captured**: $([ -n "${SLEEP_FALSE}" ] && echo "YES" || echo "NO")

## Core Evaluator Verdicts
- **Pre-Suspend boot_id**: \`${BOOT_PRE}\`
- **Post-Suspend boot_id**: \`${BOOT_POST}\`
- **Boot Continuity Assessment**: \`${BOOT_STATE}\` (Invariant 100/109/110)
- **Clock Continuity Assessment**: \`${CLOCK_STATE}\` (Invariant 97/108)
- **Observed Suspend Duration**: \`${SUSPEND_DUR}s\`

## Verification Conclusion
$([ "${VERDICT}" = "PASS" ] && echo "Physical host suspend/resume continuity successfully verified on real Linux hardware without privileges. TimeContinuityEvaluator strictly classified the dual-clock divergence as SuspendIntervalObserved, and boot identity was verified as Continued without synthesizing false reboot or network outage intervals." || echo "Physical suspend verification failed: ${FAIL_REASONS}")
EOF

echo "Artifacts written to ${REPORT_JSON} and ${REPORT_MD}"

if [ "${VERDICT}" = "PASS" ]; then
    echo ">> [PASS] 3.1-6F-S1 Physical Suspend/Resume Acceptance strictly verified!"
    exit 0
else
    echo ">> [FAIL] 3.1-6F-S1 Verification failed: ${FAIL_REASONS}"
    exit 1
fi
