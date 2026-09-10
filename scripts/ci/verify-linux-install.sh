#!/usr/bin/env bash
set -euo pipefail

PACKAGE_PATH="${1:-}"
if [ -z "${PACKAGE_PATH}" ] || [ ! -f "${PACKAGE_PATH}" ]; then
    echo "Usage: $0 path/to/current.deb [path/to/previous.deb]" >&2
    exit 2
fi
PACKAGE_PATH="$(realpath "${PACKAGE_PATH}")"

PREVIOUS_PACKAGE_PATH="${2:-}"
if [ -n "${PREVIOUS_PACKAGE_PATH}" ]; then
    if [ ! -f "${PREVIOUS_PACKAGE_PATH}" ]; then
        echo "Previous package does not exist: ${PREVIOUS_PACKAGE_PATH}" >&2
        exit 2
    fi
    PREVIOUS_PACKAGE_PATH="$(realpath "${PREVIOUS_PACKAGE_PATH}")"
fi

export DEBIAN_FRONTEND=noninteractive

apt-get update
apt-get install -y file desktop-file-utils
apt-get install -y "${PREVIOUS_PACKAGE_PATH:-${PACKAGE_PATH}}"

bash scripts/ci/verify-linux-package.sh "${PACKAGE_PATH}"
dpkg-query -W -f='Package=${Package} Version=${Version} Status=${Status}\n' \
    internet-evidence-monitor
systemd-analyze verify /usr/lib/systemd/system/internet-evidence-monitor.service

getent passwd iem >/dev/null
getent group iem-users >/dev/null
getent group iem-admin >/dev/null

# The unprivileged-ICMP drop-in must exist and name the resolved iem GID.
IEM_GID="$(getent group iem | cut -d: -f3)"
SYSCTL_DROPIN=/etc/sysctl.d/99-internet-evidence-monitor.conf
if [ ! -f "${SYSCTL_DROPIN}" ]; then
    echo "sysctl drop-in was not created by postinst." >&2
    exit 1
fi
if ! grep -qx "net.ipv4.ping_group_range = ${IEM_GID} ${IEM_GID}" "${SYSCTL_DROPIN}"; then
    echo "sysctl drop-in does not carry the resolved iem GID (${IEM_GID})." >&2
    cat "${SYSCTL_DROPIN}" >&2
    exit 1
fi

STATE_ROOT=/var/lib/internet-evidence-monitor
EVIDENCE_SENTINEL="${STATE_ROOT}/sessions/lifecycle-verification/evidence.jsonl"
KEY_SENTINEL="${STATE_ROOT}/keys/evidence-signing-v1.p8"
PORTABLE_SENTINEL=/root/.local/state/internet-evidence-monitor/lifecycle-portable-sentinel.txt

install -d -m 0700 -o iem -g iem "${STATE_ROOT}" "${STATE_ROOT}/sessions" \
    "${STATE_ROOT}/sessions/lifecycle-verification" "${STATE_ROOT}/keys"
printf '%s\n' 'preserve-system-evidence-across-lifecycle' > "${EVIDENCE_SENTINEL}"
printf '%s\n' 'preserve-system-key-identity-across-lifecycle' > "${KEY_SENTINEL}"
chown iem:iem "${EVIDENCE_SENTINEL}" "${KEY_SENTINEL}"
chmod 0600 "${EVIDENCE_SENTINEL}" "${KEY_SENTINEL}"

install -d -m 0700 "$(dirname "${PORTABLE_SENTINEL}")"
printf '%s\n' 'portable-state-is-outside-system-package-authority' > "${PORTABLE_SENTINEL}"

EVIDENCE_HASH="$(sha256sum "${EVIDENCE_SENTINEL}" | cut -d' ' -f1)"
KEY_HASH="$(sha256sum "${KEY_SENTINEL}" | cut -d' ' -f1)"
PORTABLE_HASH="$(sha256sum "${PORTABLE_SENTINEL}" | cut -d' ' -f1)"

assert_state_preserved() {
    test "$(sha256sum "${EVIDENCE_SENTINEL}" | cut -d' ' -f1)" = "${EVIDENCE_HASH}"
    test "$(sha256sum "${KEY_SENTINEL}" | cut -d' ' -f1)" = "${KEY_HASH}"
    test "$(sha256sum "${PORTABLE_SENTINEL}" | cut -d' ' -f1)" = "${PORTABLE_HASH}"
}

if [ -n "${PREVIOUS_PACKAGE_PATH}" ]; then
    # Real version transition: the state and signing namespace belong to the service, not
    # to the package payload, and must survive the upgrade byte-for-byte.
    apt-get install -y "${PACKAGE_PATH}"
    assert_state_preserved
fi

# Reinstallation is a distinct lifecycle operation even when no previous version was supplied.
apt-get install -y --reinstall "${PACKAGE_PATH}"
assert_state_preserved

apt-get purge -y internet-evidence-monitor

if [ -e /usr/bin/internet-evidence-monitor ] || \
   [ -e /usr/lib/internet-evidence-monitor ]; then
    echo "Package payload remained after purge." >&2
    exit 1
fi

assert_state_preserved

if [ -f "${SYSCTL_DROPIN}" ]; then
    echo "sysctl drop-in survived purge." >&2
    exit 1
fi

getent passwd iem >/dev/null
getent group iem-users >/dev/null
getent group iem-admin >/dev/null

echo "Clean install, upgrade/reinstall validation and evidence-preserving purge passed."
