# 3.1-13 · Debian Package Lifecycle Acceptance (container)

- **Gate**: `3.1-13 · Linux Installation Lifecycle`
- **Runner**: `scripts/ci/verify-linux-install.sh`
- **Environment**: Docker container, image `ubuntu:24.04`, host Docker Desktop / WSL2 (kernel 6.6.87.2-microsoft-standard-WSL2)
- **Distro tier**: **Tier B** (§20.3). Ubuntu 24.04 is a compatibility target, *not* one of the
  Tier-A release blockers (Ubuntu 26.04 / Debian 13 / Fedora 44, §20.2).
- **Verdict**: **PASS**

## What this run proves

- `apt install ./<pkg>.deb` resolves and completes on a clean Ubuntu 24.04 root filesystem
  (59 packages pulled, no held or unmet dependency).
- `postinst` creates `iem`, `iem-users`, `iem-admin` and writes the
  `net.ipv4.ping_group_range` sysctl drop-in carrying the *resolved* `iem` GID.
- `systemd-analyze verify /usr/lib/systemd/system/internet-evidence-monitor.service` reports
  no unit errors.
- `apt install --reinstall` preserves system evidence, the signing key and the portable XDG
  state byte-for-byte (SHA-256 compared before and after).
- `apt purge` removes the payload and the sysctl drop-in, while evidence, signing identity
  and portable state survive — deinstalling the program must not destroy the evidence.

## What this run does NOT prove

- **PID 1 is not systemd** in this container, so the Type=notify lifecycle, capability
  bounding, `StateDirectory`/`RuntimeDirectory` semantics and `Restart=on-failure` are
  untested here. That is Lane C (§31.C).
- No machine reboot, no suspend/resume, no real GUI session.
- Tier-A release acceptance is unaffected by this run.

