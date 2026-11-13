<#
.SYNOPSIS
    Installs the mining-fleet operator console.

.DESCRIPTION
    Meant to be piped straight from the web:

        irm https://raw.githubusercontent.com/XYphrodite/mining-fleet/master/deploy/install.ps1 | iex

    Downloads the newest release for this platform, unpacks it into the per-user programs
    folder and puts it on PATH. No administrator rights are needed: this installs the
    console on the operator machine, not the agent on a mining node.

    Because `iex` cannot take parameters, overrides come from environment variables.
    MINING_FLEET_* wins; the older XMRIG_FLEET_* names still work.

        $env:MINING_FLEET_REPO    = 'owner/name'   # release source
        $env:MINING_FLEET_VERSION = 'v1.2.0'       # a specific tag instead of the newest
        $env:MINING_FLEET_DIR     = 'D:\tools\xf'  # install somewhere else
        $env:MINING_FLEET_VARIANT = 'auto'         # auto|full|light: auto picks the small
                                                   # framework-dependent zip when the
                                                   # .NET 10 runtime is installed
#>

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 still negotiates TLS 1.0 by default on some machines.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Get-FleetEnv([string]$Suffix) {
    foreach ($prefix in 'MINING_FLEET', 'XMRIG_FLEET') {
        $value = [Environment]::GetEnvironmentVariable("${prefix}_$Suffix")
        if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
    }
    return $null
}

$repo    = Get-FleetEnv 'REPO'
if (-not $repo) { $repo = 'XYphrodite/mining-fleet' }
$version = Get-FleetEnv 'VERSION'
$dirOverride = Get-FleetEnv 'DIR'
$legacyDir = Join-Path $env:LOCALAPPDATA 'Programs\xmrig-fleet'
$modernDir = Join-Path $env:LOCALAPPDATA 'Programs\mining-fleet'
$target = if ($dirOverride) { $dirOverride } elseif (Test-Path $legacyDir) { $legacyDir } else { $modernDir }

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

# Major of the .NET runtime the light package targets. Bump together with the apps'
# target framework. Console apps, so Microsoft.NETCore.App qualifies; a machine with
# Microsoft.WindowsDesktop.App has the base runtime too. Same rule as
# ReleaseAssets.IsCompatibleRuntimeLine in the source.
$DotNetMajor = '10'
$VariantMarker = '.mining-fleet-variant'

function Test-DotNetRuntimeLine {
    param([string]$Line, [string]$Major = $DotNetMajor)
    if ($Line -match 'Program Files \(x86\)') { return $false }
    return [bool]($Line -match "^Microsoft\.(NETCore|WindowsDesktop)\.App\s+$Major\.")
}

function Test-DotNetRuntime {
    $dotnetCmd = if (Test-Path "$env:ProgramFiles\dotnet\dotnet.exe") { "$env:ProgramFiles\dotnet\dotnet.exe" } else { 'dotnet' }
    try {
        $runtimes = & $dotnetCmd --list-runtimes 2>$null
    } catch {
        return $false
    }
    foreach ($line in @($runtimes)) {
        if (Test-DotNetRuntimeLine -Line ([string]$line)) { return $true }
    }
    return $false
}

function Select-FleetAsset {
    param(
        [string]$Variant = 'auto',
        [bool]$HasRuntime = $false,
        [Parameter(Mandatory = $true)][string[]]$FullNames,
        [Parameter(Mandatory = $true)][string[]]$LightNames
    )
    if ($Variant -eq 'light') { return $LightNames }
    if ($Variant -eq 'full') { return $FullNames }
    if ($HasRuntime) { return $LightNames }
    return $FullNames
}

# Release assets are named per platform. Prefer mining-fleet-win-x64.zip, fall back to
# the xmrig-fleet alias so a console installed before the rename still updates.
$arch = if ([Environment]::Is64BitOperatingSystem) {
    if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'arm64' } else { 'x64' }
} else {
    throw 'mining-fleet needs a 64-bit Windows.'
}
# Matched in full: a release also ships the agent zip, and a substring match on the
# platform would install the node agent instead of the console.
$assetNames = @("mining-fleet-win-$arch.zip", "xmrig-fleet-win-$arch.zip")
$lightAssetNames = @("mining-fleet-win-$arch-light.zip", "xmrig-fleet-win-$arch-light.zip")

$variant = Get-FleetEnv 'VARIANT'
if (-not $variant) { $variant = 'auto' }
$variant = $variant.Trim().ToLowerInvariant()
if ($variant -notin @('auto', 'full', 'light')) { throw "MINING_FLEET_VARIANT must be auto, full or light, not '$variant'." }
$hasRuntime = Test-DotNetRuntime
if ($variant -eq 'light' -and -not $hasRuntime) {
    throw 'The light package needs the .NET runtime, which was not found. Install the runtime or set MINING_FLEET_VARIANT=full.'
}
$assetNames = Select-FleetAsset -Variant $variant -HasRuntime $hasRuntime -FullNames $assetNames -LightNames $lightAssetNames
$variantName = if ($assetNames[0] -like '*-light.zip') { 'light' } else { 'full' }

Write-Step "Looking up the newest release of $repo"
$api = if ($version) { "https://api.github.com/repos/$repo/releases/tags/$version" }
       else          { "https://api.github.com/repos/$repo/releases/latest" }

$release = $null
$apiError = $null
try {
    $release = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'mining-fleet-installer' } -TimeoutSec 30
} catch {
    $apiError = $_.Exception.Message
}

$asset = $null
if ($release) {
    foreach ($assetName in $assetNames) {
        $asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
        if ($asset) { break }
    }
    if (-not $asset) {
        $available = ($release.assets | ForEach-Object { $_.name }) -join ', '
        throw "Release $($release.tag_name) carries no $($assetNames -join ' or '). Available: $available"
    }
} else {
    # api.github.com is blocked or throttled on some networks while github.com itself works.
    # The plain download URLs need no API call, so probe those before giving up.
    Write-Host "    GitHub API unreachable ($apiError); trying direct download links" -ForegroundColor Yellow
    $base = if ($version) { "https://github.com/$repo/releases/download/$version" }
            else          { "https://github.com/$repo/releases/latest/download" }
    foreach ($assetName in $assetNames) {
        $url = "$base/$assetName"
        try {
            Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing -TimeoutSec 30 -Headers @{ 'User-Agent' = 'mining-fleet-installer' } | Out-Null
            $asset = [pscustomobject]@{ name = $assetName; size = 0; browser_download_url = $url }
            $release = [pscustomobject]@{ tag_name = if ($version) { $version } else { 'latest' } }
            break
        } catch { }
    }
    if (-not $asset) {
        throw "Could not read releases of $repo, and the direct download links failed too. Is the repository published and does it have a release? ($apiError)"
    }
}

Write-Host "    $($release.tag_name) - $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB)"

$archive = Join-Path ([IO.Path]::GetTempPath()) "mining-fleet-$([guid]::NewGuid().ToString('N')).zip"
Write-Step 'Downloading'

# HttpWebRequest rather than HttpClient: System.Net.Http is not loaded by default in
# Windows PowerShell 5.1, and Invoke-WebRequest there buffers the whole body in memory
# behind its own slow progress rendering. Streaming keeps the bar honest and the download fast.
$request = [Net.HttpWebRequest]::Create($asset.browser_download_url)
$request.UserAgent = 'mining-fleet-installer'
$request.Timeout = 60000
$request.ReadWriteTimeout = 300000

$response = $request.GetResponse()
try {
    $total = $response.ContentLength
    if ($total -le 0) { $total = $asset.size }

    $source = $response.GetResponseStream()
    $file   = [IO.File]::Create($archive)
    try {
        $buffer = New-Object byte[] 81920
        $received = 0L
        $started = [Diagnostics.Stopwatch]::StartNew()
        $lastReport = [Diagnostics.Stopwatch]::StartNew()
        while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $file.Write($buffer, 0, $read)
            $received += $read
            # Repainting on every chunk costs more time than the download itself.
            if ($lastReport.ElapsedMilliseconds -ge 150 -or $received -ge $total) {
                $lastReport.Restart()
                $percent = if ($total -gt 0) { [math]::Min(100, [int](100 * $received / $total)) } else { 0 }
                $speed = if ($started.Elapsed.TotalSeconds -gt 0) { $received / 1MB / $started.Elapsed.TotalSeconds } else { 0 }
                Write-Progress -Activity "Downloading $($asset.name)" `
                    -Status ("{0:N1} / {1:N1} MB   {2:N1} MB/s" -f ($received / 1MB), ($total / 1MB), $speed) `
                    -PercentComplete $percent
            }
        }
    } finally {
        $file.Dispose()
        $source.Dispose()
        Write-Progress -Activity "Downloading $($asset.name)" -Completed
    }
} finally {
    $response.Dispose()
}

Write-Step "Installing into $target"
New-Item -ItemType Directory -Force -Path $target | Out-Null

# A running console locks its own exe; move it aside rather than failing the install.
foreach ($name in 'mining-fleet.exe', 'xmrig-fleet.exe') {
    Get-ChildItem -Path $target -Filter $name -ErrorAction SilentlyContinue | ForEach-Object {
        try { Move-Item $_.FullName "$($_.FullName).old" -Force } catch { }
    }
}

$stageDir = Join-Path ([IO.Path]::GetTempPath()) "mining-fleet-stage-$([guid]::NewGuid().ToString('N'))"
try {
    Expand-Archive -Path $archive -DestinationPath $stageDir -Force
    if ($variantName -eq 'light') {
        $protectedNames = @('fleet.json', 'fleet.sync.json', 'gpu-income.json', 'lifetime-income.json', $VariantMarker)
        Get-ChildItem -Path $target -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
            if ($protectedNames -contains $_.Name -or $_.Name -like '*.old' -or $_.Name -like '*.old-*') { return }
            $rel = $_.FullName.Substring($target.TrimEnd('\', '/').Length).TrimStart('\', '/')
            if (Test-Path (Join-Path $stageDir $rel)) { return }
            if ($_.Extension -in @('.dll', '.pdb', '.mib', '.nib') -or $_.Name -eq 'createdump.exe' -or $_.Name -like '*.deps.json' -or $_.Name -like '*.runtimeconfig.json') {
                try { Remove-Item $_.FullName -Force -ErrorAction Stop }
                catch { try { Move-Item $_.FullName "$($_.FullName).old" -Force } catch { } }
            }
        }
    }
    Copy-Item -Path (Join-Path $stageDir '*') -Destination $target -Recurse -Force
} finally {
    Remove-Item $archive -Force -ErrorAction SilentlyContinue
    Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
}
Get-ChildItem -Path $target -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like '*.old' -or $_.Name -like '*.old-*' } |
    ForEach-Object { try { Remove-Item $_.FullName -Force } catch { } }

$exe = Get-ChildItem -Path $target -Filter 'mining-fleet.exe' -Recurse | Select-Object -First 1
if (-not $exe) { $exe = Get-ChildItem -Path $target -Filter 'xmrig-fleet.exe' -Recurse | Select-Object -First 1 }
if (-not $exe) { throw "Unpacked the archive but found no mining-fleet.exe under $target." }
$binDir = $exe.Directory.FullName
$modernExe = Join-Path $binDir 'mining-fleet.exe'
$legacyExe = Join-Path $binDir 'xmrig-fleet.exe'
if ((Test-Path $modernExe) -and -not (Test-Path $legacyExe)) { Copy-Item $modernExe $legacyExe }
if ((Test-Path $legacyExe) -and -not (Test-Path $modernExe)) { Copy-Item $legacyExe $modernExe }
# Records which kind of build this is, so `mining-fleet update` keeps the variant.
[IO.File]::WriteAllText((Join-Path $binDir $VariantMarker), $variantName)

# Put it on PATH for future shells, and on this one so it can be run right away.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (($userPath -split ';') -notcontains $binDir) {
    Write-Step 'Adding to your PATH'
    $updated = if ([string]::IsNullOrEmpty($userPath)) { $binDir } else { "$userPath;$binDir" }
    [Environment]::SetEnvironmentVariable('Path', $updated, 'User')
}
if (($env:Path -split ';') -notcontains $binDir) { $env:Path = "$env:Path;$binDir" }

Write-Host ''
Write-Host "mining-fleet $($release.tag_name) ($variantName) installed to $binDir" -ForegroundColor Green
Write-Host ''
Write-Host 'Next:' -ForegroundColor Cyan
Write-Host '  mining-fleet            # interactive console: set the token, wallet and kWh price'
Write-Host '  mining-fleet status     # one-shot fleet check'
Write-Host '  mining-fleet update     # pull the next release'
Write-Host ''
Write-Host 'Open a new terminal if `mining-fleet` is not found in an existing one. `xmrig-fleet` is the same binary.' -ForegroundColor DarkGray
