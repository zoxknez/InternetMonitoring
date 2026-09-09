#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-linux-x64}"
VERSION="${IEM_VERSION:-3.1.0-alpha.1}"
OUTPUT_ROOT="${IEM_LINUX_OUTPUT_ROOT:-${ROOT_DIR}/artifacts/linux}"

case "${RID}" in
    linux-x64)
        DEB_ARCH="amd64"
        ;;
    linux-arm64)
        DEB_ARCH="arm64"
        ;;
    *)
        echo "Nepodržan RID: ${RID}. Dozvoljeni su linux-x64 i linux-arm64." >&2
        exit 2
        ;;
esac

case "${OUTPUT_ROOT}" in
    "${ROOT_DIR}/artifacts/"*|"${ROOT_DIR}/artifacts") ;;
    *)
        echo "Output mora ostati unutar ${ROOT_DIR}/artifacts." >&2
        exit 2
        ;;
esac

BUILD_ROOT="${OUTPUT_ROOT}/.work/${RID}"
PUBLISH_ROOT="${BUILD_ROOT}/publish"
STAGE_ROOT="${BUILD_ROOT}/stage"
PACKAGE_ROOT="${OUTPUT_ROOT}/${RID}"
DOTNET_ARTIFACTS_ROOT="${BUILD_ROOT}/dotnet"

rm -rf -- "${BUILD_ROOT}" "${PACKAGE_ROOT}"
mkdir -p "${PUBLISH_ROOT}/app" "${PUBLISH_ROOT}/service" "${STAGE_ROOT}" "${PACKAGE_ROOT}"

dotnet restore "${ROOT_DIR}/src/IEM.App.Linux/IEM.App.Linux.csproj" \
    --artifacts-path "${DOTNET_ARTIFACTS_ROOT}" \
    -p:VerifyLockedDependencies=true --nologo
dotnet restore "${ROOT_DIR}/src/IEM.Service.Linux/IEM.Service.Linux.csproj" \
    --artifacts-path "${DOTNET_ARTIFACTS_ROOT}" \
    -p:VerifyLockedDependencies=true --nologo

dotnet publish "${ROOT_DIR}/src/IEM.App.Linux/IEM.App.Linux.csproj" \
    -c Release -r "${RID}" --self-contained true --no-restore --nologo \
    --artifacts-path "${DOTNET_ARTIFACTS_ROOT}" \
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false \
    -p:Version="${VERSION}" -o "${PUBLISH_ROOT}/app"

dotnet publish "${ROOT_DIR}/src/IEM.Service.Linux/IEM.Service.Linux.csproj" \
    -c Release -r "${RID}" --self-contained true --no-restore --nologo \
    --artifacts-path "${DOTNET_ARTIFACTS_ROOT}" \
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false \
    -p:Version="${VERSION}" -o "${PUBLISH_ROOT}/service"

install -d "${STAGE_ROOT}/usr/lib/internet-evidence-monitor/app"
install -d "${STAGE_ROOT}/usr/lib/internet-evidence-monitor/service"
install -d "${STAGE_ROOT}/usr/bin"
install -d "${STAGE_ROOT}/usr/share/applications"
install -d "${STAGE_ROOT}/usr/share/icons/hicolor/scalable/apps"
install -d "${STAGE_ROOT}/usr/share/doc/internet-evidence-monitor"
install -d "${STAGE_ROOT}/usr/lib/systemd/system"

cp -a "${PUBLISH_ROOT}/app/." "${STAGE_ROOT}/usr/lib/internet-evidence-monitor/app/"
cp -a "${PUBLISH_ROOT}/service/." "${STAGE_ROOT}/usr/lib/internet-evidence-monitor/service/"
install -m 0755 "${ROOT_DIR}/packaging/linux/internet-evidence-monitor" \
    "${STAGE_ROOT}/usr/bin/internet-evidence-monitor"
install -m 0644 "${ROOT_DIR}/packaging/linux/internet-evidence-monitor.desktop" \
    "${STAGE_ROOT}/usr/share/applications/internet-evidence-monitor.desktop"
install -m 0644 "${ROOT_DIR}/packaging/linux/internet-evidence-monitor.svg" \
    "${STAGE_ROOT}/usr/share/icons/hicolor/scalable/apps/internet-evidence-monitor.svg"
install -m 0644 "${ROOT_DIR}/packaging/systemd/internet-evidence-monitor.service" \
    "${STAGE_ROOT}/usr/lib/systemd/system/internet-evidence-monitor.service"
install -m 0644 "${ROOT_DIR}/LICENSE" \
    "${STAGE_ROOT}/usr/share/doc/internet-evidence-monitor/copyright"

chmod 0755 "${STAGE_ROOT}/usr/lib/internet-evidence-monitor/app/IEM.App.Linux"
chmod 0755 "${STAGE_ROOT}/usr/lib/internet-evidence-monitor/service/IEM.Service.Linux"
find "${STAGE_ROOT}/usr/lib/internet-evidence-monitor" -type f \
    ! -name 'IEM.App.Linux' ! -name 'IEM.Service.Linux' ! -name 'createdump' \
    -exec chmod 0644 {} +
find "${STAGE_ROOT}/usr/lib/internet-evidence-monitor" -type f -name 'createdump' \
    -exec chmod 0755 {} +

# SBOM over the staged payload - the exact bytes that ship (invariants 200, 201).
python3 "${ROOT_DIR}/scripts/ci/generate-linux-sbom.py" \
    --repo-root "${ROOT_DIR}" \
    --payload-root "${STAGE_ROOT}/usr/lib/internet-evidence-monitor" \
    --version "${VERSION}" \
    --rid "${RID}" \
    --output "${STAGE_ROOT}/usr/share/doc/internet-evidence-monitor/sbom.json"
install -m 0644 "${STAGE_ROOT}/usr/share/doc/internet-evidence-monitor/sbom.json" \
    "${PACKAGE_ROOT}/sbom-${RID}.json"

PORTABLE_NAME="MonitorInternetDokaza-${VERSION}-${RID}-portable.tar.gz"
tar -C "${PUBLISH_ROOT}/app" -czf "${PACKAGE_ROOT}/${PORTABLE_NAME}" .

if command -v dpkg-deb >/dev/null 2>&1; then
    install -d "${STAGE_ROOT}/DEBIAN"
    INSTALLED_SIZE="$(du -sk "${STAGE_ROOT}/usr" | cut -f1)"
    DEB_VERSION="${VERSION/-alpha./~alpha}"

    cat > "${STAGE_ROOT}/DEBIAN/control" <<EOF
Package: internet-evidence-monitor
Version: ${DEB_VERSION}
Section: net
Priority: optional
Architecture: ${DEB_ARCH}
Installed-Size: ${INSTALLED_SIZE}
Maintainer: Internet Evidence Monitor contributors
Depends: adduser, systemd, libx11-6, libice6, libsm6, libfontconfig1, ca-certificates, tzdata, libc6, libgcc-s1, libstdc++6, zlib1g, libssl3t64 | libssl3, libicu | libicu78 | libicu76 | libicu74 | libicu72
Homepage: https://github.com/zoxknez/InternetMonitoring
Description: Lokalni nadzor prekida internet veze i proverljiv dokazni paket
 Grafička aplikacija i systemd servis beleže mrežna zapažanja bez slanja
 korisničkih podataka na spoljne servere.
EOF
    install -m 0755 "${ROOT_DIR}/packaging/linux/debian/postinst" "${STAGE_ROOT}/DEBIAN/postinst"
    install -m 0755 "${ROOT_DIR}/packaging/linux/debian/prerm" "${STAGE_ROOT}/DEBIAN/prerm"
    install -m 0755 "${ROOT_DIR}/packaging/linux/debian/postrm" "${STAGE_ROOT}/DEBIAN/postrm"

    dpkg-deb --root-owner-group --build "${STAGE_ROOT}" \
        "${PACKAGE_ROOT}/internet-evidence-monitor_${DEB_VERSION}_${DEB_ARCH}.deb"
fi

if command -v rpmbuild >/dev/null 2>&1; then
    RPM_TOP="${BUILD_ROOT}/rpmbuild"
    mkdir -p "${RPM_TOP}/BUILD" "${RPM_TOP}/BUILDROOT" "${RPM_TOP}/RPMS" \
        "${RPM_TOP}/SOURCES" "${RPM_TOP}/SPECS" "${RPM_TOP}/SRPMS"
    tar -C "${STAGE_ROOT}" --exclude='./DEBIAN' -czf "${RPM_TOP}/SOURCES/iem-payload.tar.gz" .
    cp "${ROOT_DIR}/packaging/linux/rpm/internet-evidence-monitor.spec" "${RPM_TOP}/SPECS/"
    rpmbuild -bb "${RPM_TOP}/SPECS/internet-evidence-monitor.spec" \
        --define "_topdir ${RPM_TOP}" \
        --define "_iem_version ${VERSION}"
    find "${RPM_TOP}/RPMS" -type f -name '*.rpm' -exec cp {} "${PACKAGE_ROOT}/" \;
fi

(
    cd "${PACKAGE_ROOT}"
    # The output directory may have existed before older versions of this script cleaned it.
    # Never hash a previous checksum file (or the temporary file being written).
    rm -f -- SHA256SUMS SHA256SUMS.tmp
    sha256sum -- * > SHA256SUMS.tmp
    mv -- SHA256SUMS.tmp SHA256SUMS
)

echo "Linux artefakti su napravljeni u ${PACKAGE_ROOT}"
