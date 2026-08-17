# Elevated removal step.
# Removes the service and the installed files. Recorded sessions under ProgramData are
# deliberately left alone: they are the evidence, and an uninstaller has no business
# deleting the thing the application existed to produce.

$ErrorActionPreference = 'Stop'
$log = "$env:TEMP\iem-uninstall.log"
Set-Content -Path $log -Value '' -Encoding utf8

function Write-Log([string]$m) {
    $line = "$(Get-Date -Format 'HH:mm:ss')  $m"
    Add-Content -Path $log -Value $line -Encoding utf8
    Write-Host $line
}

try {
    $target = 'C:\Program Files\InternetEvidenceMonitor'

    # The layout changed when the package grew to hold the interface and the console
    # alongside the service. Both are checked, so this still removes an installation left
    # by an older version rather than silently leaving the service registered.
    $exe = @(
        (Join-Path $target 'service\InternetEvidenceService.exe'),
        (Join-Path $target 'InternetEvidenceService.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    # A service that is already gone is a finished job, not a failure.
    #
    # Treating it as one aborted the script before it removed the files, so running the
    # uninstaller twice - or after removing the service by any other means - left the
    # program installed with nothing to say why. Removal has to be safe to repeat.
    if (-not (Get-Service -Name InternetEvidenceMonitor -ErrorAction SilentlyContinue)) {
        Write-Log 'Servis nije registrovan; nastavlja se uklanjanje fajlova'
    }
    elseif ($exe) {
        Write-Log 'Uklanjanje servisa'
        & $exe uninstall 2>&1 | ForEach-Object { Write-Log "  $_" }
    }
    else {
        Write-Log 'Izvrsni fajl nije pronadjen; uklanjanje preko sc.exe'
        & sc.exe stop InternetEvidenceMonitor 2>&1 | Out-Null
        & sc.exe delete InternetEvidenceMonitor 2>&1 | ForEach-Object { Write-Log "  $_" }
    }

    # The service process needs a moment to exit before its folder can be removed.
    Start-Sleep -Seconds 2

    if (Test-Path $target) {
        Write-Log "Brisanje foldera: $target"
        Remove-Item $target -Recurse -Force
    }

    # A shortcut left behind after an uninstall points at nothing and reads as a botched
    # removal, which is the last impression this program should leave.
    $shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Internet Monitoring.lnk'

    if (Test-Path $shortcut) {
        Write-Log 'Brisanje precice iz Start menija'
        Remove-Item $shortcut -Force
    }

    Write-Log 'Snimljene sesije u C:\ProgramData\InternetEvidenceMonitor nisu obrisane.'
    Write-Log 'To je namerno: one su dokaz, i program koji se uklanja nema posla da brise'
    Write-Log 'ono zbog cega je i postojao.'
    Write-Log 'GOTOVO'
}
catch {
    Write-Log "GRESKA: $_"
    exit 1
}
