Write-Host "=== TEST SIMULACIJE UPDATE-A I POKRETANJA APLIKACIJE/SERVISA ===" -ForegroundColor Cyan

# 1. Provera statusa manifesta
$manifestPath = "updates\windows\stable.json"
$manifest = Get-Content $manifestPath | ConvertFrom-Json
Write-Host "[1] Lokalni manifest verzije:" $manifest.version "(Kanal: $($manifest.channel))" -ForegroundColor Green
Write-Host "    Download URL: $($manifest.downloadUrl)"
Write-Host "    Release Notes: $($manifest.releaseNotesUrl)"

# 2. Resetovanje lokalnih preferencija (uklanjanje prethodnog 24h snooze/timestamp-a)
$appDataDir = [System.IO.Path]::Combine($env:LOCALAPPDATA, "InternetEvidenceMonitor")
$prefFile = [System.IO.Path]::Combine($appDataDir, "update-preferences.json")
if (Test-Path $prefFile) {
    Remove-Item $prefFile -Force
    Write-Host "[2] Resetovane update preferencije za svežu proveru." -ForegroundColor Yellow
}

# 3. Pokretanje Desktop Aplikacije (IEM.App)
$appExe = "src\IEM.App\bin\Release\net10.0-windows\InternetEvidenceMonitor.exe"
if (Test-Path $appExe) {
    Write-Host "[3] Pokretanje Desktop aplikacije: $appExe" -ForegroundColor Green
    $appProc = Start-Process -FilePath $appExe -PassThru
    Write-Host "    Aplikacija pokrenuta sa PID: $($appProc.Id)"
} else {
    Write-Host "[3] Greška: $appExe nije pronađen." -ForegroundColor Red
}

# 4. Pokretanje Servisa u testnom/konzolnom modu
$serviceExe = "src\IEM.Service\bin\Release\net10.0-windows\InternetEvidenceService.exe"
if (Test-Path $serviceExe) {
    Write-Host "[4] Testiranje rada servisa: $serviceExe" -ForegroundColor Green
    $serviceProc = Start-Process -FilePath $serviceExe -ArgumentList "--test" -PassThru -WindowStyle Hidden
    Write-Host "    Servis pokrenut sa PID: $($serviceProc.Id)"
}

Write-Host "`n=== SIMULACIJA AKTIVNA ===" -ForegroundColor Cyan
Write-Host "Aplikacija u pozadini preuzima manifest i aktivira gornji plavi baner sa verzijom $($manifest.version)!"
