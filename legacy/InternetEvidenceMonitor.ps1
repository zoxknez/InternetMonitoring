#requires -Version 5.1
<#
.SYNOPSIS
    Internet Evidence Monitor for Windows - 48h Wi-Fi / Internet outage evidence collector.

.DESCRIPTION
    Continuously records:
      - Wi-Fi/network adapter state
      - Default gateway reachability
      - Multiple independent public IP ping targets
      - DNS resolution
      - Windows NCSI HTTP probe
      - Latency and packet-loss indicators
      - State transitions and confirmed incidents
      - Windows WLAN AutoConfig and related System events
      - Baseline network configuration snapshots
      - SHA-256 hashes of generated evidence files

    All logging is local. Loss of Internet connectivity does not stop the monitor.

.NOTES
    Core monitoring does not require Administrator privileges.
    Windows 10/11 + Windows PowerShell 5.1 or PowerShell 7 on Windows.
#>

[CmdletBinding()]
param(
    [ValidateRange(1, 168)]
    [int]$DurationHours = 48,

    [ValidateRange(0, 10080)]
    [int]$DurationMinutes = 0,

    [ValidateRange(1, 60)]
    [int]$IntervalSeconds = 5,

    [ValidateRange(100, 5000)]
    [int]$PingTimeoutMs = 1000,

    [ValidateRange(5, 300)]
    [int]$DeepCheckEverySeconds = 30,

    [ValidateRange(50, 5000)]
    [int]$HighLatencyMs = 150,

    [string[]]$PingTargets = @("1.1.1.1", "8.8.8.8", "9.9.9.9"),

    [string]$DnsTestName = "www.msftconnecttest.com",

    [string]$HttpProbeUrl = "http://www.msftconnecttest.com/connecttest.txt",

    [string]$ExpectedHttpText = "Microsoft Connect Test",

    [string]$AdapterName = "",

    [string]$OutputRoot = "$env:USERPROFILE\Desktop\InternetEvidence",

    [string]$SessionDirectory = "",

    [switch]$ReportOnly,

    [switch]$NoPreventSleep
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

# ----------------------------
# Helpers
# ----------------------------

function Get-NowPair {
    $local = [DateTimeOffset]::Now
    [pscustomobject]@{
        Local = $local
        Utc   = $local.ToUniversalTime()
    }
}

function Ensure-Directory {
    param([Parameter(Mandatory=$true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Write-Utf8Text {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][AllowEmptyString()][string]$Text
    )
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

function Append-CsvRow {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)]$Object
    )
    if (Test-Path -LiteralPath $Path) {
        $Object | Export-Csv -LiteralPath $Path -NoTypeInformation -Encoding UTF8 -Append
    } else {
        $Object | Export-Csv -LiteralPath $Path -NoTypeInformation -Encoding UTF8
    }
}

function ConvertTo-HtmlSafe {
    param([AllowNull()][object]$Value)
    if ($null -eq $Value) { return "" }
    return [System.Net.WebUtility]::HtmlEncode([string]$Value)
}

function Get-MonitorAdapter {
    param([string]$PreferredName)

    $all = @(Get-NetAdapter -ErrorAction SilentlyContinue)
    if ($all.Count -eq 0) {
        return $null
    }

    if (-not [string]::IsNullOrWhiteSpace($PreferredName)) {
        $preferred = $all | Where-Object { $_.Name -eq $PreferredName } | Select-Object -First 1
        if ($null -ne $preferred) { return $preferred }
    }

    $wifi = @(
        $all | Where-Object {
            $_.Name -match '(?i)wi-?fi|wlan|wireless' -or
            $_.InterfaceDescription -match '(?i)wireless|wi-?fi|wlan|802\.11'
        }
    )

    if ($wifi.Count -gt 0) {
        return $wifi |
            Sort-Object @{Expression={ if ($_.Status -eq "Up") { 0 } else { 1 } }}, ifIndex |
            Select-Object -First 1
    }

    # Fallback: adapter used by the best IPv4 default route.
    $route = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue |
        Where-Object { $_.NextHop -ne "0.0.0.0" } |
        Sort-Object RouteMetric |
        Select-Object -First 1

    if ($null -ne $route) {
        return $all | Where-Object { $_.ifIndex -eq $route.InterfaceIndex } | Select-Object -First 1
    }

    return $all | Where-Object { $_.Status -eq "Up" } | Select-Object -First 1
}

function Get-DefaultGateway {
    param([int]$InterfaceIndex)

    $route = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix "0.0.0.0/0" -InterfaceIndex $InterfaceIndex -ErrorAction SilentlyContinue |
        Where-Object { $_.NextHop -and $_.NextHop -ne "0.0.0.0" } |
        Sort-Object RouteMetric |
        Select-Object -First 1

    if ($null -eq $route) { return "" }
    return [string]$route.NextHop
}

function Get-ConnectionProfileInfo {
    param([int]$InterfaceIndex)
    try {
        $p = Get-NetConnectionProfile -InterfaceIndex $InterfaceIndex -ErrorAction Stop | Select-Object -First 1
        if ($null -eq $p) { throw "No connection profile" }
        return [pscustomobject]@{
            Name             = [string]$p.Name
            NetworkCategory  = [string]$p.NetworkCategory
            IPv4Connectivity = [string]$p.IPv4Connectivity
            IPv6Connectivity = [string]$p.IPv6Connectivity
        }
    } catch {
        return [pscustomobject]@{
            Name             = ""
            NetworkCategory  = ""
            IPv4Connectivity = ""
            IPv6Connectivity = ""
        }
    }
}

function Invoke-PingCheck {
    param(
        [Parameter(Mandatory=$true)][string]$Target,
        [Parameter(Mandatory=$true)][int]$TimeoutMs
    )

    $pinger = New-Object System.Net.NetworkInformation.Ping
    try {
        $reply = $pinger.Send($Target, $TimeoutMs)
        if ($reply.Status -eq [System.Net.NetworkInformation.IPStatus]::Success) {
            return [pscustomobject]@{
                Target  = $Target
                Success = $true
                RttMs   = [int64]$reply.RoundtripTime
                Status  = "Success"
            }
        }
        return [pscustomobject]@{
            Target  = $Target
            Success = $false
            RttMs   = $null
            Status  = [string]$reply.Status
        }
    } catch {
        return [pscustomobject]@{
            Target  = $Target
            Success = $false
            RttMs   = $null
            Status  = $_.Exception.GetType().Name + ": " + $_.Exception.Message
        }
    } finally {
        $pinger.Dispose()
    }
}

function Invoke-DnsCheck {
    param([Parameter(Mandatory=$true)][string]$Name)

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        if (Get-Command Resolve-DnsName -ErrorAction SilentlyContinue) {
            $records = @(Resolve-DnsName -Name $Name -Type A -DnsOnly -QuickTimeout -ErrorAction Stop)
            $ok = ($records | Where-Object { $_.IPAddress } | Measure-Object).Count -gt 0
        } else {
            $addresses = [System.Net.Dns]::GetHostAddresses($Name)
            $ok = $addresses.Count -gt 0
        }
        $sw.Stop()
        return [pscustomobject]@{
            Success = [bool]$ok
            Ms      = [int64]$sw.ElapsedMilliseconds
            Error   = if ($ok) { "" } else { "No A records returned" }
        }
    } catch {
        $sw.Stop()
        return [pscustomobject]@{
            Success = $false
            Ms      = [int64]$sw.ElapsedMilliseconds
            Error   = $_.Exception.GetType().Name + ": " + $_.Exception.Message
        }
    }
}

function Invoke-HttpProbe {
    param(
        [Parameter(Mandatory=$true)][string]$Url,
        [string]$ExpectedText
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $params = @{
            Uri         = $Url
            Method      = "Get"
            TimeoutSec  = 4
            ErrorAction = "Stop"
        }

        if ($PSVersionTable.PSVersion.Major -le 5) {
            $params["UseBasicParsing"] = $true
        }

        $response = Invoke-WebRequest @params
        $content = [string]$response.Content
        $statusCode = 0
        try { $statusCode = [int]$response.StatusCode } catch { $statusCode = 200 }

        $contentOk = $true
        if (-not [string]::IsNullOrWhiteSpace($ExpectedText)) {
            $contentOk = $content -match [regex]::Escape($ExpectedText)
        }

        $sw.Stop()
        return [pscustomobject]@{
            Success    = ($statusCode -ge 200 -and $statusCode -lt 400 -and $contentOk)
            Ms         = [int64]$sw.ElapsedMilliseconds
            StatusCode = $statusCode
            Error      = if ($contentOk) { "" } else { "Unexpected probe content" }
        }
    } catch {
        $sw.Stop()
        return [pscustomobject]@{
            Success    = $false
            Ms         = [int64]$sw.ElapsedMilliseconds
            StatusCode = 0
            Error      = $_.Exception.GetType().Name + ": " + $_.Exception.Message
        }
    }
}

function Get-StateClassification {
    param(
        [string]$AdapterStatus,
        [bool]$GatewayConfigured,
        [bool]$GatewaySuccess,
        [int]$InternetSuccessCount,
        [int]$InternetTargetCount,
        [bool]$DnsSuccess,
        [bool]$HttpSuccess,
        [double]$AverageRttMs,
        [int]$HighLatencyThresholdMs,
        [int]$DnsFailureStreak
    )

    if ($AdapterStatus -ne "Up") {
        return [pscustomobject]@{ State="WIFI_LINK_DOWN"; Severity="OUTAGE"; Reason="Network adapter is not Up" }
    }

    if ($GatewayConfigured -and -not $GatewaySuccess -and $InternetSuccessCount -eq 0 -and -not $HttpSuccess) {
        return [pscustomobject]@{ State="LOCAL_GATEWAY_DOWN"; Severity="OUTAGE"; Reason="Adapter is Up, but gateway and all external checks failed" }
    }

    if ($InternetSuccessCount -eq 0) {
        if ($HttpSuccess) {
            return [pscustomobject]@{ State="ICMP_FILTERED_OR_UNRESPONSIVE"; Severity="DEGRADED"; Reason="All ICMP targets failed while HTTP still worked" }
        }
        return [pscustomobject]@{ State="INTERNET_DOWN"; Severity="OUTAGE"; Reason="All public IP targets and HTTP probe failed" }
    }

    if (-not $DnsSuccess) {
        if ($DnsFailureStreak -ge 2) {
            return [pscustomobject]@{ State="DNS_FAILURE"; Severity="OUTAGE"; Reason="IP connectivity works but DNS failed on consecutive checks" }
        }
        return [pscustomobject]@{ State="DNS_FAILURE_SUSPECTED"; Severity="DEGRADED"; Reason="IP connectivity works but DNS failed once" }
    }

    if ($InternetSuccessCount -lt $InternetTargetCount) {
        return [pscustomobject]@{ State="PARTIAL_PACKET_LOSS"; Severity="DEGRADED"; Reason="One or more independent public IP targets failed" }
    }

    if (-not $HttpSuccess) {
        return [pscustomobject]@{ State="HTTP_PROBE_FAILURE"; Severity="DEGRADED"; Reason="ICMP and DNS work, but Windows NCSI-style HTTP probe failed" }
    }

    if ($AverageRttMs -ge $HighLatencyThresholdMs) {
        return [pscustomobject]@{ State="HIGH_LATENCY"; Severity="DEGRADED"; Reason="Average external latency exceeded configured threshold" }
    }

    if ($GatewayConfigured -and -not $GatewaySuccess) {
        return [pscustomobject]@{ State="GATEWAY_ICMP_UNRESPONSIVE"; Severity="INFO"; Reason="Internet works, but local gateway did not answer ICMP" }
    }

    return [pscustomobject]@{ State="OK"; Severity="OK"; Reason="Connectivity checks passed" }
}

function Get-WorstState {
    param([string[]]$States)

    $rank = @{
        "WIFI_LINK_DOWN"               = 100
        "LOCAL_GATEWAY_DOWN"           = 95
        "INTERNET_DOWN"                = 90
        "DNS_FAILURE"                  = 80
        "PARTIAL_PACKET_LOSS"          = 60
        "HIGH_LATENCY"                 = 50
        "HTTP_PROBE_FAILURE"           = 40
        "DNS_FAILURE_SUSPECTED"        = 35
        "ICMP_FILTERED_OR_UNRESPONSIVE"= 30
        "GATEWAY_ICMP_UNRESPONSIVE"    = 10
        "OK"                           = 0
    }

    $best = "OK"
    $bestRank = -1
    foreach ($s in $States) {
        $r = 0
        if ($rank.ContainsKey($s)) { $r = [int]$rank[$s] }
        if ($r -gt $bestRank) {
            $bestRank = $r
            $best = $s
        }
    }
    return $best
}

function Write-NetworkSnapshot {
    param(
        [Parameter(Mandatory=$true)][string]$Directory,
        [Parameter(Mandatory=$true)][string]$Label
    )

    $stamp = (Get-Date).ToString("yyyyMMdd_HHmmss")
    $dir = Join-Path $Directory ("snapshots\" + $stamp + "_" + $Label)
    Ensure-Directory $dir

    try { ipconfig /all | Out-File -LiteralPath (Join-Path $dir "ipconfig-all.txt") -Encoding utf8 -Width 300 } catch {}
    try { route print -4 | Out-File -LiteralPath (Join-Path $dir "route-print-ipv4.txt") -Encoding utf8 -Width 300 } catch {}
    try { netsh wlan show interfaces | Out-File -LiteralPath (Join-Path $dir "netsh-wlan-interfaces.txt") -Encoding utf8 -Width 300 } catch {}
    try { netsh wlan show drivers | Out-File -LiteralPath (Join-Path $dir "netsh-wlan-drivers.txt") -Encoding utf8 -Width 300 } catch {}
    try { netsh wlan show settings | Out-File -LiteralPath (Join-Path $dir "netsh-wlan-settings.txt") -Encoding utf8 -Width 300 } catch {}
    try {
        Get-NetAdapter -ErrorAction SilentlyContinue |
            Select-Object Name, InterfaceDescription, ifIndex, Status, LinkSpeed, MacAddress |
            Export-Csv -LiteralPath (Join-Path $dir "net-adapters.csv") -NoTypeInformation -Encoding UTF8
    } catch {}
    try {
        Get-NetIPConfiguration -ErrorAction SilentlyContinue |
            Format-List * |
            Out-File -LiteralPath (Join-Path $dir "net-ip-configuration.txt") -Encoding utf8 -Width 300
    } catch {}
}

function Export-WindowsNetworkEvents {
    param(
        [Parameter(Mandatory=$true)][datetime]$StartTime,
        [Parameter(Mandatory=$true)][datetime]$EndTime,
        [Parameter(Mandatory=$true)][string]$Directory
    )

    $wlanCsv = Join-Path $Directory "windows-wlan-events.csv"
    $systemCsv = Join-Path $Directory "windows-network-system-events.csv"

    try {
        Get-WinEvent -FilterHashtable @{
            LogName   = "Microsoft-Windows-WLAN-AutoConfig/Operational"
            StartTime = $StartTime
            EndTime   = $EndTime
        } -ErrorAction Stop |
        Select-Object TimeCreated, Id, LevelDisplayName, ProviderName, Message |
        Export-Csv -LiteralPath $wlanCsv -NoTypeInformation -Encoding UTF8
    } catch {
        Write-Utf8Text -Path (Join-Path $Directory "windows-wlan-events-error.txt") -Text $_.Exception.Message
    }

    try {
        Get-WinEvent -FilterHashtable @{
            LogName   = "System"
            StartTime = $StartTime
            EndTime   = $EndTime
        } -ErrorAction Stop |
        Where-Object {
            $_.ProviderName -match '(?i)wlan|wifi|wireless|tcpip|dhcp|ndis|netwtw|network|kernel-pnp'
        } |
        Select-Object TimeCreated, Id, LevelDisplayName, ProviderName, Message |
        Export-Csv -LiteralPath $systemCsv -NoTypeInformation -Encoding UTF8
    } catch {
        Write-Utf8Text -Path (Join-Path $Directory "windows-network-system-events-error.txt") -Text $_.Exception.Message
    }
}

function Try-GenerateNativeWlanReport {
    param([Parameter(Mandatory=$true)][string]$Directory)

    try {
        $output = netsh wlan show wlanreport 2>&1 | Out-String
        Write-Utf8Text -Path (Join-Path $Directory "native-wlan-report-command.txt") -Text $output

        $latest = "C:\ProgramData\Microsoft\Windows\WlanReport\wlan-report-latest.html"
        if (Test-Path -LiteralPath $latest) {
            $reportFile = Get-Item -LiteralPath $latest -ErrorAction SilentlyContinue
            if ($null -ne $reportFile -and $reportFile.LastWriteTime -ge (Get-Date).AddMinutes(-10)) {
                Copy-Item -LiteralPath $latest -Destination (Join-Path $Directory "windows-native-wlan-report.html") -Force
            }
        }
    } catch {
        Write-Utf8Text -Path (Join-Path $Directory "native-wlan-report-error.txt") -Text $_.Exception.Message
    }
}

function New-EvidenceReport {
    param([Parameter(Mandatory=$true)][string]$Directory)

    $samplesPath = Join-Path $Directory "samples.csv"
    $incidentsPath = Join-Path $Directory "incidents.csv"
    $transitionsPath = Join-Path $Directory "state-transitions.csv"
    $metaPath = Join-Path $Directory "session-meta.json"
    $reportPath = Join-Path $Directory "REPORT.html"
    $summaryPath = Join-Path $Directory "SUMMARY.txt"

    $samples = @()
    if (Test-Path -LiteralPath $samplesPath) { $samples = @(Import-Csv -LiteralPath $samplesPath) }

    $incidents = @()
    if (Test-Path -LiteralPath $incidentsPath) { $incidents = @(Import-Csv -LiteralPath $incidentsPath) }

    $transitions = @()
    if (Test-Path -LiteralPath $transitionsPath) { $transitions = @(Import-Csv -LiteralPath $transitionsPath) }

    $meta = $null
    if (Test-Path -LiteralPath $metaPath) {
        try { $meta = Get-Content -LiteralPath $metaPath -Raw | ConvertFrom-Json } catch {}
    }

    $totalSamples = $samples.Count
    $outageStates = @("WIFI_LINK_DOWN", "LOCAL_GATEWAY_DOWN", "INTERNET_DOWN", "DNS_FAILURE")
    $degradedStates = @("PARTIAL_PACKET_LOSS", "HIGH_LATENCY", "HTTP_PROBE_FAILURE", "DNS_FAILURE_SUSPECTED", "ICMP_FILTERED_OR_UNRESPONSIVE")

    $outageSamples = @($samples | Where-Object { $outageStates -contains $_.State }).Count
    $degradedSamples = @($samples | Where-Object { $degradedStates -contains $_.State }).Count
    $monitoringGaps = @($transitions | Where-Object { $_.State -eq "MONITORING_GAP" }).Count

    $downtimeSec = 0.0
    foreach ($i in $incidents) {
        $v = 0.0
        if ([double]::TryParse([string]$i.DurationSeconds, [ref]$v)) { $downtimeSec += $v }
    }

    $durationSec = 0.0
    if ($samples.Count -ge 2) {
        try {
            $first = [DateTimeOffset]::Parse($samples[0].TimestampLocal)
            $last = [DateTimeOffset]::Parse($samples[-1].TimestampLocal)
            $durationSec = [Math]::Max(0, ($last - $first).TotalSeconds)
        } catch {}
    }

    $availability = 100.0
    if ($durationSec -gt 0) {
        $availability = [Math]::Max(0, 100.0 * (($durationSec - $downtimeSec) / $durationSec))
    }

    $latencies = @()
    foreach ($s in $samples) {
        $d = 0.0
        if ([double]::TryParse([string]$s.AvgInternetRttMs, [ref]$d)) { $latencies += $d }
    }

    $avgLatency = if ($latencies.Count -gt 0) { [Math]::Round(($latencies | Measure-Object -Average).Average, 2) } else { 0 }
    $maxLatency = if ($latencies.Count -gt 0) { [Math]::Round(($latencies | Measure-Object -Maximum).Maximum, 2) } else { 0 }

    $lossValues = @()
    foreach ($s in $samples) {
        $d = 0.0
        if ([double]::TryParse([string]$s.SamplePacketLossPct, [ref]$d)) { $lossValues += $d }
    }
    $avgSampleLoss = if ($lossValues.Count -gt 0) { [Math]::Round(($lossValues | Measure-Object -Average).Average, 3) } else { 0 }

    $startText = if ($meta -and $meta.StartLocal) { [string]$meta.StartLocal } elseif ($samples.Count -gt 0) { $samples[0].TimestampLocal } else { "" }
    $endText = if ($samples.Count -gt 0) { $samples[-1].TimestampLocal } else { "" }

    $summary = @"
Internet Evidence Monitor - SUMMARY

Session directory: $Directory
Start: $startText
Last sample: $endText
Samples: $totalSamples
Confirmed outage incidents: $($incidents.Count)
Outage samples: $outageSamples
Degraded samples: $degradedSamples
Monitoring gaps: $monitoringGaps
Confirmed downtime: $([Math]::Round($downtimeSec, 2)) seconds
Measured availability: $([Math]::Round($availability, 5)) %
Average external latency: $avgLatency ms
Maximum external average latency: $maxLatency ms
Average per-sample target loss: $avgSampleLoss %

Interpretation:
- WIFI_LINK_DOWN: Windows reports monitored adapter not Up.
- LOCAL_GATEWAY_DOWN: adapter Up, but gateway + all external checks fail.
- INTERNET_DOWN: all independent public IP targets + HTTP probe fail.
- DNS_FAILURE: IP connectivity works, DNS fails on consecutive checks.
- MONITORING_GAP: PC/script did not sample for an abnormally long interval. This is NOT counted as ISP downtime.
"@
    Write-Utf8Text -Path $summaryPath -Text $summary

    $incidentRows = ""
    if ($incidents.Count -eq 0) {
        $incidentRows = '<tr><td colspan="7">No confirmed outage incidents were recorded.</td></tr>'
    } else {
        foreach ($i in $incidents) {
            $incidentRows += "<tr>" +
                "<td>$(ConvertTo-HtmlSafe $i.StartLocal)</td>" +
                "<td>$(ConvertTo-HtmlSafe $i.EndLocal)</td>" +
                "<td>$(ConvertTo-HtmlSafe $i.DurationSeconds)</td>" +
                "<td><strong>$(ConvertTo-HtmlSafe $i.WorstState)</strong></td>" +
                "<td>$(ConvertTo-HtmlSafe $i.StatesSeen)</td>" +
                "<td>$(ConvertTo-HtmlSafe $i.StartReason)</td>" +
                "<td>$(ConvertTo-HtmlSafe $i.SampleCount)</td>" +
                "</tr>"
        }
    }

    $transitionRows = ""
    $lastTransitions = @($transitions | Select-Object -Last 100)
    if ($lastTransitions.Count -eq 0) {
        $transitionRows = '<tr><td colspan="5">No transitions recorded.</td></tr>'
    } else {
        foreach ($t in $lastTransitions) {
            $transitionRows += "<tr>" +
                "<td>$(ConvertTo-HtmlSafe $t.TimestampLocal)</td>" +
                "<td>$(ConvertTo-HtmlSafe $t.State)</td>" +
                "<td>$(ConvertTo-HtmlSafe $t.Severity)</td>" +
                "<td>$(ConvertTo-HtmlSafe $t.Reason)</td>" +
                "<td>$(ConvertTo-HtmlSafe $t.Details)</td>" +
                "</tr>"
        }
    }

    $html = @"
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Internet Evidence Report</title>
<style>
body { font-family: Segoe UI, Arial, sans-serif; margin: 28px; color: #1d1d1f; }
h1,h2 { margin-bottom: 8px; }
.grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(180px,1fr)); gap:12px; margin:18px 0 28px; }
.card { border:1px solid #d9d9de; border-radius:10px; padding:14px; }
.value { font-size:24px; font-weight:700; margin-top:5px; }
table { width:100%; border-collapse:collapse; margin:12px 0 28px; font-size:13px; }
th,td { border:1px solid #ddd; padding:8px; text-align:left; vertical-align:top; }
th { background:#f4f4f6; }
.note { background:#f7f7f8; border-left:4px solid #777; padding:12px; margin:16px 0; }
.small { color:#666; font-size:12px; }
code { background:#f4f4f6; padding:2px 4px; border-radius:4px; }
</style>
</head>
<body>
<h1>Internet Evidence Monitor</h1>
<div class="small">Generated: $(ConvertTo-HtmlSafe ([DateTimeOffset]::Now.ToString("o")))</div>

<div class="grid">
  <div class="card"><div>Samples</div><div class="value">$totalSamples</div></div>
  <div class="card"><div>Confirmed incidents</div><div class="value">$($incidents.Count)</div></div>
  <div class="card"><div>Confirmed downtime</div><div class="value">$([Math]::Round($downtimeSec,1)) s</div></div>
  <div class="card"><div>Measured availability</div><div class="value">$([Math]::Round($availability,4))%</div></div>
  <div class="card"><div>Average latency</div><div class="value">$avgLatency ms</div></div>
  <div class="card"><div>Max avg latency</div><div class="value">$maxLatency ms</div></div>
  <div class="card"><div>Average target loss</div><div class="value">$avgSampleLoss%</div></div>
  <div class="card"><div>Monitoring gaps</div><div class="value">$monitoringGaps</div></div>
</div>

<div class="note">
<strong>Evidence model:</strong> a confirmed Internet outage requires corroboration from multiple independent public IP targets and the HTTP probe.
A Wi-Fi/link failure and a local-gateway failure are classified separately. A monitoring gap caused by sleep, shutdown, reboot, or script interruption is never counted as ISP downtime.
</div>

<h2>Confirmed outage incidents</h2>
<table>
<thead><tr><th>Start</th><th>End</th><th>Duration s</th><th>Worst state</th><th>States seen</th><th>Reason</th><th>Samples</th></tr></thead>
<tbody>$incidentRows</tbody>
</table>

<h2>Last 100 state transitions / monitor events</h2>
<table>
<thead><tr><th>Timestamp</th><th>State</th><th>Severity</th><th>Reason</th><th>Details</th></tr></thead>
<tbody>$transitionRows</tbody>
</table>

<h2>Files to preserve</h2>
<p>
<code>samples.csv</code> - every measurement;
<code>incidents.csv</code> - confirmed outage windows;
<code>state-transitions.csv</code> - state changes and monitoring gaps;
<code>windows-wlan-events.csv</code> - Windows WLAN AutoConfig evidence;
<code>windows-network-system-events.csv</code> - related Windows System events;
<code>windows-native-wlan-report.html</code> - native Windows WLAN report when available;
<code>SHA256SUMS.txt</code> - integrity hashes.
</p>

<p class="small">This report is technical evidence for troubleshooting and escalation. It is not a trusted third-party timestamp or a cryptographic signature by the ISP.</p>
</body>
</html>
"@

    Write-Utf8Text -Path $reportPath -Text $html
}

function Write-Hashes {
    param([Parameter(Mandatory=$true)][string]$Directory)

    $hashPath = Join-Path $Directory "SHA256SUMS.txt"
    $files = Get-ChildItem -LiteralPath $Directory -File -Recurse |
        Where-Object { $_.FullName -ne $hashPath } |
        Sort-Object FullName

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($f in $files) {
        try {
            $h = Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256
            $relative = $f.FullName.Substring($Directory.Length).TrimStart('\')
            $lines.Add("$($h.Hash)  $relative")
        } catch {}
    }
    Write-Utf8Text -Path $hashPath -Text ($lines -join [Environment]::NewLine)
}

function Enable-SleepPrevention {
    if ("SleepPreventer" -as [type]) { return }
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SleepPreventer {
    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint esFlags);
}
"@
    # ES_CONTINUOUS | ES_SYSTEM_REQUIRED
    [void][SleepPreventer]::SetThreadExecutionState([uint32]2147483649)
}

function Disable-SleepPrevention {
    if ("SleepPreventer" -as [type]) {
        # ES_CONTINUOUS only - clear previous requirements.
        [void][SleepPreventer]::SetThreadExecutionState([uint32]2147483648)
    }
}

# ----------------------------
# Report-only mode
# ----------------------------

if ($ReportOnly) {
    if ([string]::IsNullOrWhiteSpace($SessionDirectory) -or -not (Test-Path -LiteralPath $SessionDirectory)) {
        throw "Use -ReportOnly -SessionDirectory <existing session folder>."
    }

    New-EvidenceReport -Directory $SessionDirectory
    Write-Hashes -Directory $SessionDirectory
    Write-Host "Report rebuilt:" -ForegroundColor Green
    Write-Host (Join-Path $SessionDirectory "REPORT.html")
    exit 0
}

# ----------------------------
# Session initialization
# ----------------------------

Ensure-Directory $OutputRoot

$startPair = Get-NowPair
if ([string]::IsNullOrWhiteSpace($SessionDirectory)) {
    $sessionName = "Session_" + $startPair.Local.ToString("yyyyMMdd_HHmmss")
    $SessionDirectory = Join-Path $OutputRoot $sessionName
}
Ensure-Directory $SessionDirectory
Ensure-Directory (Join-Path $SessionDirectory "snapshots")

$samplesCsv = Join-Path $SessionDirectory "samples.csv"
$transitionsCsv = Join-Path $SessionDirectory "state-transitions.csv"
$incidentsCsv = Join-Path $SessionDirectory "incidents.csv"
$metaPath = Join-Path $SessionDirectory "session-meta.json"
$runtimeStatePath = Join-Path $SessionDirectory "runtime-state.json"

$endUtc = if ($DurationMinutes -gt 0) { $startPair.Utc.AddMinutes($DurationMinutes) } else { $startPair.Utc.AddHours($DurationHours) }
$meta = [ordered]@{
    ToolVersion               = "1.0.0"
    StartLocal                = $startPair.Local.ToString("o")
    StartUtc                  = $startPair.Utc.ToString("o")
    PlannedEndUtc             = $endUtc.ToString("o")
    DurationHours             = $DurationHours
    DurationMinutesOverride     = $DurationMinutes
    IntervalSeconds           = $IntervalSeconds
    PingTimeoutMs             = $PingTimeoutMs
    DeepCheckEverySeconds     = $DeepCheckEverySeconds
    HighLatencyMs             = $HighLatencyMs
    PingTargets               = $PingTargets
    DnsTestName               = $DnsTestName
    HttpProbeUrl              = $HttpProbeUrl
    AdapterNameRequested      = $AdapterName
    ComputerName              = $env:COMPUTERNAME
    UserName                  = $env:USERNAME
    PowerShellVersion         = $PSVersionTable.PSVersion.ToString()
    PreventSleep              = (-not $NoPreventSleep.IsPresent)
}
Write-Utf8Text -Path $metaPath -Text ($meta | ConvertTo-Json -Depth 5)

Write-NetworkSnapshot -Directory $SessionDirectory -Label "START"

$adapter = Get-MonitorAdapter -PreferredName $AdapterName
if ($null -eq $adapter) {
    throw "No network adapter could be detected. Use -AdapterName 'Wi-Fi' if needed."
}

$selectedAdapterName = [string]$adapter.Name
$selectedIfIndex = [int]$adapter.ifIndex
$gateway = Get-DefaultGateway -InterfaceIndex $selectedIfIndex

Append-CsvRow -Path $transitionsCsv -Object ([pscustomobject]@{
    TimestampLocal = $startPair.Local.ToString("o")
    TimestampUtc   = $startPair.Utc.ToString("o")
    State          = "MONITOR_STARTED"
    Severity       = "INFO"
    Reason         = "Monitoring session started"
    Details        = "Adapter=$selectedAdapterName; ifIndex=$selectedIfIndex; Gateway=$gateway; PlannedEndUtc=$($endUtc.ToString('o')); IntervalSeconds=$IntervalSeconds"
})

if (-not $NoPreventSleep) {
    try { Enable-SleepPrevention } catch {
        Append-CsvRow -Path $transitionsCsv -Object ([pscustomobject]@{
            TimestampLocal = [DateTimeOffset]::Now.ToString("o")
            TimestampUtc   = [DateTimeOffset]::UtcNow.ToString("o")
            State          = "SLEEP_PREVENTION_FAILED"
            Severity       = "INFO"
            Reason         = "Could not request Windows to keep system awake"
            Details        = $_.Exception.Message
        })
    }
}

Write-Host ""
Write-Host "Internet Evidence Monitor 1.0" -ForegroundColor Cyan
Write-Host "Session: $SessionDirectory"
Write-Host "Adapter: $selectedAdapterName (ifIndex $selectedIfIndex)"
Write-Host "Gateway: $gateway"
Write-Host "Target end: $($endUtc.ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss zzz'))"
Write-Host "Press Ctrl+C to stop early. Existing logs remain valid."
Write-Host ""

# ----------------------------
# Runtime state
# ----------------------------

$seq = 0
$lastSampleUtc = $null
$lastDeepCheckUtc = [DateTimeOffset]::MinValue
$lastDns = [pscustomobject]@{ Success=$true; Ms=0; Error="" }
$lastHttp = [pscustomobject]@{ Success=$true; Ms=0; StatusCode=0; Error="" }
$previousState = ""
$previousSeverity = ""
$currentIncident = $null
$dnsFailureStreak = 0
$lastSnapshotUtc = $startPair.Utc
$lastRuntimeStateWriteUtc = [DateTimeOffset]::MinValue
$outageStates = @("WIFI_LINK_DOWN", "LOCAL_GATEWAY_DOWN", "INTERNET_DOWN", "DNS_FAILURE")

try {
    while ([DateTimeOffset]::UtcNow -lt $endUtc) {
        $loopStart = [DateTimeOffset]::UtcNow
        $now = Get-NowPair
        $seq++

        $gapSec = 0.0
        if ($null -ne $lastSampleUtc) {
            $gapSec = ($now.Utc - $lastSampleUtc).TotalSeconds
            if ($gapSec -gt [Math]::Max(($IntervalSeconds * 3), 20)) {
                Append-CsvRow -Path $transitionsCsv -Object ([pscustomobject]@{
                    TimestampLocal = $now.Local.ToString("o")
                    TimestampUtc   = $now.Utc.ToString("o")
                    State          = "MONITORING_GAP"
                    Severity       = "INFO"
                    Reason         = "Sampling gap exceeded threshold; not counted as ISP downtime"
                    Details        = "GapSeconds=$([Math]::Round($gapSec,2)); ExpectedIntervalSeconds=$IntervalSeconds"
                })
            }
        }
        $lastSampleUtc = $now.Utc

        # Refresh adapter each loop so disconnect/reconnect state is captured.
        try {
            $adapter = Get-NetAdapter -InterfaceIndex $selectedIfIndex -ErrorAction Stop
        } catch {
            $adapter = Get-MonitorAdapter -PreferredName $selectedAdapterName
            if ($null -ne $adapter) {
                $selectedIfIndex = [int]$adapter.ifIndex
            }
        }

        if ($null -eq $adapter) {
            $adapterStatus = "Missing"
            $linkSpeed = ""
        } else {
            $adapterStatus = [string]$adapter.Status
            $linkSpeed = [string]$adapter.LinkSpeed
        }

        $profile = if ($null -ne $adapter) { Get-ConnectionProfileInfo -InterfaceIndex $selectedIfIndex } else {
            [pscustomobject]@{ Name=""; NetworkCategory=""; IPv4Connectivity=""; IPv6Connectivity="" }
        }

        $gateway = if ($null -ne $adapter) { Get-DefaultGateway -InterfaceIndex $selectedIfIndex } else { "" }
        $gatewayConfigured = -not [string]::IsNullOrWhiteSpace($gateway)
        $gatewayCheck = if ($gatewayConfigured) {
            Invoke-PingCheck -Target $gateway -TimeoutMs $PingTimeoutMs
        } else {
            [pscustomobject]@{ Target=""; Success=$false; RttMs=$null; Status="NoGateway" }
        }

        $internetChecks = New-Object System.Collections.Generic.List[object]
        foreach ($target in $PingTargets) {
            $internetChecks.Add((Invoke-PingCheck -Target $target -TimeoutMs $PingTimeoutMs))
        }

        $internetSuccessCount = @($internetChecks | Where-Object { $_.Success }).Count
        $successfulRtts = @($internetChecks | Where-Object { $_.Success -and $null -ne $_.RttMs } | ForEach-Object { [double]$_.RttMs })
        $avgRtt = if ($successfulRtts.Count -gt 0) {
            [Math]::Round(($successfulRtts | Measure-Object -Average).Average, 2)
        } else { 0.0 }

        $packetLossPct = if ($PingTargets.Count -gt 0) {
            [Math]::Round(100.0 * (($PingTargets.Count - $internetSuccessCount) / [double]$PingTargets.Count), 2)
        } else { 100.0 }

        $mustDeepCheck = (($now.Utc - $lastDeepCheckUtc).TotalSeconds -ge $DeepCheckEverySeconds) -or
                         ($internetSuccessCount -lt $PingTargets.Count) -or
                         (-not $gatewayCheck.Success) -or
                         (-not $lastDns.Success) -or
                         (-not $lastHttp.Success)

        $deepCheckRan = $false
        if ($mustDeepCheck) {
            $deepCheckRan = $true
            $lastDns = Invoke-DnsCheck -Name $DnsTestName
            $lastHttp = Invoke-HttpProbe -Url $HttpProbeUrl -ExpectedText $ExpectedHttpText
            $lastDeepCheckUtc = $now.Utc

            if ($lastDns.Success) {
                $dnsFailureStreak = 0
            } else {
                $dnsFailureStreak++
            }
        }

        $classification = Get-StateClassification `
            -AdapterStatus $adapterStatus `
            -GatewayConfigured $gatewayConfigured `
            -GatewaySuccess ([bool]$gatewayCheck.Success) `
            -InternetSuccessCount $internetSuccessCount `
            -InternetTargetCount $PingTargets.Count `
            -DnsSuccess ([bool]$lastDns.Success) `
            -HttpSuccess ([bool]$lastHttp.Success) `
            -AverageRttMs $avgRtt `
            -HighLatencyThresholdMs $HighLatencyMs `
            -DnsFailureStreak $dnsFailureStreak

        $pingSummary = ($internetChecks | ForEach-Object {
            if ($_.Success) { "$($_.Target)=OK:$($_.RttMs)ms" } else { "$($_.Target)=FAIL:$($_.Status)" }
        }) -join "; "

        $sample = [pscustomobject]@{
            Sequence                = $seq
            TimestampLocal          = $now.Local.ToString("o")
            TimestampUtc            = $now.Utc.ToString("o")
            SampleGapSeconds        = [Math]::Round($gapSec, 2)
            AdapterName             = $selectedAdapterName
            AdapterIfIndex          = $selectedIfIndex
            AdapterStatus           = $adapterStatus
            LinkSpeed               = $linkSpeed
            NetworkProfile          = $profile.Name
            IPv4Connectivity        = $profile.IPv4Connectivity
            Gateway                 = $gateway
            GatewaySuccess          = [bool]$gatewayCheck.Success
            GatewayRttMs            = if ($null -ne $gatewayCheck.RttMs) { $gatewayCheck.RttMs } else { "" }
            GatewayStatus           = $gatewayCheck.Status
            InternetSuccessCount    = $internetSuccessCount
            InternetTargetCount     = $PingTargets.Count
            SamplePacketLossPct     = $packetLossPct
            AvgInternetRttMs        = if ($successfulRtts.Count -gt 0) { $avgRtt } else { "" }
            InternetPingDetails     = $pingSummary
            DeepCheckRan            = $deepCheckRan
            DnsSuccess              = [bool]$lastDns.Success
            DnsMs                   = $lastDns.Ms
            DnsError                = $lastDns.Error
            HttpSuccess             = [bool]$lastHttp.Success
            HttpMs                  = $lastHttp.Ms
            HttpStatusCode          = $lastHttp.StatusCode
            HttpError               = $lastHttp.Error
            State                   = $classification.State
            Severity                = $classification.Severity
            Reason                  = $classification.Reason
        }
        Append-CsvRow -Path $samplesCsv -Object $sample

        # State transitions.
        if ($classification.State -ne $previousState -or $classification.Severity -ne $previousSeverity) {
            Append-CsvRow -Path $transitionsCsv -Object ([pscustomobject]@{
                TimestampLocal = $now.Local.ToString("o")
                TimestampUtc   = $now.Utc.ToString("o")
                State          = $classification.State
                Severity       = $classification.Severity
                Reason         = $classification.Reason
                Details        = "Adapter=$adapterStatus; Gateway=$gateway/$($gatewayCheck.Success); Internet=$internetSuccessCount/$($PingTargets.Count); DNS=$($lastDns.Success); HTTP=$($lastHttp.Success); AvgRttMs=$avgRtt; LossPct=$packetLossPct"
            })

            if ($classification.Severity -eq "OUTAGE") {
                Write-Host "[$($now.Local.ToString('yyyy-MM-dd HH:mm:ss'))] OUTAGE: $($classification.State) - $($classification.Reason)" -ForegroundColor Red
                Write-NetworkSnapshot -Directory $SessionDirectory -Label $classification.State
            } elseif ($classification.Severity -eq "DEGRADED") {
                Write-Host "[$($now.Local.ToString('yyyy-MM-dd HH:mm:ss'))] DEGRADED: $($classification.State) - $($classification.Reason)" -ForegroundColor Yellow
            } elseif ($previousSeverity -eq "OUTAGE" -and $classification.State -eq "OK") {
                Write-Host "[$($now.Local.ToString('yyyy-MM-dd HH:mm:ss'))] RECOVERED" -ForegroundColor Green
                Write-NetworkSnapshot -Directory $SessionDirectory -Label "RECOVERED"
            }

            $previousState = $classification.State
            $previousSeverity = $classification.Severity
        }

        # Confirmed incident lifecycle.
        $isOutage = $outageStates -contains $classification.State
        if ($isOutage) {
            if ($null -eq $currentIncident) {
                $currentIncident = [ordered]@{
                    StartLocal   = $now.Local
                    StartUtc     = $now.Utc
                    LastBadLocal = $now.Local
                    LastBadUtc   = $now.Utc
                    StatesSeen   = @($classification.State)
                    StartReason  = $classification.Reason
                    SampleCount  = 1
                }
            } else {
                $currentIncident.LastBadLocal = $now.Local
                $currentIncident.LastBadUtc = $now.Utc
                $currentIncident.SampleCount = [int]$currentIncident.SampleCount + 1
                if ($currentIncident.StatesSeen -notcontains $classification.State) {
                    $currentIncident.StatesSeen += $classification.State
                }
            }
        } elseif ($null -ne $currentIncident) {
            $duration = ($currentIncident.LastBadUtc - $currentIncident.StartUtc).TotalSeconds + $IntervalSeconds
            $worst = Get-WorstState -States $currentIncident.StatesSeen

            Append-CsvRow -Path $incidentsCsv -Object ([pscustomobject]@{
                StartLocal      = $currentIncident.StartLocal.ToString("o")
                StartUtc        = $currentIncident.StartUtc.ToString("o")
                EndLocal        = $currentIncident.LastBadLocal.ToString("o")
                EndUtc          = $currentIncident.LastBadUtc.ToString("o")
                DurationSeconds = [Math]::Round([Math]::Max($IntervalSeconds, $duration), 2)
                WorstState      = $worst
                StatesSeen      = ($currentIncident.StatesSeen -join ";")
                StartReason     = $currentIncident.StartReason
                SampleCount     = $currentIncident.SampleCount
            })
            $currentIncident = $null
        }

        # Periodic full snapshot every 30 minutes.
        if (($now.Utc - $lastSnapshotUtc).TotalMinutes -ge 30) {
            Write-NetworkSnapshot -Directory $SessionDirectory -Label "PERIODIC"
            $lastSnapshotUtc = $now.Utc
        }

        # Lightweight runtime checkpoint once per minute.
        if (($now.Utc - $lastRuntimeStateWriteUtc).TotalSeconds -ge 60) {
            $runtimeState = [ordered]@{
                LastSequence       = $seq
                LastSampleLocal    = $now.Local.ToString("o")
                LastSampleUtc      = $now.Utc.ToString("o")
                CurrentState       = $classification.State
                CurrentSeverity    = $classification.Severity
                CurrentAdapter     = $selectedAdapterName
                CurrentGateway     = $gateway
                InternetSuccess    = "$internetSuccessCount/$($PingTargets.Count)"
                DnsSuccess         = [bool]$lastDns.Success
                HttpSuccess        = [bool]$lastHttp.Success
            }
            Write-Utf8Text -Path $runtimeStatePath -Text ($runtimeState | ConvertTo-Json -Depth 4)
            $lastRuntimeStateWriteUtc = $now.Utc
        }

        $remaining = $endUtc - [DateTimeOffset]::UtcNow
        $statusText = "$($classification.State) | GW=$($gatewayCheck.Success) | Internet=$internetSuccessCount/$($PingTargets.Count) | Avg=$avgRtt ms | DNS=$($lastDns.Success) | HTTP=$($lastHttp.Success) | Remaining=$([Math]::Max(0,[int]$remaining.TotalHours))h $($remaining.Minutes)m"
        $pct = [Math]::Min(100, [Math]::Max(0, 100.0 * (($now.Utc - $startPair.Utc).TotalSeconds / ($endUtc - $startPair.Utc).TotalSeconds)))
        Write-Progress -Activity "Internet Evidence Monitor" -Status $statusText -PercentComplete $pct

        $elapsedMs = ([DateTimeOffset]::UtcNow - $loopStart).TotalMilliseconds
        $sleepMs = [Math]::Max(0, ($IntervalSeconds * 1000) - $elapsedMs)
        if ($sleepMs -gt 0) {
            Start-Sleep -Milliseconds ([int]$sleepMs)
        }
    }
}
finally {
    Write-Progress -Activity "Internet Evidence Monitor" -Completed

    $finish = Get-NowPair

    # Close an incident that was active when monitoring stopped.
    if ($null -ne $currentIncident) {
        $duration = ($currentIncident.LastBadUtc - $currentIncident.StartUtc).TotalSeconds + $IntervalSeconds
        $worst = Get-WorstState -States $currentIncident.StatesSeen
        Append-CsvRow -Path $incidentsCsv -Object ([pscustomobject]@{
            StartLocal      = $currentIncident.StartLocal.ToString("o")
            StartUtc        = $currentIncident.StartUtc.ToString("o")
            EndLocal        = $currentIncident.LastBadLocal.ToString("o")
            EndUtc          = $currentIncident.LastBadUtc.ToString("o")
            DurationSeconds = [Math]::Round([Math]::Max($IntervalSeconds, $duration), 2)
            WorstState      = $worst
            StatesSeen      = ($currentIncident.StatesSeen -join ";")
            StartReason     = $currentIncident.StartReason
            SampleCount     = $currentIncident.SampleCount
        })
    }

    Append-CsvRow -Path $transitionsCsv -Object ([pscustomobject]@{
        TimestampLocal = $finish.Local.ToString("o")
        TimestampUtc   = $finish.Utc.ToString("o")
        State          = "MONITOR_STOPPED"
        Severity       = "INFO"
        Reason         = "Monitoring session ended or was stopped"
        Details        = "LastSequence=$seq"
    })

    try { Write-NetworkSnapshot -Directory $SessionDirectory -Label "END" } catch {}
    try { Export-WindowsNetworkEvents -StartTime $startPair.Local.DateTime -EndTime $finish.Local.DateTime -Directory $SessionDirectory } catch {}
    try { Try-GenerateNativeWlanReport -Directory $SessionDirectory } catch {}
    try { New-EvidenceReport -Directory $SessionDirectory } catch {
        Write-Utf8Text -Path (Join-Path $SessionDirectory "report-error.txt") -Text $_.Exception.ToString()
    }
    try { Write-Hashes -Directory $SessionDirectory } catch {}

    if (-not $NoPreventSleep) {
        try { Disable-SleepPrevention } catch {}
    }

    Write-Host ""
    Write-Host "Monitoring finished." -ForegroundColor Green
    Write-Host "Evidence folder: $SessionDirectory"
    Write-Host "Open: $(Join-Path $SessionDirectory 'REPORT.html')"
    Write-Host "Send the operator REPORT.html + incidents.csv + samples.csv + Windows WLAN evidence if needed."
}
