#!/usr/bin/env bash
set -euo pipefail

PACKAGE_PATH="${1:-}"
if [ -z "${PACKAGE_PATH}" ] || [ ! -f "${PACKAGE_PATH}" ]; then
    echo "Usage: $0 path/to/package.deb" >&2
    exit 2
fi

export DEBIAN_FRONTEND=noninteractive

apt-get update
apt-get install -y file desktop-file-utils
apt-get install -y "${PACKAGE_PATH}"

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

install -d -m 0700 -o iem -g iem /var/lib/internet-evidence-monitor
printf '%s\n' 'preserve-evidence-on-uninstall' \
    > /var/lib/internet-evidence-monitor/verification-sentinel.txt
chown iem:iem /var/lib/internet-evidence-monitor/verification-sentinel.txt

apt-get purge -y internet-evidence-monitor

if [ -e /usr/bin/internet-evidence-monitor ] || \
   [ -e /usr/lib/internet-evidence-monitor ]; then
    echo "Package payload remained after purge." >&2
    exit 1
fi

if [ ! -f /var/lib/internet-evidence-monitor/verification-sentinel.txt ]; then
    echo "Evidence directory was removed during purge." >&2
    exit 1
fi

if [ -f "${SYSCTL_DROPIN}" ]; then
    echo "sysctl drop-in survived purge." >&2
    exit 1
fi

getent passwd iem >/dev/null
getent group iem-users >/dev/null
getent group iem-admin >/dev/null

echo "Clean Ubuntu install, validation and evidence-preserving purge passed."
