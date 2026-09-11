# 3.1-14 · Lane C pre-flight (container, NOT a release gate)

- **Runner**: `scripts/ci/lane-c-systemd-acceptance.sh`
- **Host**: Docker container, `iem-ubuntu24-systemd:10.0.111`, `--privileged --cgroupns=host`,
  PID 1 = systemd 255, kernel 6.6.87.2-microsoft-standard-WSL2, .NET SDK 10.0.111
- **Authority**: **none**. §20.2 makes Tier A Ubuntu 26.04 / Debian 13 / Fedora 44, and §20.3
  puts Ubuntu 24.04 in Tier B. §31.C additionally requires a real VM image, not a container.
  This run is a pre-flight that finds defects cheaply; it grades nothing.

## Why this run mattered anyway

Lane C had never executed past its second gate since 3.1-11. It published the service flat
into `/usr/lib/internet-evidence-monitor`, while the canonical unit it installs starts
`/usr/lib/internet-evidence-monitor/service/IEM.Service.Linux`. systemd answered with
`203/EXEC`, so gates 5 and beyond were recorded `NOT_TESTED` and nobody saw it, because the
runner had only ever been syntax-checked with `bash -n`.

With the layout corrected the runner reached gate 10, and gate 10 immediately found a
release-blocking defect in the shipped unit (see below).

## Gate frontier

| | Before any fix | After layout fix | After all fixes |
|---|---|---|---|
| Gates PASS | 4 | 9 | 9 |
| First failure | gate 5, `Type=notify` (`203/EXEC`) | gate 10, IPC matrix | gate 10, sub-test 6 only |
| IPC sub-tests passing | 0 of 6 | 2 of 6 | **5 of 6** |

Within gate 10, outsider denial, `GetServiceStatus`, `StartSession`, the spoofed cross-user
stop denial and the owner's own `StopSession` all pass. Only the admin-override
`FinalizeSession` still fails; it is characterised below.

Two runner defects were fixed on the way, both of which made the runner blame the product
for its own problems:

- It published the service flat into `${INSTALL_DIR}` while the canonical unit starts
  `${INSTALL_DIR}/service/…`, so the service died with `203/EXEC` at gate 5.
- It calls `jq` in eleven places and nothing checked for it. Without `jq`,
  `read_active_session_id` returns an empty string, `wait_for_active_session` times out, and
  the run reports `StartSession failed` while quoting a response that plainly says
  `"Accepted": true`. A prerequisite check now fails loudly and names the missing tool.

It also published with `PublishSingleFile=true` and an unlocked restore, so it graded a
binary shape the package never ships and silently rewrote committed `packages.lock.json`
files on every run. It now publishes exactly as `publish-linux.sh` does.

## Open: admin override FinalizeSession (gate 10, sub-test 6)

Reproducible against the shipped `3.1.0~alpha5` `.deb`, not a timing race and not a
container artifact:

```
StartSession demo-1   -> Accepted: true;  GetServiceStatus -> SessionId demo-1
StopSession  demo-1   -> Accepted: true   (pause)
StartSession demo-1   -> Accepted: true   (resume)
FinalizeSession demo-1 -> status 8 SESSION_NOT_FOUND
                          "Nema aktivne sesije kojom se moze upravljati."
```

At that point `GetServiceStatus` still names `demo-1`, but reports `State: 3`
(`SessionState.Interrupted`) with `Resumed: false` and `FinalizeStep: 5 "Zavrseno."`. It
fails identically for the session owner and for an `iem-admin` member, so it is not an
authorization problem. A subsequent `StartSession` with a new id is accepted but does not
become the current session.

Whether the defect is in the resume path or in Lane C's expectation depends on the intended
pause/resume semantics, which is a design question about evidence continuity rather than an
obvious coding slip. Left open deliberately rather than patched by guesswork.

## The defect gate 10 found

`RestrictSUIDSGID=yes` and `openat2(2)` are mutually exclusive. seccomp cannot dereference
the `struct open_how` that `openat2` takes, so it cannot inspect the mode argument, and
systemd blocks the syscall outright instead of filtering it. The storage boundary resolves
every evidence path through `openat2` with `RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS |
RESOLVE_NO_XDEV | RESOLVE_NO_MAGICLINKS`.

Isolated by bisecting the unit's hardening directives one at a time under `systemd-run`:
every other directive left `openat2` working; `RestrictSUIDSGID=yes` alone turned all six
flag combinations into `ENOSYS`, including the call with no resolve flags at all.

Confirmed against the built `.deb`, not just the runner:

| | `RestrictSUIDSGID=yes` (shipped) | `RestrictSUIDSGID=no` |
|---|---|---|
| `apt install` | OK | OK |
| Service state | `active (running)` | `active (running)` |
| `StartSession` accepted | yes | yes |
| Restarts within 15s | **11, crash loop** | **0** |
| Evidence written | **none** | `sesija.db`, `SirovaEvidencija.jsonl`, `layout.json` |
| `sessions` segment | — | `iem:iem 0700` |

The failure hides behind a healthy daemon: everything an install-level check can see stays
green. `scripts/ci/verify-linux-session-smoke.sh` was added to close exactly that gap.

## Still not proven anywhere

- Tier-A distro acceptance (§20.2) — needs real Ubuntu 26.04 / Debian 13 / Fedora 44 VMs.
- Machine-reboot continuity and suspend/resume — a container shares the host kernel.
- Gates 11-23, still `NOT_TESTED`: netlink FIB, nl80211, datagram ICMP, source-binding
  parity, protocol parity, rtnetlink observer, netns fault injection, adjtimex provenance,
  logind D-Bus, dual-clock continuity, STOP persistence.
- The runner publishes the service with `PublishSingleFile=true`, while the package ships a
  241-file self-contained layout. The gate therefore exercises a binary shape no user ever
  receives; worth reconciling before Tier-A runs are treated as authoritative.
