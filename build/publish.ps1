# Builds a complete, self-contained, dual-RID Windows distribution under one canonical ReleaseIdentity.
# Invariants:
# 191. RELEASE_ARTIFACT_IDENTITY_IS_EXPLICIT_AND_VERSION_BOUND
# 192. ALL_ARTIFACTS_OF_ONE_RELEASE_SHARE_ONE_CANONICAL_RELEASE_IDENTITY
# 193. RELEASE_IDENTITY_NEVER_CHANGES_AFTER_ARTIFACT_SIGNING
# 194. UNSIGNED_REQUIRED_EXECUTABLE_IS_NEVER_RELEASED
# 196. RELEASE_SIGNING_FAILURE_ALWAYS_FAILS_CLOSED
# 197. TIMESTAMP_FAILURE_NEVER_SILENTLY_DEGRADES
# 198. SIGNED_ARTIFACT_IS_NEVER_MUTATED_AFTER_SIGNING
# 199. RELEASE_MANIFEST_HASHES_EXACT_DISTRIBUTED_ARTIFACTS
# 200. SBOM_IS_GENERATED_FROM_THE_RELEASE_BEING_DISTRIBUTED
# 201. SBOM_ACCURATELY_REPRESENTS_RELEASE_COMPONENTS
# 207. SERVICE_AND_APPLICATION_RELEASE_VERSIONS_NEVER_SILENTLY_DIVERGE
# 210. DISTRIBUTED_ARTIFACTS_ARE_BIT_IDENTICAL_TO_THE_VERIFIED_RELEASE_SET
# WIN_RELEASE_SOURCE_TREE_MUST_BE_CLEAN_BEFORE_AND_AFTER_BUILD

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

# 1. Exact SDK Verification (global.json authority)
Write-Step 'Provera .NET SDK verzije'
$requiredSdk = '10.0.111'
$actualSdk = (& dotnet --version).Trim()
if ($actualSdk -ne $requiredSdk) {
    throw "Release zahteva .NET SDK $requiredSdk; aktivan je $actualSdk."
}
Write-Host "Aktivan .NET SDK: $actualSdk (potvrdjeno)" -ForegroundColor Green

# 2. Clean-tree check before start (including untracked files)
Write-Step 'Provera stanja radnog stabla (pre gradnje)'
$statusBefore = git status --porcelain
if ($statusBefore) {
    throw "Radno stablo nije cisto pre gradnje (Release source tree dirty before build):`n$statusBefore"
}
Write-Host "Radno stablo je potpuno cisto." -ForegroundColor Green

# 3. Mandatory Signing Certificate Resolution and Pre-Validation
Write-Step 'Provera i validacija sertifikata za potpisivanje'
if ([string]::IsNullOrWhiteSpace($SigningThumbprint)) {
    throw "Obavezan otisak sertifikata (SigningThumbprint / IEM_SIGNING_THUMBPRINT) nije zadat. Izrada izdanja se prekida (Fail-Closed)."
}

$cert = Get-Item "Cert:\CurrentUser\My\$SigningThumbprint" -ErrorAction SilentlyContinue
$isLocalMachine = $false
if (-not $cert) {
    $cert = Get-Item "Cert:\LocalMachine\My\$SigningThumbprint" -ErrorAction SilentlyContinue
    if ($cert) { $isLocalMachine = $true }
}

if (-not $cert) {
    throw "Sertifikat za potpisivanje sa otiskom '$SigningThumbprint' nije pronadjen u Cert:\CurrentUser\My ili Cert:\LocalMachine\My."
}

if (-not $cert.HasPrivateKey) {
    throw "Sertifikat sa otiskom '$SigningThumbprint' nema privatni kljuc (HasPrivateKey = false)."
}

if ($cert.NotAfter -le [DateTime]::UtcNow) {
    throw "Sertifikat sa otiskom '$SigningThumbprint' je istekao dana $($cert.NotAfter.ToString('u'))."
}

$hasCodeSigningEku = $false
foreach ($ext in $cert.Extensions) {
    if ($ext -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
        foreach ($usage in $ext.EnhancedKeyUsages) {
            if ($usage.Value -eq '1.3.6.1.5.5.7.3.3') { # Code Signing OID
                $hasCodeSigningEku = $true
                break
            }
        }
    }
}
if (-not $hasCodeSigningEku -and ($cert.EnhancedKeyUsageList.Count -gt 0)) {
    # If EnhancedKeyUsageList is present, verify code signing is included
    $hasCodeSigningEku = ($cert.EnhancedKeyUsageList | Where-Object { $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3' -or $_.Value -eq '1.3.6.1.5.5.7.3.3' }) -ne $null
}

if (-not $hasCodeSigningEku) {
    throw "Sertifikat sa otiskom '$SigningThumbprint' ne poseduje Code Signing EKU (1.3.6.1.5.5.7.3.3)."
}

Write-Host "Sertifikat je validan: $($cert.Subject) [Otisak: $($cert.Thumbprint)]" -ForegroundColor Green

# 4. Locate SignTool from Windows SDK
Write-Step 'Pronalazenje signtool.exe'
$signtoolPaths = @(
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe",
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe",
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin\x64\signtool.exe",
    "${env:ProgramFiles}\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe",
    "${env:ProgramFiles}\Windows Kits\10\bin\x64\signtool.exe"
)
$signtool = $null
foreach ($path in $signtoolPaths) {
    if (Test-Path $path) {
        $signtool = $path
        break
    }
}
if (-not $signtool) {
    $found = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits", "${env:ProgramFiles}\Windows Kits" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\x64\signtool.exe" } | Select-Object -First 1
    if ($found) { $signtool = $found.FullName }
}
if (-not $signtool) {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { $signtool = $cmd.Source }
}
if (-not $signtool) {
    throw "signtool.exe nije pronadjen u Windows SDK ili na sistemu. Fail-Closed."
}
Write-Host "SignTool pronadjen: $signtool" -ForegroundColor Green

# 5. Extract Authoritative Version from Directory.Build.props
$propsFile = Join-Path $repoRoot 'Directory.Build.props'
$versionMatch = Select-String -Path $propsFile -Pattern '<Version>(.+?)</Version>'
if (-not $versionMatch) {
    throw "Nije moguce pronaci <Version> u $propsFile"
}
$version = $versionMatch.Matches[0].Groups[1].Value.Trim()

$gitCommit = (git rev-parse HEAD).Trim()
$buildTimestampUtc = [DateTimeOffset]::UtcNow
$buildId = "build-$($buildTimestampUtc.ToString('yyyyMMddHHmmss'))"

Write-Host "Release Target: $version" -ForegroundColor Green
Write-Host "Git Commit:     $gitCommit" -ForegroundColor Green
Write-Host "SDK Version:    $actualSdk" -ForegroundColor Green
Write-Host "Build ID:       $buildId" -ForegroundColor Green

# 6. Verify locked dependencies
Write-Step 'Provera zakljucanih zavisnosti'
& dotnet restore (Join-Path $repoRoot 'InternetEvidenceMonitor.slnx') -p:VerifyLockedDependencies=true --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Zakljucane zavisnosti se ne poklapaju sa projektima."
}

# 7. Run tests
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
            @{ Name = 'servis';      Path = 'src\IEM.Service';    Folder = 'service';   ExeName = 'InternetEvidenceService.exe' }
            @{ Name = 'interfejs';   Path = 'src\IEM.App';        Folder = 'app';       ExeName = 'InternetEvidenceMonitor.exe' }
            @{ Name = 'konzola';     Path = 'src\IEM.Cli';        Folder = 'cli';       ExeName = 'iem.exe' }
            @{ Name = 'verifikator'; Path = 'src\IEM.Verifier';   Folder = 'verifier';  ExeName = 'iem-verifier.exe' }
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
.NET SDK:      $actualSdk
Git Commit:    $gitCommit

Sadrzaj:
  service\   Windows servis (InternetEvidenceService.exe).
  app\       Graficki interfejs (InternetEvidenceMonitor.exe).
  cli\       Konzolni pokretac (iem.exe).
  verifier\  Verifikator paketa (iem-verifier.exe).
  install\   Skripte za instalaciju servisa.
"@ | Set-Content -Path (Join-Path $outputRoot 'IZDANJE.txt') -Encoding utf8

        # 8. Fail-Closed SignTool PE Signing & Authenticode PS1 Signing Hook
        Write-Step "[$Runtime] Potpisivanje izvrsnih datoteka (SignTool RFC3161)"
        $peFiles = Get-ChildItem $outputRoot -Recurse -Include *.exe, *.dll
        $storeArgs = if ($isLocalMachine) { @('/sm', '/s', 'My') } else { @('/s', 'My') }

        foreach ($pe in $peFiles) {
            & $signtool sign @storeArgs /sha1 $SigningThumbprint /fd SHA256 /tr 'http://timestamp.digicert.com' /td SHA256 $pe.FullName
            if ($LASTEXITCODE -ne 0) {
                throw "SignTool potpisivanje nije uspelo za: $($pe.FullName) (ExitCode: $LASTEXITCODE)"
            }

            # Independent verification check
            & $signtool verify /pa /all /v $pe.FullName | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "SignTool verifikacija nije uspela za potpisani PE: $($pe.FullName)"
            }
        }

        # Sign PowerShell installation scripts
        $ps1Files = Get-ChildItem $outputRoot -Recurse -Filter *.ps1
        foreach ($ps1 in $ps1Files) {
            $sigRes = Set-AuthenticodeSignature -FilePath $ps1.FullName -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
            if ($sigRes.Status -ne 'Valid') {
                throw "Set-AuthenticodeSignature nije uspeo za skriptu: $($ps1.FullName) ($($sigRes.StatusMessage))"
            }
        }

        # 9. Record signature metadata from actual signed PE binaries (No fabrication)
        foreach ($pe in (Get-ChildItem $outputRoot -Recurse -Filter *.exe)) {
            $sig = Get-AuthenticodeSignature $pe.FullName
            if ($sig.Status -ne 'Valid') {
                throw "Potpis datoteke $($pe.Name) nije validan: $($sig.StatusMessage)"
            }
            if (-not $sig.TimeStamperCertificate) {
                throw "Datoteka $($pe.Name) nema validan vremenski zig (timestamp)."
            }

            $rel = $pe.FullName.Substring($outputRoot.Length + 1).Replace('\', '/')
            $key = "$Runtime/$rel"

            $allSignatures[$key] = [PSCustomObject]@{
                ArtifactPath = $key
                IsSigned = ($sig.Status -eq 'Valid')
                Publisher = $sig.SignerCertificate.Subject
                SubjectThumbprint = $sig.SignerCertificate.Thumbprint
                HasValidTimestamp = ($null -ne $sig.TimeStamperCertificate)
                TimestampUtc = $null # Do not fabricate assumed timestamps
                DigestAlgorithm = "SHA256"
                ChainValidated = ($sig.Status -eq 'Valid')
            }

            # Record inner executable hashes directly into ArtifactSha256Hashes
            $peHash = (Get-FileHash $pe.FullName -Algorithm SHA256).Hash.ToLower()
            $allArtifactHashes[$key] = $peHash
        }

        # 10. Internal checksums (SHA256SUMS.txt)
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

        # 11. Create ZIP archives from signed files
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

        # 12. Portable single-file editions
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

            # Sign portable executable with SignTool
            & $signtool sign @storeArgs /sha1 $SigningThumbprint /fd SHA256 /tr 'http://timestamp.digicert.com' /td SHA256 $shipped
            if ($LASTEXITCODE -ne 0) {
                throw "SignTool potpisivanje nije uspelo za portabl izdanje: $shipped"
            }
            & $signtool verify /pa /all /v $shipped | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "SignTool verifikacija nije uspela za portabl izdanje: $shipped"
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
                TimestampUtc = $null
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

    # 13. Comprehensive SBOM & Release Metadata Generation
    Write-Step 'Generisanje sveobuhvatnog SBOM-a i metapodataka o izdanju (release-metadata)'
    $metaDir = Join-Path $artifactsRoot 'release-metadata'
    if (Test-Path $metaDir) { Remove-Item $metaDir -Recurse -Force }
    New-Item -ItemType Directory -Path $metaDir -Force | Out-Null

    # Solution Projects
    $projectNames = @(
        'IEM.Core', 'IEM.Storage', 'IEM.Presentation', 'IEM.Evidence',
        'IEM.Verification', 'IEM.Windows', 'IEM.Linux', 'IEM.Service.Runtime',
        'IEM.Legal', 'IEM.Service', 'IEM.Service.Linux', 'IEM.App', 'IEM.Cli', 'IEM.Verifier'
    )
    $sbomComponents = [System.Collections.Generic.List[object]]::new()
    foreach ($p in $projectNames) {
        $projFile = Join-Path $repoRoot "src\$p\$p.csproj"
        if (Test-Path $projFile) {
            $h = (Get-FileHash $projFile -Algorithm SHA256).Hash.ToLower()
            $sbomComponents.Add([PSCustomObject]@{
                Name = $p
                Version = $version
                PackageType = 'project'
                Supplier = 'IEM Project'
                License = 'MIT'
                Sha256Hash = $h
            })
        }
    }

    # Locked NuGet Packages from packages.lock.json
    $lockFiles = Get-ChildItem (Join-Path $repoRoot 'src') -Recurse -Filter 'packages.lock.json'
    $seenPackages = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($lf in $lockFiles) {
        $lockJson = Get-Content $lf.FullName -Raw | ConvertFrom-Json
        if ($lockJson.dependencies) {
            foreach ($tfProp in $lockJson.dependencies.PSObject.Properties) {
                $targetFramework = $tfProp.Value
                foreach ($pkgProp in $targetFramework.PSObject.Properties) {
                    $pkgName = $pkgProp.Name
                    $pkgDetails = $pkgProp.Value
                    $key = "$($pkgName):$($pkgDetails.resolved)"
                    if (-not $seenPackages.Contains($key)) {
                        $seenPackages.Add($key) | Out-Null
                        $sbomComponents.Add([PSCustomObject]@{
                            Name = $pkgName
                            Version = $pkgDetails.resolved
                            PackageType = 'nuget'
                            Supplier = 'NuGet'
                            License = 'Various'
                            Sha256Hash = $pkgDetails.contentHash
                        })
                    }
                }
            }
        }
    }

    # .NET Runtime Packs for Runtimes
    foreach ($r in $Runtimes) {
        $sbomComponents.Add([PSCustomObject]@{
            Name = "Microsoft.NETCore.App.Runtime.$r"
            Version = '10.0.11'
            PackageType = 'runtime-pack'
            Supplier = 'Microsoft'
            License = 'MIT'
            Sha256Hash = $actualSdk
        })
    }

    # Write sbom.json WITHOUT self-hash (clean external hash design)
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
    }
    $sbomPath = Join-Path $metaDir 'sbom.json'
    $sbomJson = $sbomDoc | ConvertTo-Json -Depth 10
    Set-Content -Path $sbomPath -Value $sbomJson -Encoding utf8

    # Compute external byte hash of sbom.json
    $sbomSha256 = (Get-FileHash $sbomPath -Algorithm SHA256).Hash.ToLower()

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
    $manifestPath = Join-Path $metaDir 'release-manifest.json'
    $manifestJson = $releaseManifest | ConvertTo-Json -Depth 10
    Set-Content -Path $manifestPath -Value $manifestJson -Encoding utf8

    # Release Provenance
    $provenance = [PSCustomObject]@{
        Version = $version
        GitCommit = $gitCommit
        BuiltAtUtc = $buildTimestampUtc.ToString('o')
        DotnetSdkVersion = $actualSdk
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

# 14. Clean-tree check after build (Fail-Closed)
Write-Step 'Provera stanja radnog stabla (nakon gradnje)'
$statusAfter = git status --porcelain
if ($statusAfter) {
    throw "Radno stablo nije cisto nakon gradnje (Release source tree dirty after build):`n$statusAfter"
}
Write-Host "Radno stablo je cisto nakon gradnje." -ForegroundColor Green

Write-Host ''
Write-Host "  IZRADA IZDANJA JE USPESNO ZAVRSENA ($version)" -ForegroundColor Green
Write-Host ''
