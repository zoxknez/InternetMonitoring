#!/usr/bin/env bash
set -euo pipefail

PACKAGE_PATH="${1:-}"
if [ -z "${PACKAGE_PATH}" ] || [ ! -f "${PACKAGE_PATH}" ]; then
    echo "Usage: $0 path/to/package.deb" >&2
    exit 2
fi

PACKAGE_PATH="$(realpath "${PACKAGE_PATH}")"
VERIFY_ROOT="$(mktemp -d /tmp/iem-package-verify.XXXXXX)"

cleanup() {
    case "${VERIFY_ROOT}" in
        /tmp/iem-package-verify.*)
            rm -rf -- "${VERIFY_ROOT}"
            ;;
        *)
            echo "Refusing to remove unexpected verification path: ${VERIFY_ROOT}" >&2
            ;;
    esac
}
trap cleanup EXIT

PAYLOAD_ROOT="${VERIFY_ROOT}/payload"
CONTROL_ROOT="${VERIFY_ROOT}/control"
mkdir -p "${PAYLOAD_ROOT}" "${CONTROL_ROOT}"

dpkg-deb --extract "${PACKAGE_PATH}" "${PAYLOAD_ROOT}"
dpkg-deb --control "${PACKAGE_PATH}" "${CONTROL_ROOT}"

PACKAGE_NAME="$(dpkg-deb --field "${PACKAGE_PATH}" Package)"
PACKAGE_ARCH="$(dpkg-deb --field "${PACKAGE_PATH}" Architecture)"
if [ "${PACKAGE_NAME}" != "internet-evidence-monitor" ]; then
    echo "Unexpected Debian package name: ${PACKAGE_NAME}" >&2
    exit 1
fi

APP="${PAYLOAD_ROOT}/usr/lib/internet-evidence-monitor/app/IEM.App.Linux"
SERVICE="${PAYLOAD_ROOT}/usr/lib/internet-evidence-monitor/service/IEM.Service.Linux"
LAUNCHER="${PAYLOAD_ROOT}/usr/bin/internet-evidence-monitor"
UNIT="${PAYLOAD_ROOT}/usr/lib/systemd/system/internet-evidence-monitor.service"
DESKTOP="${PAYLOAD_ROOT}/usr/share/applications/internet-evidence-monitor.desktop"
ICON="${PAYLOAD_ROOT}/usr/share/icons/hicolor/scalable/apps/internet-evidence-monitor.svg"
LICENSE_FILE="${PAYLOAD_ROOT}/usr/share/doc/internet-evidence-monitor/copyright"
SBOM="${PAYLOAD_ROOT}/usr/share/doc/internet-evidence-monitor/sbom.json"

for required in "${APP}" "${SERVICE}" "${LAUNCHER}" "${UNIT}" "${DESKTOP}" "${ICON}" "${LICENSE_FILE}" "${SBOM}"; do
    if [ ! -f "${required}" ]; then
        echo "Required package path is missing: ${required#${PAYLOAD_ROOT}}" >&2
        exit 1
    fi
done

for executable in "${APP}" "${SERVICE}" "${LAUNCHER}"; do
    if [ ! -x "${executable}" ]; then
        echo "Expected executable bit: ${executable#${PAYLOAD_ROOT}}" >&2
        exit 1
    fi
done

while IFS= read -r dump_helper; do
    if [ ! -x "${dump_helper}" ]; then
        echo "createdump must remain executable: ${dump_helper#${PAYLOAD_ROOT}}" >&2
        exit 1
    fi
done < <(find "${PAYLOAD_ROOT}/usr/lib/internet-evidence-monitor" -type f -name createdump -print)

if find "${PAYLOAD_ROOT}/usr/lib/internet-evidence-monitor" -type f -name '*.pdb' -print -quit | grep -q .; then
    echo "Release package contains debug symbol files." >&2
    exit 1
fi

ELF_DESCRIPTION="$(file "${APP}" "${SERVICE}")"
if ! grep -q 'ELF 64-bit' <<< "${ELF_DESCRIPTION}"; then
    echo "Published app or service is not a 64-bit ELF executable." >&2
    exit 1
fi

case "${PACKAGE_ARCH}" in
    amd64)
        grep -q 'x86-64' <<< "${ELF_DESCRIPTION}"
        ;;
    arm64)
        grep -Eq 'ARM aarch64|ARM64' <<< "${ELF_DESCRIPTION}"
        ;;
    *)
        echo "Unsupported Debian architecture: ${PACKAGE_ARCH}" >&2
        exit 1
        ;;
esac

if [ "${PACKAGE_ARCH}" = "$(dpkg --print-architecture)" ]; then
    for executable in "${APP}" "${SERVICE}"; do
        if ldd "${executable}" | grep -q 'not found'; then
            ldd "${executable}" >&2
            exit 1
        fi
    done
fi

grep -qx 'Exec=internet-evidence-monitor' "${DESKTOP}"
grep -qx 'ExecStart=/usr/lib/internet-evidence-monitor/service/IEM.Service.Linux' "${UNIT}"
grep -qx 'Type=notify' "${UNIT}"
grep -qx 'ProtectSystem=strict' "${UNIT}"

for maintainer_script in postinst prerm postrm; do
    if [ ! -x "${CONTROL_ROOT}/${maintainer_script}" ]; then
        echo "Maintainer script is missing or not executable: ${maintainer_script}" >&2
        exit 1
    fi
    dash -n "${CONTROL_ROOT}/${maintainer_script}"
done

# The postinst must provision all three canonical groups and the unprivileged-ICMP tunable.
for token in 'iem-users' 'iem-admin' 'ping_group_range'; do
    if ! grep -q "${token}" "${CONTROL_ROOT}/postinst"; then
        echo "postinst does not reference required token: ${token}" >&2
        exit 1
    fi
done
if ! grep -q 'sysctl.d/99-internet-evidence-monitor.conf' "${CONTROL_ROOT}/postrm"; then
    echo "postrm does not clean up the sysctl drop-in on purge." >&2
    exit 1
fi

# SBOM: IEM-SBOM-1, generated from this payload.
if command -v python3 >/dev/null 2>&1; then
    python3 - "${SBOM}" <<'PY'
import json, sys
doc = json.load(open(sys.argv[1], encoding="utf-8"))
assert doc.get("SbomFormat") == "IEM-SBOM-1", doc.get("SbomFormat")
assert doc.get("ComponentCount", 0) == len(doc.get("Components", [])), "component count mismatch"
assert any(c["PackageType"] == "payload-file" for c in doc["Components"]), "no payload files in SBOM"
assert any(c["PackageType"] == "nuget" for c in doc["Components"]), "no nuget components in SBOM"
print(f"SBOM OK: {doc['ComponentCount']} components")
PY
fi

if command -v desktop-file-validate >/dev/null 2>&1; then
    desktop-file-validate "${DESKTOP}"
fi

echo "Verified Debian package: $(basename "${PACKAGE_PATH}")"
