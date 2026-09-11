# 3.1-6 · Linux Host + Network + Power + Time Live Acceptance Report

- **Timestamp**: 2026-09-11 12:25:58 UTC
- **Commit**: `UNKNOWN`
- **Distro**: ubuntu 24.04 (x86_64)
- **Kernel**: 6.6.87.2-microsoft-standard-WSL2
- **systemd**: systemd 255 (255.4-1ubuntu8.17)
- **.NET SDK**: 10.0.111
- **Failed Stage**: STAGE_9_5_UNIX_IPC_IDENTITY: Unix IPC control.sock authorization matrix failed

## Process & Directory Facts
- **MainPID**: 1991
- **User / UID**: iem (997)
- **Group / GID**: iem (996)
- **Supplementary Groups in Process**: `	996 997 `
- **Capabilities**: `CapEff=0000000000000000`, `CapAmb=0000000000000000`
- **StateDirectory Stat**: `iem:iem 700`
- **RuntimeDirectory Stat**: `iem:iem-users 750`
- **Lifecycle Session**: expected ``, after start ``, after restart ``, after crash ``

## Gate Status Matrix
| Gate | Status |
|---|---|
| PID 1 Strictness | **PASS** |
| Build & Publish | **PASS** |
| Service Account Provisioning | **PASS** |
| Unit Installation | **PASS** |
| Type=notify Readiness | **PASS** |
| Process Identity & Supplementary Groups | **PASS** |
| Capability Bounding | **PASS** |
| StateDirectory Persistence | **PASS** |
| RuntimeDirectory Ephemeral Lifecycle | **PASS** |
| Unix IPC control.sock 5-Layer Identity | **FAIL** |
| Netlink RTM_GETROUTE Kernel FIB Routing | **NOT_TESTED** |
| Generic Netlink nl80211 Family & Interface Dump | **NOT_TESTED** |
| Unprivileged Datagram ICMP Echo (SOCK_DGRAM) | **NOT_TESTED** |
| Source-Address Binding Parity (ICMP/TCP/DNS/HTTP) | **NOT_TESTED** |
| Core Protocol Parity (System DNS/Public DNS/TLS/HTTP) | **NOT_TESTED** |
| Gateway & FIB Path Resolution Integration | **NOT_TESTED** |
| Rtnetlink Observer & Route TOCTOU Continuity | **NOT_TESTED** |
| Netns Probe Execution & Fault Injection Matrix | **NOT_TESTED** |
| Linux Time, Boot & adjtimex Provenance | **NOT_TESTED** |
| systemd-logind D-Bus Signal Availability | **NOT_TESTED** |
| Suspend/Resume Dual-Clock Continuity | **NOT_TESTED** |
| STOP Persistence & Cleanup | **NOT_TESTED** |
| RESTART Persistence & Restore | **NOT_TESTED** |
| ProtectSystem=strict Mount Namespace | **NOT_TESTED** |
| Failure / Restart Propagation | **NOT_TESTED** |
| FatalExitCode == 3 Verification | **NOT_TESTED** |
| Start Without Network | **NOT_TESTED** |

## Summary
- **PASS**: 9
- **FAIL**: 1
- **NOT_TESTED**: 0
- **Final Verdict**: **FAIL**
