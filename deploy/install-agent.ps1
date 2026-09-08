<#
.SYNOPSIS
    Installs the mining-fleet agent as a Windows service on a mining node.

.DESCRIPTION
    Copies the published agent to the target directory, writes appsettings.json with the
    shared fleet token, opens the agent port to the tailnet only, and registers a service
    that starts automatically.

    Run this in an elevated PowerShell on the node. The agent needs Administrator to read
    temperature and power sensors and to control the miner process.

    Can also be run straight from the web, which avoids the execution policy that blocks a
    downloaded .ps1 file:

        $env:MINING_FLEET_TOKEN = '<fleet token>'
        irm https://raw.githubusercontent.com/XYphrodite/xmrig-fleet/master/deploy/install-agent.ps1 | iex

    In that form the agent payload is downloaded from the newest release automatically.

.EXAMPLE
    .\install-agent.ps1 -Token "my-fleet-secret" -SourcePath .\publish
#>
[CmdletBinding()]
param(
    # Shared secret, must match "token" in the console's fleet.json. Falls back to
    # $env:MINING_FLEET_TOKEN (or the older XMRIG_FLEET_TOKEN) so the script also works
    # when piped to iex, which cannot pass arguments.
    [string]$Token = $(if ($env:MINING_FLEET_TOKEN) { $env:MINING_FLEET_TOKEN } else { $env:XMRIG_FLEET_TOKEN }),

    # Folder holding the published agent. Empty means: fetch the newest release.
    [string]$SourcePath = '',

    [string]$InstallPath = 'C:\Program Files\xmrig-fleet-agent',

    [int]$Port = 47800,

    # Loopback port the agent starts xmrig's own HTTP API on.
    [int]$XmrigApiPort = 47801,

    [string]$ServiceName = 'xmrig-fleet-agent'
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell.'
}

if ([string]::IsNullOrWhiteSpace($Token)) {
    throw 'No fleet token. Pass -Token, or set $env:MINING_FLEET_TOKEN before piping this script to iex.'
}

$exeName = 'xmrig-fleet-agent.exe'

# No payload given: pull the agent for this platform out of the newest release.
if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $repo = if ($env:MINING_FLEET_REPO) { $env:MINING_FLEET_REPO } elseif ($env:XMRIG_FLEET_REPO) { $env:XMRIG_FLEET_REPO } else { 'XYphrodite/xmrig-fleet' }
    $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'arm64' } else { 'x64' }
    $assetNames = @("mining-fleet-agent-win-$arch.zip", "xmrig-fleet-agent-win-$arch.zip")

    Write-Host "==> Fetching agent from the newest release of $repo"
    $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'mining-fleet-agent-installer' } -TimeoutSec 30
    $asset = $null
    foreach ($assetName in $assetNames) {
        $asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
        if ($asset) { break }
    }
    if (-not $asset) { throw "Release $($release.tag_name) carries no $($assetNames -join ' or ')." }

    $archive = Join-Path ([IO.Path]::GetTempPath()) "xmrig-fleet-agent-$([guid]::NewGuid().ToString('N')).zip"
    $SourcePath = Join-Path ([IO.Path]::GetTempPath()) "xmrig-fleet-agent-$([guid]::NewGuid().ToString('N'))"
    Invoke-WebRequest $asset.browser_download_url -OutFile $archive -UseBasicParsing
    Expand-Archive $archive $SourcePath -Force
    Remove-Item $archive -Force -ErrorAction SilentlyContinue
    Write-Host "    $($release.tag_name) unpacked"
}

if (-not (Test-Path $SourcePath)) {
    throw "Published agent not found at $SourcePath. Run deploy\publish.ps1 first, or omit -SourcePath to download a release."
}
if (-not (Test-Path (Join-Path $SourcePath $exeName))) {
    throw "$exeName is missing from $SourcePath."
}

# Stop an existing service so its files can be replaced.
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    # sc.exe delete returns before the service object disappears; wait it out.
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# An agent someone started by hand to diagnose something keeps the port, and then the service
# cannot bind and refuses to start with a message that names no cause. That cost an evening
# once; it is not allowed to cost another.
$stray = Get-Process -Name 'xmrig-fleet-agent' -ErrorAction SilentlyContinue
if ($stray) {
    Write-Host "Closing $($stray.Count) agent process(es) left running outside the service..."
    $stray | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

Write-Host "Copying agent to $InstallPath"
New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallPath -Recurse -Force

$settings = [ordered]@{
    Agent = [ordered]@{
        Token          = $Token
        ListenUrl      = "http://0.0.0.0:$Port"
        XmrigApiPort   = $XmrigApiPort
        AutoStartMiner = $false
    }
    Logging = @{ LogLevel = @{ Default = 'Information'; 'Microsoft.AspNetCore' = 'Warning' } }
    AllowedHosts = '*'
}
$settings | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $InstallPath 'appsettings.json') -Encoding utf8

# The agent port must not be reachable from the LAN or the internet: only from the tailnet.
$ruleName = "xmrig-fleet-agent ($Port)"
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule `
    -DisplayName $ruleName `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort $Port `
    -RemoteAddress '100.64.0.0/10' `
    -Profile Any | Out-Null
Write-Host "Firewall: allowed TCP $Port from the tailnet range only (100.64.0.0/10)."

$binPath = '"{0}"' -f (Join-Path $InstallPath $exeName)
& sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= "xmrig fleet agent" | Out-Null
& sc.exe description $ServiceName "Controls xmrig and reports hardware telemetry to the mining-fleet console." | Out-Null
# Restart the service if it ever crashes, so a node does not silently drop out of the fleet.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

Start-Service -Name $ServiceName
Start-Sleep -Seconds 2

$service = Get-Service -Name $ServiceName
Write-Host "Service '$ServiceName' is $($service.Status)."

try {
    $response = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/v1/info" -Headers @{ 'X-Fleet-Token' = $Token } -TimeoutSec 10
    Write-Host "Agent $($response.agentVersion) responding on $($response.hostname). Elevated: $($response.isElevated)."
    $tailscaleIp = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -like '100.*' } | Select-Object -First 1).IPAddress
    if ($tailscaleIp) { Write-Host "Add this node to the console as: $tailscaleIp`:$Port" }
} catch {
    Write-Warning "Service started but the API did not answer: $($_.Exception.Message)"
    # Not the event log: the agent no longer writes there, because on one node the Event Log
    # service answers "RPC server unavailable" and the write took the whole agent down with it.
    Write-Warning "Look in $(Join-Path $InstallPath 'agent.log') for the reason."
}

# On PATH so the agent can be run by hand from anywhere for diagnostics. Safe only because it
# now reads appsettings.json from its own directory rather than the current one - without that,
# running it from elsewhere silently loads no configuration and reports an empty token.
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
if ($machinePath -notlike "*$InstallPath*") {
    [Environment]::SetEnvironmentVariable('Path', ($machinePath.TrimEnd(';') + ';' + $InstallPath), 'Machine')
    Write-Host "Added $InstallPath to PATH (opens in a new shell)."
}
