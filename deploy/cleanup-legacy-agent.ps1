# Moves the agent from the old Program Files folder to mining-fleet-agent,
# points the service at the new exe, and deletes xmrig-fleet leftovers.
# Does not touch xmrig / lolMiner.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$old = 'C:\Program Files\xmrig-fleet-agent'
$new = 'C:\Program Files\mining-fleet-agent'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run elevated. This script does not stop the miner.'
}

$newSvc = Get-Service -Name 'mining-fleet-agent' -ErrorAction SilentlyContinue
$oldSvc = Get-Service -Name 'xmrig-fleet-agent' -ErrorAction SilentlyContinue
if (-not $newSvc -and -not $oldSvc) { throw 'No agent service found.' }

if ($newSvc -and $newSvc.Status -eq 'Running') {
    Write-Host "Stopping mining-fleet-agent (miner is not touched)..."
    Stop-Service mining-fleet-agent -Force
}
if ($oldSvc -and $oldSvc.Status -eq 'Running') {
    Write-Host "Stopping xmrig-fleet-agent (miner is not touched)..."
    Stop-Service xmrig-fleet-agent -Force
}
Start-Sleep -Seconds 3

Get-Process -Name 'mining-fleet-agent', 'xmrig-fleet-agent' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

if (Test-Path $old) {
    Write-Host "Copying $old -> $new"
    New-Item -ItemType Directory -Force -Path $new | Out-Null
    Copy-Item -Path (Join-Path $old '*') -Destination $new -Recurse -Force
}

$exe = Join-Path $new 'mining-fleet-agent.exe'
if (-not (Test-Path $exe)) { throw "mining-fleet-agent.exe missing at $new" }

if (-not $newSvc) {
    & sc.exe create mining-fleet-agent binPath= "`"$exe`"" start= auto DisplayName= "mining-fleet agent" | Out-Null
    & sc.exe failure mining-fleet-agent reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
} else {
    & sc.exe config mining-fleet-agent binPath= "`"$exe`"" start= auto | Out-Null
}

Remove-Item (Join-Path $new 'xmrig-fleet-agent.exe') -Force -ErrorAction SilentlyContinue

Write-Host 'Starting mining-fleet-agent...'
Start-Service mining-fleet-agent
Start-Sleep -Seconds 3
$running = Get-Service mining-fleet-agent
if ($running.Status -ne 'Running') { throw "mining-fleet-agent is $($running.Status)" }

if ($oldSvc) {
    & sc.exe delete xmrig-fleet-agent | Out-Null
}
Start-Sleep -Seconds 2
if (Test-Path $old) {
    Write-Host "Removing $old"
    Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $old) { Write-Warning "Could not fully remove $old (file in use). Miner is untouched." }
}

Get-NetFirewallRule -DisplayName 'xmrig-fleet-agent (47800)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if (-not (Get-NetFirewallRule -DisplayName 'mining-fleet-agent (47800)' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName 'mining-fleet-agent (47800)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 47800 -RemoteAddress '100.64.0.0/10' -Profile Any | Out-Null
}

$mp = [Environment]::GetEnvironmentVariable('Path', 'Machine')
$parts = @($mp -split ';' | Where-Object { $_ -and $_ -ne $old })
if ($parts -notcontains $new) { $parts += $new }
[Environment]::SetEnvironmentVariable('Path', ($parts -join ';'), 'Machine')

Write-Host "Service mining-fleet-agent is $((Get-Service mining-fleet-agent).Status) at $exe"
