# Builds a complete, self-contained distribution.
#
# Self-contained on purpose. The people this is for are not going to install a .NET runtime
# first, and a program that fails on launch with a message about a missing framework is a
# program that never gets used. The cost is size; the alternative is that the evidence never
# gets collected at all.
#
# Run:  powershell -ExecutionPolicy Bypass -File build\publish.ps1
#       powershell -ExecutionPolicy Bypass -File build\publish.ps1 -Runtime win-arm64

param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputRoot = Join-Path $repoRoot "artifacts\$Runtime"

function Write-Step([string]$message) {
    Write-Host ''
    Write-Host "  $message" -ForegroundColor Cyan
}

Write-Step "Priprema: $Runtime, $Configuration"

if (Test-Path $outputRoot) {
    Remove-Item $outputRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

# The lock files have to match the projects before anything is built. Checked here rather
# than through a build property, because a self-contained publish pulls in packages an
# ordinary build does not, so one lock file cannot satisfy both shapes at once.
Write-Step 'Provera zakljucanih zavisnosti'

& dotnet restore (Join-Path $repoRoot 'InternetEvidenceMonitor.slnx') -p:VerifyLockedDependencies=true --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host '  Zakljucane zavisnosti se ne poklapaju sa projektima.' -ForegroundColor Red
    Write-Host '  Ako je promena namerna, osvezite ih sa:' -ForegroundColor Red
    Write-Host '    dotnet restore --force-evaluate' -ForegroundColor Red
    exit 1
}

# Tests next. Publishing a build whose tests fail would be shipping a tool whose whole
# purpose is to be trusted with someone's evidence.
Write-Step 'Testovi'
& dotnet test (Join-Path $repoRoot 'InternetEvidenceMonitor.slnx') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host '  Testovi nisu prosli. Objavljivanje je prekinuto.' -ForegroundColor Red
    exit 1
}

$projects = @(
    @{ Name = 'servis';    Path = 'src\IEM.Service'; Folder = 'service' }
    @{ Name = 'interfejs'; Path = 'src\IEM.App';     Folder = 'app' }
    @{ Name = 'konzola';   Path = 'src\IEM.Cli';     Folder = 'cli' }
)

foreach ($project in $projects) {
    Write-Step "Objavljivanje: $($project.Name)"

    $target = Join-Path $outputRoot $project.Folder

    & dotnet publish (Join-Path $repoRoot $project.Path) `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        -o $target `
        --nologo

    if ($LASTEXITCODE -ne 0) {
        Write-Host "  Objavljivanje nije uspelo: $($project.Name)" -ForegroundColor Red
        exit 1
    }
}

Write-Step 'Kopiranje skripti za instalaciju'

$installTarget = Join-Path $outputRoot 'install'
New-Item -ItemType Directory -Path $installTarget -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'install\*.ps1') $installTarget -Force

Copy-Item (Join-Path $repoRoot 'README.md') $outputRoot -Force

# The Liberation fonts are embedded in IEM.Evidence and therefore redistributed inside every
# copy of this program. SIL OFL 1.1 requires the licence to travel with them, so it ships
# beside the binaries rather than only sitting in the source tree.
Copy-Item (Join-Path $repoRoot 'src\IEM.Evidence\Fonts\LICENSE-LiberationFonts.txt') $outputRoot -Force

# A record of exactly what went into the package. The report claims the evidence is
# reproducible; that claim is only checkable if the build itself is identifiable.
Write-Step 'Zapis o izdanju'

$version = (Select-String -Path (Join-Path $repoRoot 'Directory.Build.props') -Pattern '<Version>(.+?)</Version>').Matches[0].Groups[1].Value

@"
Internet Monitoring $version
Platforma:     $Runtime
Konfiguracija: $Configuration
Napravljeno:   $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')
.NET SDK:      $(& dotnet --version)

Sadrzaj:
  service\   Windows servis. Instalira se, radi u pozadini, prezivljava restart.
  app\       Graficki interfejs.
  cli\       Konzolni pokretac, za skripte i za proveru tudjeg paketa.
  install\   Skripte za instalaciju servisa (traze administratorska prava).

  LICENSE-LiberationFonts.txt   Licenca fontova ugradjenih u izvestaj (SIL OFL 1.1).

Instalacija servisa:
  powershell -ExecutionPolicy Bypass -File install\install.ps1

Bez instalacije: pokrenite app\InternetEvidenceMonitor.exe i snimajte
u folder na radnoj povrsini. Nadzor tada radi samo dok je program otvoren.
"@ | Set-Content -Path (Join-Path $outputRoot 'IZDANJE.txt') -Encoding utf8

Write-Step 'Kontrolni zbirovi'

$sums = Get-ChildItem $outputRoot -Recurse -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object FullName |
    ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
        $relative = $_.FullName.Substring($outputRoot.Length + 1).Replace('\', '/')
        "$hash  $relative"
    }

$sums | Set-Content -Path (Join-Path $outputRoot 'SHA256SUMS.txt') -Encoding utf8

Write-Step 'Arhiva'

$zip = Join-Path $repoRoot "artifacts\InternetMonitoring-$version-$Runtime.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$outputRoot\*" -DestinationPath $zip

# A checksum beside the archive is the whole verification story for a distribution without
# signatures: whoever sends the file can publish this hash wherever they sent the file, and
# whoever receives it can check what arrived is what left.
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
$zipHash | Set-Content -Path "$zip.sha256" -Encoding ascii

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)

# One file, nothing to unpack, nothing to install. Built every time rather than behind a
# switch, because it is the form most people will actually use and a form that only gets
# built when somebody remembers to ask for it is a form that ships broken.
#
# The cost is real and worth stating: everything is packed into the executable and unpacked
# into a temporary folder on first run, so the first start is slow and the file is large.
# Monitoring runs only while the window is open - a service that survives a restart has to be
# installed, and that is what the archive above is for.
Write-Step 'Portabl izdanje'

$portable = @(
    @{ Name = 'interfejs'; Path = 'src\IEM.App'; Built = 'InternetEvidenceMonitor.exe'; Ships = "InternetMonitoring-$version-$Runtime.exe" }
    @{ Name = 'konzola';   Path = 'src\IEM.Cli'; Built = 'iem.exe';                     Ships = "iem-$version-$Runtime.exe" }
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
        Write-Host "  Portabl izdanje nije uspelo: $($single.Name)" -ForegroundColor Red
        exit 1
    }

    $shipped = Join-Path $repoRoot "artifacts\$($single.Ships)"
    if (Test-Path $shipped) { Remove-Item $shipped -Force }
    Move-Item (Join-Path $staging $single.Built) $shipped

    # The staging folder holds the debug symbols the single file left behind; they have no
    # business in the distribution folder that gets zipped.
    Remove-Item $staging -Recurse -Force

    $singleHash = (Get-FileHash $shipped -Algorithm SHA256).Hash.ToLower()
    $singleHash | Set-Content -Path "$shipped.sha256" -Encoding ascii

    $singleSize = [math]::Round((Get-Item $shipped).Length / 1MB, 1)

    $single.Result = "$shipped  ($singleSize MB)"
    $single.Hash = $singleHash
}

Write-Host ''
Write-Host "  GOTOVO" -ForegroundColor Green
Write-Host "  Folder:  $outputRoot"
Write-Host "  Arhiva:  $zip  ($size MB)"
Write-Host "  SHA-256: $zipHash"
Write-Host ''

foreach ($single in $portable) {
    Write-Host "  Portabl ($($single.Name)): $($single.Result)"
    Write-Host "  SHA-256: $($single.Hash)"
}

Write-Host ''
