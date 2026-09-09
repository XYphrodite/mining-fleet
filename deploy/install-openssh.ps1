<#
.SYNOPSIS
    Installs and starts the built-in Windows OpenSSH server on a mining node, reachable
    from the tailnet only.

.DESCRIPTION
    Nodes are diagnosed by hand far more often than the fleet API allows: the agent
    exposes miner control and telemetry, not a shell, so a node with no SSH server can
    only be inspected by sitting at it. This script turns on the OpenSSH server that
    ships with Windows 10/11 as an optional feature, registers it to start with the
    machine, and opens port 22 to 100.64.0.0/10 the way install-agent.ps1 opens 47800 -
    the tailnet is the security boundary, so the port must never face the LAN.

    That component is a Feature on Demand, fetched from Windows Update rather than from the
    installation media, and on a node whose updates are pointed at a WSUS server that does
    not carry it the install fails with REGDB_E_CLASSNOTREG after a long silent wait. Seen
    on a live node. When that happens the script falls back to the upstream MSI, which needs
    nothing from Windows Update - see Install-FromRelease for why that is a fallback and not
    the default.

    Authentication is by public key. A password-authenticated SSH server on a mining rig
    is an invitation, and the operator console already reaches these nodes without one.
    Pass -PublicKey (or set $env:MINING_FLEET_SSH_KEY) and the key is installed for
    -UserName. Which file that means is decided by whether the account is an
    administrator: Windows' sshd_config sends every admin login to one shared
    administrators_authorized_keys and ignores that account's own ~\.ssh, so a key
    written to the wrong one of the two is silently never read.

    Run this in an elevated PowerShell on the node. It can also be run straight from the
    web, which avoids the execution policy that blocks a downloaded .ps1 file:

        $env:MINING_FLEET_SSH_KEY  = 'ssh-ed25519 AAAA... operator'
        $env:MINING_FLEET_SSH_USER = 'local'
        irm https://raw.githubusercontent.com/XYphrodite/mining-fleet/master/deploy/install-openssh.ps1 | iex

    That URL is cached for five minutes, so a node re-run straight after a push can be handed
    the previous copy. Check the version this prints against $ScriptVersion below; when they
    differ, fetch the commit instead of the branch:

        .../mining-fleet/<commit sha>/deploy/install-openssh.ps1

    Nothing here touches xmrig or the fleet agent: installing SSH does not interrupt
    mining.

.EXAMPLE
    .\install-openssh.ps1 -PublicKey (Get-Content ~\.ssh\id_ed25519.pub -Raw)
#>
[CmdletBinding()]
param(
    # Operator's SSH public key, authorised for administrator logins. Falls back to
    # $env:MINING_FLEET_SSH_KEY (or XMRIG_FLEET_SSH_KEY) so the script also works when
    # piped to iex, which cannot pass arguments.
    [string]$PublicKey = $(if ($env:MINING_FLEET_SSH_KEY) { $env:MINING_FLEET_SSH_KEY } else { $env:XMRIG_FLEET_SSH_KEY }),

    # Account the key logs in as. An elevated shell may be running as a different
    # administrator than the one the operator logs in as, so this is worth naming.
    [string]$UserName = $(if ($env:MINING_FLEET_SSH_USER) { $env:MINING_FLEET_SSH_USER } elseif ($env:XMRIG_FLEET_SSH_USER) { $env:XMRIG_FLEET_SSH_USER } else { $env:USERNAME }),

    # Leave password authentication on. Off by default: see the description.
    [switch]$AllowPasswordAuth,

    [int]$Port = 22,

    # Remote range allowed through the firewall. The tailnet CGNAT range by default.
    [string]$RemoteAddress = '100.64.0.0/10'
)

$ErrorActionPreference = 'Stop'

# Bump this whenever the script changes. raw.githubusercontent.com serves with
# Cache-Control: max-age=300, so for five minutes after a push a node can still be handed
# the previous copy - and an operator then reads output that does not match the source and
# concludes the fix did not work. That happened twice in one evening. Printing the version
# turns "which copy ran" from a deduction into the first line of output.
$ScriptVersion = '2026-09-03.2'
Write-Host "install-openssh.ps1 $ScriptVersion"

# Records that this node cannot fetch Features on Demand, so later runs skip the wait
# rather than rediscovering it five minutes at a time. See Get-BlockedUpdateReason.
$script:CapabilityGaveUpMarker = Join-Path $env:ProgramData 'xmrig-fleet\openssh-capability-unavailable.txt'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell.'
}

if ([string]::IsNullOrWhiteSpace($PublicKey) -and -not $AllowPasswordAuth) {
    throw 'No public key. Pass -PublicKey, set $env:MINING_FLEET_SSH_KEY, or accept the risk with -AllowPasswordAuth.'
}

# ---------------------------------------------------------------- install the feature

<#
.SYNOPSIS
    Installs OpenSSH from the upstream release when the Windows component will not.

.DESCRIPTION
    Deliberately a fallback rather than the default. The component that ships with Windows
    is serviced by Windows Update; a copy installed from a release is not, and somebody has
    to remember it exists. But a node that cannot install the component is a node that can
    only ever be diagnosed by sitting at it, which is the very thing this script exists to
    avoid, so the fallback wins over having no shell at all.

    Upstream tags every build "Preview" - it is their long-standing habit, not a warning
    about this particular one - so a release is chosen by date and that label is ignored.
#>
function Install-FromRelease {
    Write-Host '==> Installing OpenSSH from the upstream release instead'

    $api = 'https://api.github.com/repos/PowerShell/Win32-OpenSSH/releases?per_page=10'
    $releases = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'xmrig-fleet' }

    $arch = if ([Environment]::Is64BitOperatingSystem) {
        if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'Win64' }
    } else { 'Win32' }

    $asset = $releases |
        Sort-Object { [datetime]$_.published_at } -Descending |
        ForEach-Object { $_.assets } |
        Where-Object { $_.name -like "OpenSSH-$arch-*.msi" } |
        Select-Object -First 1

    if (-not $asset) {
        throw "No OpenSSH-$arch MSI in the last releases of PowerShell/Win32-OpenSSH. Install it by hand."
    }

    $msi = Join-Path $env:TEMP $asset.name
    Write-Host "    downloading $($asset.name)"
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $msi -UseBasicParsing

    # ADDLOCAL=Server: the client half is not wanted on a rig, and installing it would put
    # a second ssh.exe ahead of the one Windows ships on PATH.
    $arguments = "/i `"$msi`" ADDLOCAL=Server /qn /norestart"

    # 1618 is ERROR_INSTALL_ALREADY_RUNNING, and on this path it is close to expected: the
    # abandoned component install leaves TiWorker finishing what it started, and Windows
    # Installer will not begin while servicing holds the lock. Waiting is the whole remedy -
    # killing TiWorker mid-transaction is how a component store gets broken for real.
    for ($attempt = 1; ; $attempt++) {
        $run = Start-Process msiexec.exe -ArgumentList $arguments -Wait -PassThru
        if ($run.ExitCode -eq 0) { break }

        if ($run.ExitCode -ne 1618 -or $attempt -ge 20) {
            throw "msiexec failed with exit code $($run.ExitCode) installing $($asset.name)."
        }

        if ($attempt -eq 1) { Write-Host '    another installer holds the lock; waiting for it' }
        Start-Sleep -Seconds 15
    }

    Remove-Item $msi -ErrorAction SilentlyContinue
    Write-Host '    installed'
}

<#
.SYNOPSIS
    Says why this node cannot fetch a Feature on Demand, or $null when it looks able to.

.DESCRIPTION
    Both causes seen so far make the component install hang for minutes before failing, so
    it is worth asking first. Neither check is conclusive - Windows Update can be broken in
    ways no registry key admits to - so a $null here is "no known reason", not a promise.
#>
function Get-BlockedUpdateReason {
    # A node that has already spent five minutes proving it cannot fetch the package should
    # not spend another five on the next run. Written beside the agent's own state rather
    # than in the registry so it is obvious, greppable, and trivial to delete when somebody
    # fixes whatever is wrong with Windows Update on that machine.
    if (Test-Path $script:CapabilityGaveUpMarker) {
        return "a previous run already found no component source here (delete $script:CapabilityGaveUpMarker to try again)"
    }

    $policy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'
    $value = (Get-ItemProperty -Path $policy -Name UseWUServer -ErrorAction SilentlyContinue).UseWUServer
    if ($value -eq 1) {
        # Features on Demand are then searched for on the WSUS server, which usually does not
        # carry them, and the call fails with REGDB_E_CLASSNOTREG.
        return 'this node takes updates from a WSUS server (UseWUServer=1), which will not have the package'
    }

    $service = Get-Service -Name wuauserv -ErrorAction SilentlyContinue
    if ($service -and $service.StartType -eq 'Disabled') {
        return 'the Windows Update service is disabled, so there is nowhere to fetch the package from'
    }

    return $null
}

<#
.SYNOPSIS
    Installs a Windows capability, giving up rather than hanging, and falling back to the MSI.

.DESCRIPTION
    Add-WindowsCapability offers no timeout of its own, and on a node that cannot reach a
    source it sits on a progress bar for a long time before admitting defeat - an operator
    watched it twice. Run as a job so there is something to abandon.

    Abandoning a running DISM operation is not free: the component store can be left with
    work pending, which a later "DISM /Online /Cleanup-Image /RestoreHealth" clears. That is
    said out loud rather than hidden, and it is still better than a script that never returns.
#>
function Install-Capability {
    param([Parameter(Mandatory)][string]$Name, [int]$TimeoutMinutes = 5)

    $job = Start-Job -ScriptBlock { Add-WindowsCapability -Online -Name $using:Name }

    if (Wait-Job $job -Timeout ($TimeoutMinutes * 60)) {
        try {
            Receive-Job $job -ErrorAction Stop | Out-Null
            Remove-Job $job
            Write-Host '    installed'
            return
        }
        catch {
            Remove-Job $job -Force
            Write-Host "    the Windows component would not install: $($_.Exception.Message)" -ForegroundColor Yellow
            Install-FromRelease
            return
        }
    }

    Stop-Job $job; Remove-Job $job -Force
    Write-Host "    gave up after $TimeoutMinutes minutes; this node cannot reach a component source" -ForegroundColor Yellow
    Write-Host '    if Windows features misbehave later, run: DISM /Online /Cleanup-Image /RestoreHealth' -ForegroundColor Yellow

    $null = New-Item -ItemType Directory -Force -Path (Split-Path $script:CapabilityGaveUpMarker)
    Set-Content -Path $script:CapabilityGaveUpMarker -Encoding ascii -Value @(
        "Add-WindowsCapability for OpenSSH.Server did not finish within $TimeoutMinutes minutes on $(Get-Date -Format s).",
        'install-openssh.ps1 now goes straight to the upstream MSI on this node.',
        'Delete this file to make it try the Windows component again.')

    Install-FromRelease
}

# The MSI below registers sshd too, so an sshd that already exists is the end of this
# step whichever way it got there. Checked before the capability, because a node
# installed from the MSI reports the capability as absent and would be sent round the
# loop that failed in the first place.
if (Get-Service -Name sshd -ErrorAction SilentlyContinue) {
    Write-Host '==> sshd is already installed on this node'
}
else {
    $capability = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' |
        Sort-Object Name | Select-Object -First 1

    if ($capability -and $capability.State -eq 'Installed') {
        Write-Host "==> $($capability.Name) already installed"
    }
    elseif ($capability -and ($blocked = Get-BlockedUpdateReason)) {
        # Asked before trying rather than after failing, because the failure mode here is not
        # a quick error - it is minutes of a progress bar that never moves. Skipping a doomed
        # attempt is also kinder to the component store than aborting one halfway.
        Write-Host "==> Skipping the Windows component: $blocked" -ForegroundColor Yellow
        Install-FromRelease
    }
    elseif ($capability) {
        # A Feature on Demand, so it is fetched from Windows Update rather than from the
        # installation media. That makes this the one step on a mining node that depends on
        # Windows Update working, and on some nodes it does not: a WSUS policy points the
        # search at a server that does not carry the package, and the call fails with
        # REGDB_E_CLASSNOTREG (0x80040154) after a long silent wait. Observed on a live node.
        Write-Host "==> Installing $($capability.Name)"
        Install-Capability -Name $capability.Name
    }
    else {
        Write-Host '==> This Windows build does not offer the OpenSSH.Server component' -ForegroundColor Yellow
        Install-FromRelease
    }
}

# ----------------------------------------------------------------------- the services

# One run of sshd creates the host keys and the default sshd_config, so start it before
# editing either.
#
# Best-effort, not required. A node whose sshd_config is already bad cannot start the
# service at all, and this is the script that repairs such a config - so throwing here would
# make the repair unreachable and leave the node broken by the only tool able to fix it.
# That is exactly what happened on a live node. The restart after the config is written is
# the one that has to succeed, and it does throw.
Set-Service -Name sshd -StartupType Automatic
try {
    Start-Service -Name sshd
}
catch {
    Write-Host '    sshd would not start yet; carrying on to rewrite its configuration' -ForegroundColor Yellow
}

# The agent key service is what makes `ssh-add` work on the node itself; it is disabled
# out of the box and is not needed to accept logins, so leave a node's choice alone.
Write-Host "Service 'sshd' is $((Get-Service sshd).Status)."

# Restart sshd if it ever crashes, matching how the agent service is registered - a node
# that drops its shell is diagnosed the hard way.
& sc.exe failure sshd reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

# ------------------------------------------------------------------- authorised keys

if (-not [string]::IsNullOrWhiteSpace($PublicKey)) {
    $account = Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue
    if (-not $account) { throw "No local account named '$UserName' on this machine." }

    # Compared by SID: the group and the member names are both localised, so matching on
    # "Administrators" fails on a Russian Windows, which is what these nodes run.
    $adminGroup = Get-LocalGroup -SID 'S-1-5-32-544'
    $isAdmin = [bool](Get-LocalGroupMember -Group $adminGroup -ErrorAction SilentlyContinue |
        Where-Object { $_.SID -eq $account.SID })

    if ($isAdmin) {
        $keyFile = Join-Path $env:ProgramData 'ssh\administrators_authorized_keys'
        # Only Administrators and SYSTEM may write it, or sshd ignores the file and says
        # so only in its own log, where the operator will not think to look.
        # By SID, for the same reason the group membership above is checked by SID: these
        # accounts are localised, and "Administrators" on a Russian Windows makes icacls fail
        # with "no mapping between account names and security IDs". S-1-5-32-544 is the
        # Administrators group, S-1-5-18 is SYSTEM.
        $acl = { & icacls.exe $keyFile /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null }
    } else {
        $keyFile = Join-Path (Join-Path 'C:\Users' $UserName) '.ssh\authorized_keys'
        $acl = { & icacls.exe $keyFile /inheritance:r /grant "*$($account.SID.Value):F" /grant '*S-1-5-18:F' | Out-Null }
    }

    $null = New-Item -ItemType Directory -Force -Path (Split-Path $keyFile)
    $key = $PublicKey.Trim()
    $existing = if (Test-Path $keyFile) { Get-Content $keyFile } else { @() }

    if ($existing -contains $key) {
        Write-Host "==> Public key already authorised in $keyFile"
    } else {
        Write-Host "==> Authorising the public key for '$UserName' in $keyFile"
        # Rewritten whole rather than appended: Add-Content would inherit whatever
        # encoding the file already had, and sshd rejects a UTF-8 BOM in this file.
        $lines = @($existing | Where-Object { $_.Trim() }) + $key
        Set-Content -Path $keyFile -Value $lines -Encoding ascii
    }

    & $acl
    Write-Host "    $(if ($isAdmin) { "'$UserName' is an administrator, so sshd reads the shared admin key file" } else { "'$UserName' is a standard account" })"
}

# ---------------------------------------------------------------------- sshd_config

# Located through the service rather than assumed: the Windows component and the upstream
# MSI install sshd to different directories.
$imagePath = (Get-CimInstance Win32_Service -Filter "Name='sshd'").PathName
$sshdExe = $imagePath.Trim('"').Split('"')[0]

$configPath = Join-Path $env:ProgramData 'ssh\sshd_config'

# sshd writes this file on its first successful run, so a node where it has never started -
# which is precisely the node this script is trying to rescue - has no config to edit and
# would silently skip every setting below. Seed it from the default that ships beside the
# binary instead.
if (-not (Test-Path $configPath)) {
    $default = Join-Path (Split-Path $sshdExe) 'sshd_config_default'
    if (Test-Path $default) {
        Write-Host '==> sshd has never run here; seeding sshd_config from the shipped default'
        $null = New-Item -ItemType Directory -Force -Path (Split-Path $configPath)
        Copy-Item $default $configPath
    }
}

if (Test-Path $configPath) {
    $config = Get-Content $configPath

    function Set-SshOption {
        param([string[]]$Lines, [string]$Name, [string]$Value)

        # Comment out every existing occurrence, commented or not, then write one
        # authoritative line: sshd honours the *first* match, so editing in place would
        # leave a stale earlier line in charge.
        $out = @(foreach ($line in $Lines) {
            if ($line -match "^\s*#?\s*$Name\s") { "# (xmrig-fleet) $line" } else { $line }
        })

        # Placed before the first Match block rather than at the end of the file. Everything
        # after a Match line belongs to that block, and Windows ships an sshd_config that ends
        # with "Match Group administrators" - so appending put Port inside it, which sshd
        # rejects outright, and the service then would not start at all. Seen on a live node.
        $firstMatch = 0
        while ($firstMatch -lt $out.Count -and $out[$firstMatch] -notmatch '^\s*Match\s') { $firstMatch++ }

        if ($firstMatch -ge $out.Count) { return $out + "$Name $Value" }
        # Guarded because PowerShell reads $out[0..-1] as the whole array rather than none of it.
        if ($firstMatch -eq 0) { return @("$Name $Value") + $out }
        return $out[0..($firstMatch - 1)] + "$Name $Value" + $out[$firstMatch..($out.Count - 1)]
    }

    $config = Set-SshOption $config 'Port' $Port
    $config = Set-SshOption $config 'PubkeyAuthentication' 'yes'
    if (-not $AllowPasswordAuth) {
        $config = Set-SshOption $config 'PasswordAuthentication' 'no'
    }

    Set-Content -Path $configPath -Value $config -Encoding ascii
    Write-Host "sshd_config: port $Port, public keys on, passwords $(if ($AllowPasswordAuth) { 'left enabled' } else { 'off' })."

    # Asked before restarting, because a service that will not start says only that it would
    # not start. sshd -t names the file and line, which is the difference between a fix and
    # an evening.
    if (Test-Path $sshdExe) {
        $check = & $sshdExe -t -f $configPath 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "sshd rejected the configuration and was left running as it was:`n$($check -join "`n")"
        }
    }

    Restart-Service sshd
}

# ------------------------------------------------------------------------- firewall

# Windows adds its own "OpenSSH-Server-In-TCP" rule scoped to Any when the feature is
# installed. Disable it: the whole point is that this port faces the tailnet only.
Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue |
    Disable-NetFirewallRule

$ruleName = 'xmrig-fleet ssh (tailnet)'
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule `
    -DisplayName $ruleName `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort $Port `
    -RemoteAddress $RemoteAddress `
    -Profile Any | Out-Null
Write-Host "Firewall: allowed TCP $Port from $RemoteAddress only; the Windows OpenSSH rule is disabled."

# ---------------------------------------------------------------------- verification

$listening = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue
if ($listening) {
    Write-Host "sshd is listening on $Port."
} else {
    Write-Warning "sshd is not listening on $Port. Look in $(Join-Path $env:ProgramData 'ssh\logs\sshd.log')."
}

$hostname = $env:COMPUTERNAME.ToLowerInvariant()
Write-Host ''
Write-Host "Done. From the operator machine: ssh $env:USERNAME@$hostname"
