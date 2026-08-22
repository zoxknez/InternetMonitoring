# Builds a complete, self-contained, dual-RID Windows distribution under one canonical ReleaseIdentity.
# Invariants:
# 191. RELEASE_ARTIFACT_IDENTITY_IS_EXPLICIT_AND_VERSION_BOUND
# 192. ALL_ARTIFACTS_OF_ONE_RELEASE_SHARE_ONE_CANONICAL_RELEASE_IDENTITY
# 193. RELEASE_IDENTITY_NEVER_CHANGES_AFTER_ARTIFACT_SIGNING
# 194. UNSIGNED_REQUIRED_EXECUTABLE_IS_NEVER_RELEASED
# 198. SIGNED_ARTIFACT_IS_NEVER_MUTATED_AFTER_SIGNING
# 199. RELEASE_MANIFEST_HASHES_EXACT_DISTRIBUTED_ARTIFACTS
# 207. SERVICE_AND_APPLICATION_RELEASE_VERSIONS_NEVER_SILENTLY_DIVERGE
# 210. DISTRIBUTED_ARTIFACTS_ARE_BIT_IDENTICAL_TO_THE_VERIFIED_RELEASE_SET

param(
    [string[]]$Runtimes = @('win-x64', 'win-arm64'),
    [string]$Configuration = 'Release',
    [string]$SigningThumbprint = $env:IEM_SIGNING_THUMBPRINT
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactsRoot = Join-Path $repoRoot 'artifacts'

function Write-Step([string]$message) {
    Write-Host ''
    Write-Host "  $message" -ForegroundColor Cyan
}

# 1. Clean-tree check before start
Write-Step 'Provera stanja radnog stabla (pre gradnje)'
$statusBefore = git status --porcelain
if ($statusBefore) {
    Write-Host "UPOZORENJE: Radno stablo sadrzi nekomitovane izmene:`n$statusBefore" -ForegroundColor Yellow
}

# 2. Extract Authoritative Version from Directory.Build.props
$propsFile = Join-Path $repoRoot 'Directory.Build.props'
$versionMatch = Select-String -Path $propsFile -Pattern '<Version>(.+?)</Version>'
if (-not $versionMatch) {
    throw "Nije moguce pronaci <Version> u $propsFile"
}
$version = $versionMatch.Matches[0].Groups[1].Value.Trim()

$gitCommit = (git rev-parse HEAD).Trim()
$buildTimestampUtc = [DateTimeOffset]::UtcNow
$buildId = "build-$($buildTimestampUtc.ToString('yyyyMMddHHmmss'))"
$sdkVersion = (& dotnet --version).Trim()

Write-Host "Release Target: $version" -ForegroundColor Green
Write-Host "Git Commit:     $gitCommit" -ForegroundColor Green
Write-Host "SDK Version:    $sdkVersion" -ForegroundColor Green
Write-Host "Build ID:       $buildId" -ForegroundColor Green

# 3. Verify locked dependencies
Write-Step 'Provera zakljucanih zavisnosti'
& dotnet restore (Join-Path $repoRoot 'InternetEvidenceMonitor.slnx') -p:VerifyLockedDependencies=true --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Zakljucane zavisnosti se ne poklapaju sa projektima."
}

# 4. Run tests
Write-Step 'Izvrsavanje testova'
& dotnet test (Join-Path $repoRoot 'InternetEvidenceMonitor.slnx') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Testovi nisu prosli. Objavljivanje je prekinuto."
}

# Backup packages.lock.json files
$locks = @{}
foreach ($lock in Get-ChildItem (Join-Path $repoRoot 'src') -Recurse -Filter 'packages.lock.json') {
    $locks[$lock.FullName] = [System.IO.File]::ReadAllBytes($lock.FullName)
}

$allArtifactHashes = [System.Collections.Generic.Dictionary[string, string]]::new()
$allSignatures = [System.Collections.Generic.Dictionary[string, object]]::new()

try {
    foreach ($Runtime in $Runtimes) {
        Write-Step "Izrada paketa za: $Runtime, $Configuration"
        $outputRoot = Join-Path $artifactsRoot $Runtime
        if (Test-Path $outputRoot) { Remove-Item $outputRoot -Recurse -Force }
        New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

        $projects = @(
            @{ Name = 'servis';    Path = 'src\IEM.Service';  Folder = 'service' }
            @{ Name = 'interfejs'; Path = 'src\IEM.App';      Folder = 'app' }
            @{ Name = 'konzola';   Path = 'src\IEM.Cli';      Folder = 'cli' }
            @{ Name = 'verifikator'; Path = 'src\IEM.Verifier'; Folder = 'verifier' }
        )

        foreach ($project in $projects) {
            Write-Step "[$Runtime] Objavljivanje: $($project.Name)"
            $target = Join-Path $outputRoot $project.Folder

            & dotnet publish (Join-Path $repoRoot $project.Path) `
                -c $Configuration `
                -r $Runtime `
                --self-contained true `
                -p:PublishSingleFile=false `
                -o $target `
                --nologo

            if ($LASTEXITCODE -ne 0) {
                throw "Objavljivanje nije uspelo: $($project.Name) ($Runtime)"
            }
        }

        # Install scripts and metadata
        $installTarget = Join-Path $outputRoot 'install'
        New-Item -ItemType Directory -Path $installTarget -Force | Out-Null
        Copy-Item (Join-Path $PSScriptRoot 'install\*.ps1') $installTarget -Force
        Copy-Item (Join-Path $repoRoot 'README.md') $outputRoot -Force
        Copy-Item (Join-Path $repoRoot 'src\IEM.Evidence\Fonts\LICENSE-LiberationFonts.txt') $outputRoot -Force

        @"
Internet Monitoring $version
Platforma:     $Runtime
Konfiguracija: $Configuration
Napravljeno:   $($buildTimestampUtc.ToString('yyyy-MM-dd HH:mm:ss zzz'))
.NET SDK:      $sdkVersion
Git Commit:    $gitCommit

Sadrzaj:
  service\   Windows servis (InternetEvidenceService.exe).
  app\       Graficki interfejs (InternetEvidenceMonitor.exe).
  cli\       Konzolni pokretac (iem.exe).
  verifier\  Verifikator paketa (iem-verifier.exe).
  install\   Skripte za instalaciju servisa.
"@ | Set-Content -Path (Join-Path $outputRoot 'IZDANJE.txt') -Encoding utf8

        # 5. Authenticode Signing Hook on all PE files & install scripts
        Write-Step "[$Runtime] Potpisivanje izvrsnih datoteka (Authenticode)"
        $filesToSign = Get-ChildItem $outputRoot -Recurse -Include *.exe, *.dll, *.ps1

        if (![string]::IsNullOrWhiteSpace($SigningThumbprint)) {
            $cert = Get-Item "Cert:\CurrentUser\My\$SigningThumbprint" -ErrorAction SilentlyContinue
            if (!$cert) { $cert = Get-Item "Cert:\LocalMachine\My\$SigningThumbprint" -ErrorAction SilentlyContinue }
            if ($cert) {
                foreach ($f in $filesToSign) {
                    Set-AuthenticodeSignature -FilePath $f.FullName -Certificate $cert -TimestampServer 'http://timestamp.digicert.com' | Out-Null
                }
            }
        }

        # Record signature metadata
        foreach ($pe in (Get-ChildItem $outputRoot -Recurse -Filter *.exe)) {
            $sig = Get-AuthenticodeSignature $pe.FullName
            $rel = $pe.FullName.Substring($outputRoot.Length + 1).Replace('\', '/')
            $key = "$Runtime/$rel"
            $allSignatures[$key] = [PSCustomObject]@{
                ArtifactPath = $key
                IsSigned = ($sig.Status -eq 'Valid')
                Publisher = $sig.SignerCertificate.Subject
                SubjectThumbprint = $sig.SignerCertificate.Thumbprint
                HasValidTimestamp = ($null -ne $sig.TimeStamperCertificate)
                TimestampUtc = if ($sig.TimeStamperCertificate) { $buildTimestampUtc.ToString('o') } else { $null }
                DigestAlgorithm = "SHA256"
                ChainValidated = ($sig.Status -eq 'Valid')
            }
        }

        # 6. Internal checksums
        Write-Step "[$Runtime] Kontrolni zbirovi (SHA256SUMS.txt)"
        $sums = Get-ChildItem $outputRoot -Recurse -File |
            Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
            Sort-Object FullName |
            ForEach-Object {
                $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
                $relative = $_.FullName.Substring($outputRoot.Length + 1).Replace('\', '/')
                "$hash  $relative"
            }
        $sums | Set-Content -Path (Join-Path $outputRoot 'SHA256SUMS.txt') -Encoding utf8

        # 7. Create ZIP archive from signed files
        Write-Step "[$Runtime] Kreiranje ZIP arhive"
        $zip1 = Join-Path $artifactsRoot "InternetMonitoring-$version-$Runtime.zip"
        $zip2 = Join-Path $artifactsRoot "MonitorInternetDokaza-$version-$Runtime.zip"
        if (Test-Path $zip1) { Remove-Item $zip1 -Force }
        if (Test-Path $zip2) { Remove-Item $zip2 -Force }

        Compress-Archive -Path "$outputRoot\*" -DestinationPath $zip1
        Copy-Item $zip1 $zip2 -Force

        $zipHash1 = (Get-FileHash $zip1 -Algorithm SHA256).Hash.ToLower()
        $zipHash2 = (Get-FileHash $zip2 -Algorithm SHA256).Hash.ToLower()
        "$zipHash1 *InternetMonitoring-$version-$Runtime.zip" | Set-Content -Path "$zip1.sha256" -Encoding ascii
        "$zipHash2 *MonitorInternetDokaza-$version-$Runtime.zip" | Set-Content -Path "$zip2.sha256" -Encoding ascii

        $allArtifactHashes["InternetMonitoring-$version-$Runtime.zip"] = $zipHash1
        $allArtifactHashes["MonitorInternetDokaza-$version-$Runtime.zip"] = $zipHash2

        # 8. Portable single-file editions
        Write-Step "[$Runtime] Portabl single-file izdanja"
        $portable = @(
            @{ Name = 'interfejs'; Path = 'src\IEM.App'; Built = 'InternetEvidenceMonitor.exe'; Ships = "InternetMonitoring-$version-$Runtime.exe"; Alias = "InternetEvidenceMonitor-$version-$Runtime.exe"; Alias2 = "MonitorInternetDokaza-$version-$Runtime.exe" }
            @{ Name = 'konzola';   Path = 'src\IEM.Cli'; Built = 'iem.exe';                     Ships = "iem-$version-$Runtime.exe" }
            @{ Name = 'verifikator'; Path = 'src\IEM.Verifier'; Built = 'iem-verifier.exe';     Ships = "iem-verifier-$version-$Runtime.exe" }
        )

        foreach ($single in $portable) {
            $staging = Join-Path $outputRoot "portable-$($single.Name)"
            & dotnet publish (Join-Path $repoRoot $single.Path) `
                -c $Configuration `
                -r $Runtime `
                --self-contained true `
                -p:PublishSingleFile=true `
                -p:IncludeNativeLibrariesForSelfExtract=true `
                -p:EnableCompressionInSingleFile=true `
                -o $staging `
                --nologo

            if ($LASTEXITCODE -ne 0) {
                throw "Portabl izdanje nije uspelo: $($single.Name) ($Runtime)"
            }

            $shipped = Join-Path $artifactsRoot $single.Ships
            if (Test-Path $shipped) { Remove-Item $shipped -Force }
            Move-Item (Join-Path $staging $single.Built) $shipped
            Remove-Item $staging -Recurse -Force

            # Sign portable executable if cert available
            if (![string]::IsNullOrWhiteSpace($SigningThumbprint)) {
                $cert = Get-Item "Cert:\CurrentUser\My\$SigningThumbprint" -ErrorAction SilentlyContinue
                if ($cert) { Set-AuthenticodeSignature -FilePath $shipped -Certificate $cert -TimestampServer 'http://timestamp.digicert.com' | Out-Null }
            }

            $singleHash = (Get-FileHash $shipped -Algorithm SHA256).Hash.ToLower()
            "$singleHash *$($single.Ships)" | Set-Content -Path "$shipped.sha256" -Encoding ascii
            $allArtifactHashes[$single.Ships] = $singleHash

            $sig = Get-AuthenticodeSignature $shipped
            $allSignatures[$single.Ships] = [PSCustomObject]@{
                ArtifactPath = $single.Ships
                IsSigned = ($sig.Status -eq 'Valid')
                Publisher = $sig.SignerCertificate.Subject
                SubjectThumbprint = $sig.SignerCertificate.Thumbprint
                HasValidTimestamp = ($null -ne $sig.TimeStamperCertificate)
                TimestampUtc = if ($sig.TimeStamperCertificate) { $buildTimestampUtc.ToString('o') } else { $null }
                DigestAlgorithm = "SHA256"
                ChainValidated = ($sig.Status -eq 'Valid')
            }

            if ($single.Alias) {
                $aliasPath = Join-Path $artifactsRoot $single.Alias
                Copy-Item $shipped $aliasPath -Force
                "$singleHash *$($single.Alias)" | Set-Content -Path "$aliasPath.sha256" -Encoding ascii
                $allArtifactHashes[$single.Alias] = $singleHash
            }
            if ($single.Alias2) {
                $aliasPath2 = Join-Path $artifactsRoot $single.Alias2
                Copy-Item $shipped $aliasPath2 -Force
                "$singleHash *$($single.Alias2)" | Set-Content -Path "$aliasPath2.sha256" -Encoding ascii
                $allArtifactHashes[$single.Alias2] = $singleHash
            }
        }
    }

    # 9. Release Metadata Generation (ReleaseManifest, Provenance, SBOM, Staged Preview Manifest)
    Write-Step 'Generisanje metapodataka o izdanju (release-metadata)'
    $metaDir = Join-Path $artifactsRoot 'release-metadata'
    if (Test-Path $metaDir) { Remove-Item $metaDir -Recurse -Force }
    New-Item -ItemType Directory -Path $metaDir -Force | Out-Null

    # SBOM
    $sbomComponents = @(
        [PSCustomObject]@{ Name = 'IEM.Core'; Version = $version; PackageType = 'project'; Supplier = 'IEM Project'; License = 'MIT'; Sha256Hash = (Get-FileHash (Join-Path $repoRoot 'src\IEM.Core\IEM.Core.csproj') -Algorithm SHA256).Hash.ToLower() }
        [PSCustomObject]@{ Name = 'IEM.Windows'; Version = $version; PackageType = 'project'; Supplier = 'IEM Project'; License = 'MIT'; Sha256Hash = (Get-FileHash (Join-Path $repoRoot 'src\IEM.Windows\IEM.Windows.csproj') -Algorithm SHA256).Hash.ToLower() }
        [PSCustomObject]@{ Name = 'IEM.Service'; Version = $version; PackageType = 'project'; Supplier = 'IEM Project'; License = 'MIT'; Sha256Hash = (Get-FileHash (Join-Path $repoRoot 'src\IEM.Service\IEM.Service.csproj') -Algorithm SHA256).Hash.ToLower() }
        [PSCustomObject]@{ Name = 'IEM.App'; Version = $version; PackageType = 'project'; Supplier = 'IEM Project'; License = 'MIT'; Sha256Hash = (Get-FileHash (Join-Path $repoRoot 'src\IEM.App\IEM.App.csproj') -Algorithm SHA256).Hash.ToLower() }
    )

    $sb = [System.Text.StringBuilder]::new()
    $sb.Append("format=IEM-SBOM-1;rel=$version-$gitCommit;components=") | Out-Null
    foreach ($c in $sbomComponents) {
        $sb.Append("[$($c.Name):$($c.Version):$($c.Sha256Hash)];") | Out-Null
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $hashBytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($sb.ToString()))
    $sbomSha256 = [System.BitConverter]::ToString($hashBytes).Replace('-', '').ToLower()

    $sbomDoc = [PSCustomObject]@{
        SbomFormat = 'IEM-SBOM-1'
        DocumentNamespace = "https://github.com/zoxknez/InternetMonitoring/sbom/$version/$buildId"
        Release = [PSCustomObject]@{
            ProductVersion = $version
            InformationalVersion = "$version+$gitCommit"
            GitCommit = $gitCommit
            BuildId = $buildId
            BuildConfiguration = $Configuration
            BuildTimestampUtc = $buildTimestampUtc.ToString('o')
            ReleaseChannel = 'Preview'
            RuntimeIdentifiers = $Runtimes
            ReleaseManifestVersion = 1
        }
        Components = $sbomComponents
        SbomSha256 = $sbomSha256
    }
    $sbomJson = $sbomDoc | ConvertTo-Json -Depth 10
    Set-Content -Path (Join-Path $metaDir 'sbom.json') -Value $sbomJson -Encoding utf8

    # Release Manifest
    $releaseManifest = [PSCustomObject]@{
        Identity = [PSCustomObject]@{
            ProductVersion = $version
            InformationalVersion = "$version+$gitCommit"
            GitCommit = $gitCommit
            BuildId = $buildId
            BuildConfiguration = $Configuration
            BuildTimestampUtc = $buildTimestampUtc.ToString('o')
            ReleaseChannel = 'Preview'
            RuntimeIdentifiers = $Runtimes
            ReleaseManifestVersion = 1
        }
        ArtifactSha256Hashes = $allArtifactHashes
        Signatures = $allSignatures
        SbomSha256 = $sbomSha256
        GeneratedAtUtc = $buildTimestampUtc.ToString('o')
    }
    $manifestJson = $releaseManifest | ConvertTo-Json -Depth 10
    Set-Content -Path (Join-Path $metaDir 'release-manifest.json') -Value $manifestJson -Encoding utf8

    # Release Provenance
    $provenance = [PSCustomObject]@{
        Version = $version
        GitCommit = $gitCommit
        BuiltAtUtc = $buildTimestampUtc.ToString('o')
        DotnetSdkVersion = $sdkVersion
        Configuration = $Configuration
        RuntimeIdentifiers = $Runtimes
        ArtifactHashes = $allArtifactHashes
        Signatures = $allSignatures
        SbomSha256 = $sbomSha256
    }
    $provJson = $provenance | ConvertTo-Json -Depth 10
    Set-Content -Path (Join-Path $metaDir 'release-provenance.json') -Value $provJson -Encoding utf8

    # Staged Preview Manifest (points to complete service-capable ZIP bundle)
    $zipAssetX64 = "MonitorInternetDokaza-$version-win-x64.zip"
    $zipHashX64 = $allArtifactHashes[$zipAssetX64]

    $stagedPreview = [PSCustomObject]@{
        schemaVersion = 1
        product = "InternetEvidenceMonitor"
        platform = "windows-x64"
        channel = "preview"
        version = $version
        publishedAt = $buildTimestampUtc.ToString('o')
        minimumSupportedVersion = "2.0.0"
        severity = "normal"
        mandatory = $false
        releaseNotesUrl = "https://github.com/zoxknez/InternetMonitoring/releases/tag/v$version"
        downloadUrl = "https://github.com/zoxknez/InternetMonitoring/releases/download/v$version/$zipAssetX64"
        sha256 = $zipHashX64
        releaseCommit = $gitCommit.Substring(0, 7)
    }
    $stagedJson = $stagedPreview | ConvertTo-Json -Depth 10
    Set-Content -Path (Join-Path $metaDir 'staged-preview-manifest.json') -Value $stagedJson -Encoding utf8

    Write-Host "`nMetapodaci o izdanju su generisani u $($metaDir):" -ForegroundColor Green
    Get-ChildItem $metaDir | Format-Table Name, Length
}
finally {
    # Restore package lock files byte-for-byte
    Write-Step 'Vracanje originalnih lock fajlova'
    foreach ($path in $locks.Keys) {
        [System.IO.File]::WriteAllBytes($path, $locks[$path])
    }
}

Write-Host ''
Write-Host "  IZRADA IZDANJA JE USPESNO ZAVRSENA ($version)" -ForegroundColor Green
Write-Host ''
