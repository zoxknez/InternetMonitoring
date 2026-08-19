$ErrorActionPreference = "Stop"

$version = "3.0.0-rc.1"
$publishDir = "publish"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

$runtimes = @("win-x64", "win-arm64")

foreach ($rid in $runtimes) {
    Write-Host "`n=== PUBLISHING FOR $rid ===" -ForegroundColor Cyan
    $outDir = "$publishDir\$rid"
    $distDir = "$publishDir\dist"
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null

    # 1. Desktop GUI App
    Write-Host "Publishing IEM.App ($rid)..."
    dotnet publish src/IEM.App/IEM.App.csproj -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $outDir

    # 2. CLI tool
    Write-Host "Publishing IEM.Cli ($rid)..."
    dotnet publish src/IEM.Cli/IEM.Cli.csproj -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -o $outDir

    # 3. Verifier tool
    Write-Host "Publishing IEM.Verifier ($rid)..."
    dotnet publish src/IEM.Verifier/IEM.Verifier.csproj -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -o $outDir

    # Copy named binaries to dist
    $exeName = "InternetEvidenceMonitor-$version-$rid.exe"
    Copy-Item "$outDir\InternetEvidenceMonitor.exe" "$distDir\$exeName" -Force
    Copy-Item "$outDir\iem.exe" "$distDir\iem-$version-$rid.exe" -Force
    Copy-Item "$outDir\iem-verifier.exe" "$distDir\iem-verifier-$version-$rid.exe" -Force

    # Zip full bundle
    $zipName = "InternetMonitoring-$version-$rid.zip"
    Compress-Archive -Path "$outDir\*" -DestinationPath "$distDir\$zipName" -Force

    # Compute SHA-256
    $exeHash = (Get-FileHash "$distDir\$exeName" -Algorithm SHA256).Hash.ToLower()
    $zipHash = (Get-FileHash "$distDir\$zipName" -Algorithm SHA256).Hash.ToLower()

    Set-Content -Path "$distDir\$exeName.sha256" -Value "$exeHash *$exeName"
    Set-Content -Path "$distDir\$zipName.sha256" -Value "$zipHash *$zipName"

    Write-Host "[$rid] $exeName SHA256: $exeHash" -ForegroundColor Green
    Write-Host "[$rid] $zipName SHA256: $zipHash" -ForegroundColor Green
}

Write-Host "`n=== PUBLISH COMPLETE ===" -ForegroundColor Cyan
Get-ChildItem "$publishDir\dist" | Format-Table Name, Length
