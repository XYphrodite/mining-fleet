<#
.SYNOPSIS
    Installs the mining-fleet operator console.

.DESCRIPTION
    Meant to be piped straight from the web:

        irm https://raw.githubusercontent.com/XYphrodite/xmrig-fleet/master/deploy/install.ps1 | iex

    Downloads the newest release for this platform, unpacks it into the per-user programs
    folder and puts it on PATH. No administrator rights are needed: this installs the
    console on the operator machine, not the agent on a mining node.

    Because `iex` cannot take parameters, overrides come from environment variables:

        $env:XMRIG_FLEET_REPO    = 'owner/name'   # release source
        $env:XMRIG_FLEET_VERSION = 'v1.2.0'       # a specific tag instead of the newest
        $env:XMRIG_FLEET_DIR     = 'D:\tools\xf'  # install somewhere else
#>

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 still negotiates TLS 1.0 by default on some machines.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo    = if ($env:XMRIG_FLEET_REPO) { $env:XMRIG_FLEET_REPO } else { 'XYphrodite/xmrig-fleet' }
$version = $env:XMRIG_FLEET_VERSION
$target  = if ($env:XMRIG_FLEET_DIR) { $env:XMRIG_FLEET_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\xmrig-fleet' }

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

# Release assets are named per platform, e.g. xmrig-fleet-win-x64.zip.
$arch = if ([Environment]::Is64BitOperatingSystem) {
    if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'arm64' } else { 'x64' }
} else {
    throw 'xmrig-fleet needs a 64-bit Windows.'
}
# Matched in full: a release also ships xmrig-fleet-agent-win-x64.zip, and a substring
# match on the platform would install the node agent instead of the console.
$assetName = "xmrig-fleet-win-$arch.zip"

Write-Step "Looking up the newest release of $repo"
$api = if ($version) { "https://api.github.com/repos/$repo/releases/tags/$version" }
       else          { "https://api.github.com/repos/$repo/releases/latest" }

try {
    $release = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'xmrig-fleet-installer' } -TimeoutSec 30
} catch {
    throw "Could not read releases of $repo. Is the repository published and does it have a release? ($($_.Exception.Message))"
}

$asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
if (-not $asset) {
    $available = ($release.assets | ForEach-Object { $_.name }) -join ', '
    throw "Release $($release.tag_name) carries no $assetName. Available: $available"
}

Write-Host "    $($release.tag_name) - $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB)"

$archive = Join-Path ([IO.Path]::GetTempPath()) "xmrig-fleet-$([guid]::NewGuid().ToString('N')).zip"
Write-Step 'Downloading'

# HttpWebRequest rather than HttpClient: System.Net.Http is not loaded by default in
# Windows PowerShell 5.1, and Invoke-WebRequest there buffers the whole body in memory
# behind its own slow progress rendering. Streaming keeps the bar honest and the download fast.
$request = [Net.HttpWebRequest]::Create($asset.browser_download_url)
$request.UserAgent = 'xmrig-fleet-installer'
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
Get-ChildItem -Path $target -Filter 'xmrig-fleet*.exe' -ErrorAction SilentlyContinue | ForEach-Object {
    try { Move-Item $_.FullName "$($_.FullName).old" -Force } catch { }
}
Get-ChildItem -Path $target -Filter '*.old' -Recurse -ErrorAction SilentlyContinue |
    ForEach-Object { try { Remove-Item $_.FullName -Force } catch { } }

try {
    Expand-Archive -Path $archive -DestinationPath $target -Force
} finally {
    Remove-Item $archive -Force -ErrorAction SilentlyContinue
}

$exe = Get-ChildItem -Path $target -Filter 'xmrig-fleet.exe' -Recurse | Select-Object -First 1
if (-not $exe) { throw "Unpacked the archive but found no xmrig-fleet.exe under $target." }
$binDir = $exe.Directory.FullName

# Put it on PATH for future shells, and on this one so it can be run right away.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (($userPath -split ';') -notcontains $binDir) {
    Write-Step 'Adding to your PATH'
    $updated = if ([string]::IsNullOrEmpty($userPath)) { $binDir } else { "$userPath;$binDir" }
    [Environment]::SetEnvironmentVariable('Path', $updated, 'User')
}
if (($env:Path -split ';') -notcontains $binDir) { $env:Path = "$env:Path;$binDir" }

Write-Host ''
Write-Host "xmrig-fleet $($release.tag_name) installed to $binDir" -ForegroundColor Green
Write-Host ''
Write-Host 'Next:' -ForegroundColor Cyan
Write-Host '  xmrig-fleet            # interactive console: set the token, wallet and kWh price'
Write-Host '  xmrig-fleet status     # one-shot fleet check'
Write-Host '  xmrig-fleet update     # pull the next release'
Write-Host ''
Write-Host 'Open a new terminal if `xmrig-fleet` is not found in an existing one.' -ForegroundColor DarkGray
