#!/usr/bin/env bash
set -euo pipefail

# Install the built .deb and actually run a session through it.
#
# verify-linux-install.sh proves the package installs, upgrades and purges without
# losing evidence, but it never starts a session - so a service that installs
# cleanly, reaches active/running and then dies the instant it provisions storage
# looks perfectly healthy to it. That is exactly how RestrictSUIDSGID=yes shipped:
# systemd blocked openat2, and the daemon crash-looped on the first StartSession
# while every install-level check stayed green.
#
# This asks the only question those checks cannot: after a session starts, is the
# service still the same process, and did evidence actually land on disk?

PACKAGE_PATH="${1:-}"
if [ -z "${PACKAGE_PATH}" ] || [ ! -f "${PACKAGE_PATH}" ]; then
    echo "Usage: $0 path/to/current.deb" >&2
    exit 2
fi
PACKAGE_PATH="$(realpath "${PACKAGE_PATH}")"

if [ "$(ps -p 1 -o comm=)" != "systemd" ]; then
    echo "SKIP: PID 1 is not systemd; this smoke test needs a real service manager." >&2
    exit 0
fi

export DEBIAN_FRONTEND=noninteractive
UNIT=internet-evidence-monitor.service
STATE_ROOT=/var/lib/internet-evidence-monitor
SOCKET=/run/internet-evidence-monitor/control.sock

apt-get update -o Acquire::Retries=3
apt-get install -y python3 sudo
apt-get install -y "${PACKAGE_PATH}"

# A session is only startable by a member of iem-users.
id iem-smoke >/dev/null 2>&1 || useradd -m -G iem-users iem-smoke

for _ in $(seq 1 60); do
    [ -S "${SOCKET}" ] && break
    sleep 1
done
if [ ! -S "${SOCKET}" ]; then
    echo "FAIL: control socket never appeared at ${SOCKET}" >&2
    systemctl status "${UNIT}" --no-pager -l >&2 || true
    exit 1
fi

cat > /tmp/iem_smoke_client.py <<'PYEOF'
import json, socket, struct, sys

command, session_id = sys.argv[1], sys.argv[2]
req = {
    "protocolVersion": 1,
    "requestId": "req-" + command,
    "commandName": command,
    "sessionId": session_id,
    "payload": "{}",
}
body = json.dumps(req).encode("utf-8")
s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
s.connect("/run/internet-evidence-monitor/control.sock")
s.sendall(struct.pack(">I", len(body)) + body)
header = s.recv(4)
if len(header) < 4:
    print("ERROR: incomplete response header", file=sys.stderr)
    sys.exit(2)
remaining = struct.unpack(">I", header)[0]
raw = b""
while len(raw) < remaining:
    chunk = s.recv(min(4096, remaining - len(raw)))
    if not chunk:
        break
    raw += chunk
print(raw.decode("utf-8"))
PYEOF

RESTARTS_BEFORE="$(systemctl show -p NRestarts --value "${UNIT}")"
INSTANCE_BEFORE="$(systemctl show -p MainPID --value "${UNIT}")"

RESPONSE="$(sudo -u iem-smoke python3 /tmp/iem_smoke_client.py StartSession session-smoke)"
echo "StartSession -> ${RESPONSE}"
if ! printf '%s' "${RESPONSE}" | grep -Eq '"status": *(0|"Success")'; then
    echo "FAIL: StartSession was not accepted." >&2
    exit 1
fi

# Long enough for the storage boundary to provision and the first observations to
# be written; a crash loop announces itself well inside this window.
sleep 15

RESTARTS_AFTER="$(systemctl show -p NRestarts --value "${UNIT}")"
INSTANCE_AFTER="$(systemctl show -p MainPID --value "${UNIT}")"
ACTIVE_STATE="$(systemctl show -p ActiveState --value "${UNIT}")"
RESULT="$(systemctl show -p Result --value "${UNIT}")"

echo "restarts ${RESTARTS_BEFORE} -> ${RESTARTS_AFTER}, MainPID ${INSTANCE_BEFORE} -> ${INSTANCE_AFTER}, ${ACTIVE_STATE}/${RESULT}"

if [ "${RESTARTS_AFTER}" != "${RESTARTS_BEFORE}" ] || [ "${INSTANCE_AFTER}" != "${INSTANCE_BEFORE}" ]; then
    echo "FAIL: the service restarted while running a session." >&2
    journalctl -u "${UNIT}" -n 60 --no-pager >&2 || true
    exit 1
fi

if [ "${ACTIVE_STATE}" != "active" ] || [ "${RESULT}" != "success" ]; then
    echo "FAIL: service is ${ACTIVE_STATE}/${RESULT} after starting a session." >&2
    journalctl -u "${UNIT}" -n 60 --no-pager >&2 || true
    exit 1
fi

# The point of the product: evidence on disk, inside the service's own boundary.
EVIDENCE_COUNT="$(find "${STATE_ROOT}/sessions" -type f -name 'SirovaEvidencija.jsonl' 2>/dev/null | wc -l)"
if [ "${EVIDENCE_COUNT}" -lt 1 ]; then
    echo "FAIL: session produced no evidence journal under ${STATE_ROOT}/sessions." >&2
    find "${STATE_ROOT}" -maxdepth 3 >&2 || true
    exit 1
fi

SESSIONS_MODE="$(stat -c '%U:%G %a' "${STATE_ROOT}/sessions")"
if [ "${SESSIONS_MODE}" != "iem:iem 700" ]; then
    echo "FAIL: sessions segment is ${SESSIONS_MODE}, expected iem:iem 700." >&2
    exit 1
fi

echo "Session smoke passed: service survived a live session and wrote evidence."
