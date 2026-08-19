#!/usr/bin/env bash
set -euo pipefail

echo "=============================================================================="
echo "3.1-6F-S1 · QEMU Real ACPI Hardware Suspend/Resume Acceptance Orchestrator"
echo "=============================================================================="

DEBIAN_FRONTEND=noninteractive apt-get update >/dev/null
DEBIAN_FRONTEND=noninteractive apt-get install -y qemu-system-x86 linux-image-virtual dbus libdbus-1-3 util-linux systemd e2fsprogs rsync >/dev/null

WORKSPACE="/workspace"
INSTALL_DIR="/usr/lib/internet-evidence-monitor"
TIME_RUNNER="${INSTALL_DIR}/tools/IEM.TimeRunner"

echo "Building and publishing IEM.TimeRunner (self-contained single-file)..."
dotnet publish "${WORKSPACE}/tools/IEM.TimeRunner/IEM.TimeRunner.csproj" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o "${INSTALL_DIR}/tools" >/dev/null
chmod 0755 "${TIME_RUNNER}"

echo "Creating QEMU ext4 root filesystem image (3072MB)..."
DISK_IMG="/tmp/qemu_rootfs.img"
MOUNT_DIR="/mnt/qemu_rootfs"
rm -f "${DISK_IMG}"
truncate -s 3072M "${DISK_IMG}"
mkfs.ext4 -F -q "${DISK_IMG}"
mkdir -p "${MOUNT_DIR}"
mount "${DISK_IMG}" "${MOUNT_DIR}"

echo "Copying minimal base system into rootfs image..."
rsync -aHAX \
    --exclude="/tmp/*" \
    --exclude="/proc/*" \
    --exclude="/sys/*" \
    --exclude="/dev/*" \
    --exclude="/mnt/*" \
    --exclude="/usr/share/dotnet" \
    --exclude="/root/.nuget" \
    --exclude="/root/.dotnet" \
    --exclude="/workspace" \
    / "${MOUNT_DIR}/" || true

echo "Copying clean workspace repository..."
mkdir -p "${MOUNT_DIR}/workspace"
rsync -aHAX \
    --exclude=".git" \
    --exclude="artifacts" \
    --exclude="publish" \
    --exclude="**/bin" \
    --exclude="**/obj" \
    /workspace/ "${MOUNT_DIR}/workspace/"

# Setup systemd one-shot service to run acceptance script on boot
cat << 'EOF' > "${MOUNT_DIR}/etc/systemd/system/iem-acceptance.service"
[Unit]
Description=IEM Physical Suspend Acceptance Test Runner
After=multi-user.target systemd-logind.service dbus.service

[Service]
Type=oneshot
WorkingDirectory=/workspace
ExecStart=/bin/bash /workspace/scripts/acceptance/verify-physical-suspend-resume.sh
ExecStopPost=/sbin/poweroff -f
StandardOutput=journal+console
StandardError=journal+console

[Install]
WantedBy=multi-user.target
EOF

chroot "${MOUNT_DIR}" systemctl enable iem-acceptance.service >/dev/null 2>&1 || true

# Prepare fstab
cat << 'EOF' > "${MOUNT_DIR}/etc/fstab"
/dev/vda / ext4 errors=remount-ro 0 1
EOF

umount "${MOUNT_DIR}"

VMLINUZ=$(ls -1 /boot/vmlinuz* | head -n1)
INITRD=$(ls -1 /boot/initrd.img* | head -n1)

echo "Booting full systemd QEMU VM with ACPI S3 sleep capability..."
echo "Kernel: ${VMLINUZ}, Initrd: ${INITRD}"

set +e
qemu-system-x86_64 \
    -kernel "${VMLINUZ}" \
    -initrd "${INITRD}" \
    -drive file="${DISK_IMG}",format=raw,if=virtio \
    -append "root=/dev/vda rw console=ttyS0 quiet panic=1 systemd.journald.forward_to_console=1" \
    -nographic \
    -m 1536M \
    -no-reboot
QEMU_EXIT=$?
set -e

echo "Extracting acceptance artifacts from QEMU disk..."
mount "${DISK_IMG}" "${MOUNT_DIR}"
mkdir -p /workspace/artifacts/acceptance/3.1-6
cp -f "${MOUNT_DIR}/workspace/artifacts/acceptance/3.1-6/"* /workspace/artifacts/acceptance/3.1-6/ 2>/dev/null || true
umount "${MOUNT_DIR}"
rm -rf "${MOUNT_DIR}" "${DISK_IMG}"

echo "=============================================================================="
echo "QEMU Acceptance Execution Completed (Exit: ${QEMU_EXIT})"
echo "=============================================================================="
