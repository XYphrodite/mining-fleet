using System.Diagnostics;
using System.Net.NetworkInformation;
using MiningFleet.Contracts;

namespace MiningFleet.Agent;

/// <summary>
/// Makes the observations a stand-down rule needs — is that port carrying traffic, is that program
/// running — and reduces them to a yes or no through <see cref="GpuPauseRule.Evaluate"/>.
///
/// Shared by the card's rule and the CPU miner's so the two cannot drift apart. They watch the same
/// kinds of thing for the same reason: somebody is at this machine and wants it. What differs is
/// only what each gives up in response.
/// </summary>
public static class UsageProbe
{
    /// <summary>
    /// Whether anything the rule watches is in use. Null means the observation could not be made,
    /// and a caller must hold its decision rather than read the silence as quiet.
    /// </summary>
    public static (bool Busy, string Description)? Observe(GpuPauseRuleDto rule)
    {
        try
        {
            return GpuPauseRule.Evaluate(rule, OpenConnections(rule.TcpPort), RunningAmong(rule.Processes));
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Established connections to the watched port, or zero when no port is watched.
    ///
    /// Read straight from the TCP table rather than through a cmdlet or WMI: measured on
    /// mks68i7rtx, Get-NetTCPConnection returns nothing at all from a service context.
    ///
    /// Matched on the local port across every address, deliberately not on loopback. Ollama binds
    /// the node's tailnet address, and a watchdog watching 127.0.0.1 sees nothing and reports a
    /// mining duty cycle of 100% forever.
    /// </summary>
    public static int OpenConnections(int? port) =>
        port is not { } watched
            ? 0
            : IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpConnections()
                .Count(c => c.LocalEndPoint.Port == watched && c.State == TcpState.Established);

    /// <summary>
    /// Which of the watched process names are running, from one snapshot of the process table.
    ///
    /// Taken once rather than per name because this runs every second: GetProcessesByName walks
    /// every process on the machine for each call, so a rule naming four games would walk it four
    /// times over to answer one question.
    /// </summary>
    public static IReadOnlyCollection<string> RunningAmong(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return [];

        var snapshot = Process.GetProcesses();
        try
        {
            return names
                .Where(name => snapshot.Any(p => string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
        finally
        {
            foreach (var process in snapshot) process.Dispose();
        }
    }
}
