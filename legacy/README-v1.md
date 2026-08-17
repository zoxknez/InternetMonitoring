# Internet Evidence Monitor 1.0

Portable Windows evidence collector for intermittent Wi-Fi / Internet problems.

## What it proves better than a single ping

The monitor separates several failure layers:

1. **WIFI_LINK_DOWN**
   - Windows reports that the monitored network adapter is not `Up`.
   - Strong indication of a local Wi-Fi association/adapter/link problem.

2. **LOCAL_GATEWAY_DOWN**
   - Adapter is still `Up`.
   - Local default gateway does not answer.
   - All independent external IP checks fail.
   - HTTP connectivity probe also fails.
   - Points to the local router/AP path or the immediate uplink.

3. **INTERNET_DOWN**
   - Adapter is `Up`.
   - All independent public IP targets fail.
   - HTTP probe fails too.
   - This is much stronger evidence than one failed ping.

4. **DNS_FAILURE**
   - Public IP connectivity still works.
   - DNS resolution fails on consecutive checks.
   - Separates DNS problems from total Internet failure.

5. **PARTIAL_PACKET_LOSS / HIGH_LATENCY**
   - Internet still works, but quality is degraded.
   - Logged as degradation rather than a full outage.

6. **MONITORING_GAP**
   - The script/PC did not sample for an abnormally long period.
   - Typical reasons: sleep, shutdown, reboot, process termination.
   - It is **not counted as ISP downtime**.

## Evidence sources

The session directory contains:

- `samples.csv` - every measurement, normally every 5 seconds.
- `incidents.csv` - confirmed outage windows with start/end/duration.
- `state-transitions.csv` - all state changes and monitoring gaps.
- `REPORT.html` - human-readable final report.
- `SUMMARY.txt` - compact summary.
- `session-meta.json` - test configuration and start time.
- `runtime-state.json` - most recent checkpoint.
- `windows-wlan-events.csv` - Windows WLAN AutoConfig event log.
- `windows-network-system-events.csv` - related Windows System network events.
- `windows-native-wlan-report.html` - native Windows WLAN report when Windows can generate it.
- `snapshots\...` - `ipconfig`, routes, Wi-Fi interfaces/drivers/settings and adapter configuration at start, end, incidents and periodic checkpoints.
- `SHA256SUMS.txt` - SHA-256 checksums for generated evidence files.

## Default monitoring strategy

- Duration: **48 hours**
- Sampling interval: **5 seconds**
- Public ICMP targets:
  - `1.1.1.1`
  - `8.8.8.8`
  - `9.9.9.9`
- DNS test: `www.msftconnecttest.com`
- HTTP test: Windows NCSI-style probe:
  - `http://www.msftconnecttest.com/connecttest.txt`
  - expected response: `Microsoft Connect Test`
- DNS/HTTP deep checks: every 30 seconds during normal operation, immediately when connectivity looks suspicious.
- High-latency warning: 150 ms average across successful public ping targets.
- Sleep prevention: enabled by default while monitoring runs.

## Fastest start

Double-click:

`Start-48h-Monitor.bat`

The evidence folder is created on your Desktop:

`Desktop\InternetEvidence\Session_YYYYMMDD_HHMMSS`

Leave the PowerShell window open for the full test.

## Manual start

Open PowerShell in this folder:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\InternetEvidenceMonitor.ps1
```

The defaults are already configured for a 48-hour test.

### Explicit Wi-Fi adapter

If Windows does not choose the correct adapter automatically:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\InternetEvidenceMonitor.ps1 -AdapterName "Wi-Fi"
```

Find adapter names with:

```powershell
Get-NetAdapter
```

### Five-minute validation run

Before the real 48-hour test you can run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\InternetEvidenceMonitor.ps1 -DurationMinutes 5
```

Or double-click `Quick-Test.bat`.

### More aggressive sampling

For very short interruptions:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\InternetEvidenceMonitor.ps1 -IntervalSeconds 2 -PingTimeoutMs 700
```

A 5-second interval is the recommended balance for a 48-hour operator test. A 2-second interval catches shorter drops but creates more traffic and more rows.

### Disable sleep prevention

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\InternetEvidenceMonitor.ps1 -NoPreventSleep
```

If you disable sleep prevention, Windows sleep will appear as `MONITORING_GAP`, not as Internet downtime.

## If you stop it early

Press `Ctrl+C`.

The monitor writes data continuously, so already collected CSV evidence is preserved. The `finally` block also attempts to export Windows event logs and create the final report.

If Windows or the process is terminated so hard that the final report is not generated, rebuild it later:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\InternetEvidenceMonitor.ps1 `
  -ReportOnly `
  -SessionDirectory "C:\Users\YOUR_USER\Desktop\InternetEvidence\Session_YYYYMMDD_HHMMSS"
```

## Recommended conditions for an ISP test

- Connect the monitored PC directly to the operator's router Wi-Fi.
- Do not use a VPN during the test.
- Avoid switching between Wi-Fi networks.
- Keep the PC powered from AC.
- Leave the monitor running for the full 48 hours.
- Do not edit the evidence files before sending them.
- Preserve the entire original session directory.
- If possible, note the operator ticket number and test window separately.

For the strongest fault isolation, a second simultaneous test over Ethernet is useful. If Ethernet stays stable while Wi-Fi drops, the problem is likely Wi-Fi/router-side. If both fail at the same times, the case for an uplink/ISP interruption becomes much stronger.

## Privacy warning

The evidence package can contain:

- Wi-Fi/network profile name
- local/private IP addresses
- MAC address
- adapter/driver information
- Windows network event messages

Review the package before posting it publicly. For an ISP support ticket these details are usually useful.

## Evidence limitations

This tool is designed for technical troubleshooting and escalation. It is stronger than screenshots or a single continuous ping because it uses independent checks and Windows-generated event evidence, but it is **not** a trusted third-party measurement service, notarized timestamp, or ISP-signed record.

The SHA-256 file helps verify that the evidence package has not changed after you preserve/share that exact checksum, but by itself it does not prove who originally created the files.
