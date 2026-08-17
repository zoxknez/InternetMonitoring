# Elevated installation step.
#
# Kept as a script rather than an inline command for two reasons: the UAC prompt shows the
# user something they can read before agreeing to it, and afterwards there is an exact
# record of what was done to the machine.
#
# Run:  powershell -ExecutionPolicy Bypass -File build\install\install.ps1
# Publish the service first:
#   dotnet publish src\IEM.Service -c Release -r win-x64 --self-contained true -o artifacts\service

$ErrorActionPreference = 'Stop'
$log = "$env:TEMP\iem-install.log"

function Write-Log([string]$message) {
    $line = "$(Get-Date -Format 'HH:mm:ss')  $message"
    Add-Content -Path $log -Value $line -Encoding utf8
    Write-Host $line
}

Set-Content -Path $log -Value '' -Encoding utf8

try {
    # Two shapes are supported. Inside a published distribution the script sits beside the
    # folders it installs; inside the repository it has to reach the artifacts directory.
    # Both are checked rather than assumed, because the second is what a developer runs and
    # the first is what everyone else gets.
    $distributionRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

    $source = if (Test-Path (Join-Path $distributionRoot 'service\InternetEvidenceService.exe')) {
        $distributionRoot
    } elseif (Test-Path (Join-Path $repoRoot 'artifacts\win-x64\service\InternetEvidenceService.exe')) {
        Join-Path $repoRoot 'artifacts\win-x64'
    } else {
        $null
    }

    $target = 'C:\Program Files\InternetEvidenceMonitor'

    if (-not $source) {
        Write-Log 'GRESKA: nije pronadjena objavljena verzija programa.'
        Write-Log 'Prvo pokrenite:'
        Write-Log '  powershell -ExecutionPolicy Bypass -File build\publish.ps1'
        exit 1
    }

    # An existing installation has to go first, and in this order. Updating is the ordinary
    # path - every new version arrives on a machine that already has one - and skipping this
    # produced two failures at once: the copy hit a running executable it could not replace,
    # and registration failed with "service already exists".
    $existing = Get-Service -Name InternetEvidenceMonitor -ErrorAction SilentlyContinue

    if ($existing) {
        Write-Log "Zatecena ranija instalacija (status: $($existing.Status))"

        if ($existing.Status -ne 'Stopped') {
            Write-Log '  Zaustavljanje servisa'
            Stop-Service -Name InternetEvidenceMonitor -Force -ErrorAction SilentlyContinue
            $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }

        Write-Log '  Uklanjanje ranije registracije'
        & sc.exe delete InternetEvidenceMonitor | ForEach-Object { Write-Log "    $_" }

        # The service control manager releases the name asynchronously; creating the new one
        # too soon fails with "marked for deletion" and leaves the machine without a service
        # at all, which is the worst outcome of the three.
        Start-Sleep -Seconds 2
    }

    Write-Log "Kopiranje: $source -> $target"
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target -Force | Out-Null

    foreach ($part in @('service', 'app', 'cli')) {
        $from = Join-Path $source $part
        if (Test-Path $from) {
            Copy-Item $from $target -Recurse -Force
            Write-Log "  $part"
        }
    }

    $exe = Join-Path $target 'service\InternetEvidenceService.exe'
    Write-Log 'Registracija servisa'
    & $exe install 2>&1 | ForEach-Object { Write-Log "  $_" }

    Write-Log 'Provera stanja servisa'
    $svc = Get-Service -Name InternetEvidenceMonitor -ErrorAction SilentlyContinue
    if ($svc) {
        $wmi = Get-CimInstance Win32_Service -Filter "Name='InternetEvidenceMonitor'"
        Write-Log "  Status:  $($svc.Status), $($wmi.StartMode)"
        Write-Log "  Nalog:   $($wmi.StartName)"
        Write-Log "  Putanja: $($wmi.PathName)"
    } else {
        Write-Log '  GRESKA: servis nije pronadjen posle instalacije'
        exit 1
    }

    # A shortcut, because a program nobody can find is a program nobody runs - and the
    # people this is for are not going to browse to Program Files.
    $app = Join-Path $target 'app\InternetEvidenceMonitor.exe'

    if (Test-Path $app) {
        $startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
        $shortcut = Join-Path $startMenu 'Internet Monitoring.lnk'

        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut($shortcut)
        $link.TargetPath = $app
        $link.WorkingDirectory = (Split-Path $app)
        $link.Description = 'Nadzor kvaliteta internet veze i priprema dokaza za prigovor'
        $link.Save()

        Write-Log "Precica: $shortcut"
    }

    Write-Log 'GOTOVO'
    Write-Log ''
    Write-Log 'Sledeci korak, kao obican korisnik:'
    Write-Log "  `"$exe`" start-session 48h"
    Write-Log '  sc start InternetEvidenceMonitor'
    Write-Log ''
    Write-Log 'Ili otvorite "Internet Monitoring" iz Start menija.'
}
catch {
    Write-Log "GRESKA: $_"
    exit 1
}
