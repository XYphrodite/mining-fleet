<#
.SYNOPSIS
    Enables Windows Remote Desktop and scopes it to the Tailscale tailnet only.

.DESCRIPTION
    Turns on RDP on a mining node / new machine and locks the firewall so TCP 3389
    is reachable only from 100.64.0.0/10 (the tailnet CGNAT range) - the same
    isolation install-agent.ps1 uses for 47800.

    Run in an elevated PowerShell on the node. Works on Windows 10/11 Pro/Enterprise/
    Server. Windows Home has no RDP host - the script detects it and aborts with a
    hint to use RustDesk instead (the fallback we use when Tailscale itself is down).

    Idempotent: re-running only tightens the firewall and re-applies the registry.

    Can also be piped from the web:

        irm https://raw.githubusercontent.com/XYphrodite/mining-fleet/master/deploy/enable-rdp.ps1 | iex

.PARAMETER Port
    RDP TCP port. Default 3389.

.PARAMETER RemoteAddress
    Allowed source range. Default 100.64.0.0/10 (tailnet).

.PARAMETER AllowNLA
    Keep Network Level Authentication on (default). Pass -AllowNLA:$false to allow
    older clients - not recommended.

.PARAMETER DisableNLA
    Alias for -AllowNLA:$false.

.EXAMPLE
    .\enable-rdp.ps1
    .\enable-rdp.ps1 -Port 3389 -RemoteAddress 100.64.0.0/10
#>
[CmdletBinding()]
param(
    [int]$Port = 3389,
    [string]$RemoteAddress = '100.64.0.0/10',
    [switch]$AllowNLA = $true,
    [switch]$DisableNLA
)

$ErrorActionPreference = 'Stop'

$ScriptVersion = '2026-09-18.1'
Write-Host "enable-rdp.ps1 $ScriptVersion"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell.'
}

if ($DisableNLA) { $AllowNLA = $false }

# --- Detect Windows edition (Home has no RDP host) ---
$edition = (Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue).Caption
$isHome = $edition -match 'Home'
if ($isHome) {
    Write-Host "This Windows edition is '$edition' - it has no RDP host." -ForegroundColor Yellow
    Write-Host "Use RustDesk as the out-of-band fallback (see deploy/install-rustdesk idea) or upgrade to Pro." -ForegroundColor Yellow
    throw 'RDP host not available on Windows Home.'
}

# --- Enable RDP via registry (fDenyTSConnections = 0) ---
$tsKey = 'HKLM:\System\CurrentControlSet\Control\Terminal Server'
$cur = (Get-ItemProperty -Path $tsKey -Name fDenyTSConnections -ErrorAction SilentlyContinue).fDenyTSConnections
if ($cur -ne 0) {
    Write-Host "==> Enabling Remote Desktop (fDenyTSConnections=0)"
    Set-ItemProperty -Path $tsKey -Name fDenyTSConnections -Value 0
} else {
    Write-Host "==> Remote Desktop already enabled"
}

# Keep NLA on (UserAuthentication = 1) unless explicitly disabled
$nlaKey = 'HKLM:\System\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp'
if ($AllowNLA) {
    Set-ItemProperty -Path $nlaKey -Name UserAuthentication -Value 1 -ErrorAction SilentlyContinue
    Write-Host "    NLA: on (UserAuthentication=1)"
} else {
    Set-ItemProperty -Path $nlaKey -Name UserAuthentication -Value 0 -ErrorAction SilentlyContinue
    Write-Host "    NLA: off (UserAuthentication=0) - less secure" -ForegroundColor Yellow
}

# Ensure TermService is running and auto-start
Set-Service -Name TermService -StartupType Automatic -ErrorAction SilentlyContinue
try { Start-Service -Name TermService -ErrorAction Stop; Write-Host "Service 'TermService' is Running." } catch { Write-Host "TermService start: $($_.Exception.Message)" -ForegroundColor Yellow }

# --- Firewall: scope 3389 to the tailnet only ---
# Windows ships 2-3 "Remote Desktop - User Mode (TCP-In)" rules (Domain/Private/Public) with RemoteAddress=Any.
# We don't delete them - we tighten them to the tailnet range, and add our own Any-profile rule.
# That way a future Windows update that recreates the built-in rule doesn't reopen RDP to the LAN.

$builtIn = Get-NetFirewallRule -DisplayName 'Remote Desktop - User Mode (TCP-In)' -ErrorAction SilentlyContinue
if ($builtIn) {
    foreach ($r in $builtIn) {
        try {
            Set-NetFirewallRule -Name $r.Name -RemoteAddress $RemoteAddress -ErrorAction Stop | Out-Null
            Write-Host "    Tightened '$($r.DisplayName)' ($($r.Profile)) -> $RemoteAddress"
        } catch { Write-Host "    Could not tighten $($r.Name): $($_.Exception.Message)" -ForegroundColor Yellow }
    }
}

# Our explicit Any-profile rule (covers Private/Public/Domain regardless of how the NIC is classified)
$ruleName = "mining-fleet rdp (tailnet)"
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule `
    -DisplayName $ruleName `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort $Port `
    -RemoteAddress $RemoteAddress `
    -Profile Any | Out-Null
Write-Host "Firewall: allowed TCP $Port from $RemoteAddress only (tailnet). Built-in RDP rules also scoped."

# --- Verification ---
$listening = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue
if ($listening) {
    Write-Host "RDP is listening on $Port (TermService)."
} else {
    Write-Warning "RDP is not listening on $Port yet. TermService may need a moment or a reboot."
}

$tailscaleIp = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -like '100.*' } | Select-Object -First 1).IPAddress
if ($tailscaleIp) {
    Write-Host "Try from another tailnet node: mstsc /v:$tailscaleIp  (or /v:$($env:COMPUTERNAME).tail08a9a5.ts.net)"
    Write-Host "Test: Test-NetConnection $tailscaleIp -Port $Port"
} else {
    Write-Host "Tailscale IP not found yet - run 'tailscale up' and check 'tailscale status'." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done. RDP is enabled and tailnet-scoped. Keep Tailscale running - without it this port is unreachable, which is the point."
