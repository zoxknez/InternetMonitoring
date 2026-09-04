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

install -d -m 0700 -o iem -g iem /var/lib/internet-evidence-monitor
printf '%s\n' 'preserve-evidence-on-uninstall' \
    > /var/lib/internet-evidence-monitor/verification-sentinel.txt
chown iem:iem /var/lib/internet-evidence-monitor/verification-sentinel.txt

apt-get remove -y internet-evidence-monitor

if [ -e /usr/bin/internet-evidence-monitor ] || \
   [ -e /usr/lib/internet-evidence-monitor ]; then
    echo "Package payload remained after uninstall." >&2
    exit 1
fi

if [ ! -f /var/lib/internet-evidence-monitor/verification-sentinel.txt ]; then
    echo "Evidence directory was removed during uninstall." >&2
    exit 1
fi

getent passwd iem >/dev/null
getent group iem-users >/dev/null

echo "Clean Ubuntu install, validation and evidence-preserving uninstall passed."
