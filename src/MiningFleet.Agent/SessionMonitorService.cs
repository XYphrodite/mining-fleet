using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MiningFleet.Agent;

/// <summary>
/// Keeps Task Manager running, hidden, in the node's logged-on session.
///
/// A remedy without a diagnosis, and the code says so deliberately so nobody deletes it as
/// superstition. Measured on an i7-12700KF, the miner untouched between readings:
///
///     nothing watching            4 380 H/s      15.7 GB in use
///     Task Manager open           7 092 H/s       7.8 GB in use
///     Resource Monitor open       7 097 H/s       ~8 GB in use
///
/// Eleven explanations were tested and discarded, each by a controlled A/B on that node: huge
/// pages, free memory, CPU frequency, competing processes, xmrig's priority, the High
/// Performance power plan, a 1 ms timer resolution, opting out of EcoQoS, polling the same
/// counters from this service, Win32PrioritySeparation, and simply having a window open -
/// Notepad changes nothing.
///
/// Why CreateProcessAsUser rather than Process.Start: the agent is a service, so it lives in
/// session 0, which has no desktop. A process started the ordinary way lands there too, and the
/// effect does not happen - the counter pump proved that from the same session and achieved
/// nothing. Reaching the interactive session takes the user's own token, which a service running
/// as SYSTEM may borrow.
///
/// The one limit no implementation removes: somebody has to be logged on. With no session there
/// is no desktop to put a window on.
/// </summary>
public sealed class SessionMonitorService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private readonly MinerConfigStore _config;
    private readonly ILogger<SessionMonitorService> _log;
    private readonly object _gate = new();

    private int _startedPid;
    private string _startedLabel = "monitor";

    public SessionMonitorService(MinerConfigStore config, ILogger<SessionMonitorService> log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>
    /// What the monitor last did, carried to the console in <see cref="NodeSnapshotDto.MonitorNotice"/>.
    ///
    /// Every caller used to throw this sentence away - the agent discarded what <see cref="Apply"/>
    /// returned and the console printed "session monitor on" regardless - so a node whose window
    /// would not open looked identical to one where it had. That is how mks68i7rtx ran with the
    /// setting on, no window, and 60% of its hashrate, with nothing anywhere saying so.
    /// </summary>
    public string Notice { get; private set; } = "Session monitor has not run yet.";

    /// <summary>Acts on a freshly pushed setting. Returns what happened, for the console to show.</summary>
    public string Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            return Notice = "Session monitor is a Windows workaround; nothing to do on this platform.";

        if (!enabled)
        {
            StopMonitor();
            return Notice;
        }

        return EnsureRunningNotice();
    }

    private string EnsureRunningNotice()
        => Notice = EnsureRunning(out var detail)
            ? $"Session monitor on: {detail}"
            : $"Session monitor could not start: {detail}";

    /// <summary>
    /// Re-launches the window if it was closed, and stops it when the setting is turned off.
    /// Also covers the case the scheduled-task version could not: a node that boots and logs on
    /// long after the agent started still gets its window, without waiting for a logon event.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows()) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_config.Current.KeepMonitorOpen == true) EnsureRunningNotice();
                else StopMonitor();
            }
            catch (Exception ex)
            {
                // This service only makes mining faster. It must never be the reason a node
                // stops answering the console.
                _log.LogDebug(ex, "Session monitor check failed");
            }

            try { await Task.Delay(CheckInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private bool EnsureRunning(out string detail)
    {
        lock (_gate)
        {
            if (IsOurProcessAlive())
            {
                detail = $"{_startedLabel} already running as pid {_startedPid}";
                return true;
            }

            if (FindLoggedOnSession() is not { } session)
            {
                detail = "nobody is logged on to this node yet; the window will appear as soon as somebody is";
                return false;
            }

            if (Adopt(session) is { } adopted)
            {
                detail = $"adopted the {_startedLabel} already open in session {session} as pid {adopted}";
                return true;
            }

            var started = Launch(session, out detail);
            if (!started) _log.LogWarning("Session monitor could not start: {Detail}", detail);
            return started;
        }
    }

    private void StopMonitor()
    {
        lock (_gate)
        {
            Notice = "Session monitor off.";
            if (_startedPid == 0) return;

            try
            {
                // Only the window this service is tracking, which since Adopt may be one it did
                // not start. That trade is deliberate: turning the workaround off from the console
                // has to actually stop it, or the operator is left with a setting that reads "off"
                // over a node that is still being kept awake. The cost is that an operator sitting
                // at the rig with their own Task Manager open loses it - they can reopen it.
                using var process = Process.GetProcessById(_startedPid);
                process.Kill();
                _log.LogInformation("Session monitor stopped {Label} (pid {Pid})", _startedLabel, _startedPid);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }

            _startedPid = 0;
        }
    }

    private bool IsOurProcessAlive()
    {
        if (_startedPid == 0) return false;

        try
        {
            using var process = Process.GetProcessById(_startedPid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _startedPid = 0;
            return false;
        }
    }

    /// <summary>
    /// A window known to produce the effect. More than one exists because more than one was
    /// measured on the i7-12700KF and they are worth the same - 7 092 H/s with Task Manager,
    /// 7 097 H/s with Resource Monitor - so a node whose Task Manager will not run is not a node
    /// that has to go without the workaround.
    ///
    /// <para><see cref="ProcessNames"/> is a list because the executable that is launched is not
    /// the process that survives: resmon.exe is a stub that starts perfmon.exe and exits.</para>
    /// </summary>
    private sealed record MonitorKind(string Executable, string[] ProcessNames, string Label);

    /// <summary>
    /// Tried in order, and Resource Monitor comes first for a reason that cost an operator an
    /// afternoon: Task Manager is single-instance per session, so a hidden one does not merely sit
    /// there - it swallows every later attempt to open Task Manager. Measured directly, with no
    /// instance running to begin with:
    ///
    ///     start hidden          one process, no window
    ///     start again, normally same process, still no window, and no new one
    ///
    /// The person at the machine presses Ctrl+Shift+Esc and nothing whatsoever happens. Resource
    /// Monitor is worth the same hashrate (7 097 H/s against 7 092) and is a tool nobody reaches
    /// for daily, so squatting on it costs far less. Task Manager stays as the fallback for a node
    /// where Resource Monitor will not run.
    ///
    /// Both are single-instance, so this moves the nuisance rather than removing it. Removing it
    /// means a purpose-built helper that no human ever wants to open, which is the plan.
    ///
    /// <para><see cref="ProcessNames"/> is a list because the executable that is launched is not
    /// the process that survives: resmon.exe is a stub that starts perfmon.exe and exits.</para>
    /// </summary>
    private static readonly MonitorKind[] Monitors =
    {
        new("resmon.exe", new[] { "resmon", "perfmon" }, "Resource Monitor"),
        new("taskmgr.exe", new[] { "Taskmgr" }, "Task Manager"),
    };

    /// <summary>
    /// Takes over a monitor already running in the target session, or null if there is none.
    ///
    /// The tracked pid lives in this process and nowhere else, so every agent restart - and the
    /// self-update restarts on purpose - used to forget the window it had opened and start a
    /// second one beside it. Four updates in an evening left four Task Managers on the node, and
    /// the measurement they exist to produce had to be cleaned up by hand before it meant
    /// anything. Adopting instead of launching keeps the count at one, whoever opened it.
    /// </summary>
    private int? Adopt(uint session)
    {
        foreach (var kind in Monitors)
        {
            if (FindInSession(kind, session, excludePid: 0) is not { } pid) continue;

            _startedPid = pid;
            _startedLabel = kind.Label;
            _log.LogInformation("Session monitor adopted the {Label} already open in session {Session} (pid {Pid})", kind.Label, session, pid);
            return pid;
        }

        return null;
    }

    /// <summary>
    /// The live window of this kind in the given session, or null. Never throws: a process that
    /// exits mid-enumeration is ordinary, and this service must never be why a node stops
    /// answering the console.
    /// </summary>
    private int? FindInSession(MonitorKind kind, uint session, int excludePid)
    {
        foreach (var name in kind.ProcessNames)
        {
            Process[] running;
            try
            {
                running = Process.GetProcessesByName(name);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                _log.LogDebug(ex, "Could not look for a running {Name}", name);
                continue;
            }

            try
            {
                foreach (var process in running)
                {
                    try
                    {
                        if (process.Id == excludePid || process.SessionId != session || process.HasExited) continue;
                        return process.Id;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        _log.LogDebug(ex, "Could not read {Name} pid {Pid}", name, process.Id);
                    }
                }
            }
            finally
            {
                foreach (var process in running) process.Dispose();
            }
        }

        return null;
    }

    /// <summary>
    /// The session a person is actually sitting in, or null when nobody is.
    ///
    /// Deliberately not WTSGetActiveConsoleSessionId: that returns the physical console, which
    /// on a machine administered over RDP is a connected-but-empty session while the operator
    /// is somewhere else entirely. These rigs are all reached by RDP, so aiming at the console
    /// put the window in a session with nobody in it and the workaround did nothing at all.
    /// </summary>
    private uint? FindLoggedOnSession()
    {
        var buffer = IntPtr.Zero;
        try
        {
            if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out buffer, out var count)) return null;

            var size = Marshal.SizeOf<WtsSessionInfo>();
            uint? fallback = null;

            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WtsSessionInfo>(buffer + i * size);
                if (info.SessionId == 0) continue;   // services

                var user = QuerySession(info.SessionId, WtsUserName);
                if (string.IsNullOrWhiteSpace(user)) continue;

                // An active session is the one being looked at; a merely connected one still has
                // a desktop and is better than nothing if that is all this node has.
                if (info.State == WtsActive) return info.SessionId;
                fallback ??= info.SessionId;
            }

            return fallback;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _log.LogDebug(ex, "Could not enumerate sessions");
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    private static string? QuerySession(uint session, int infoClass)
    {
        var buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out buffer, out _)) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    private const int WtsUserName = 5;
    private const int WtsActive = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public uint SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint session, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    /// <summary>
    /// Puts a monitor in the session, trying each kind until one of them is still there
    /// afterwards. The detail on failure names every kind that was tried and why each failed,
    /// because "could not start" alone sent the last investigation to the wrong machine.
    /// </summary>
    private bool Launch(uint session, out string detail)
    {
        var failures = new List<string>();

        foreach (var kind in Monitors)
        {
            if (!Spawn(kind, session, out var pid, out var why))
            {
                failures.Add($"{kind.Label} - {why}");
                _log.LogWarning("Session monitor could not start {Label} in session {Session}: {Why}", kind.Label, session, why);
                continue;
            }

            _startedPid = pid;
            _startedLabel = kind.Label;
            _log.LogInformation("Session monitor started {Label} in session {Session} as pid {Pid}", kind.Label, session, pid);
            detail = $"started {kind.Label} in session {session} as pid {pid}";
            return true;
        }

        detail = string.Join("; ", failures);
        return false;
    }

    /// <summary>
    /// How long a launch is given to produce a window that is still standing.
    ///
    /// <see cref="HandOverGrace"/> comes first because the pid CreateProcessAsUser returns is
    /// usually a corpse: Task Manager restarts itself under the unfiltered administrator token
    /// and resmon.exe hands over to perfmon.exe, both within about a second. Measured on
    /// desktop-ib88isg: the agent launched pid 26852 and the live window was pid 30052, its
    /// child. Believing the returned pid is what made this service log a fresh launch every
    /// thirty seconds and made turning the workaround off from the console kill nothing.
    /// </summary>
    private static readonly TimeSpan HandOverGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits out the hand-over and returns the pid of whatever is actually still running.
    ///
    /// CreateProcessAsUser succeeding only means a process was created. A monitor that cannot
    /// load at all - the comctl32 entry-point failure on desktop-ib88isg - also "starts" and then
    /// dies, and reporting that as success let a node run for hours with the console saying the
    /// workaround was on and no window anywhere.
    /// </summary>
    private bool Settle(MonitorKind kind, uint session, int launchedPid, out int pid, out string detail)
    {
        var handOver = DateTime.UtcNow + HandOverGrace;
        var deadline = DateTime.UtcNow + SettleTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (FindInSession(kind, session, excludePid: launchedPid) is { } survivor)
            {
                pid = survivor;
                detail = $"pid {survivor}";
                return true;
            }

            // Only once the hand-over has had its moment is the launched process itself the
            // window; before that it is very likely the stub that is about to exit.
            if (DateTime.UtcNow >= handOver && IsAlive(launchedPid))
            {
                pid = launchedPid;
                detail = $"pid {launchedPid}";
                return true;
            }

            Thread.Sleep(250);
        }

        pid = 0;
        detail = IsAlive(launchedPid)
            ? $"pid {launchedPid} started but no window of its own ever appeared"
            : $"pid {launchedPid} exited immediately and left nothing behind";
        return false;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private bool Spawn(MonitorKind kind, uint session, out int pid, out string detail)
    {
        pid = 0;

        var userToken = IntPtr.Zero;
        var primaryToken = IntPtr.Zero;
        var environment = IntPtr.Zero;

        try
        {
            if (!WTSQueryUserToken(session, out userToken))
            {
                detail = $"could not borrow the session token (error {Marshal.GetLastWin32Error()})";
                return false;
            }

            if (!DuplicateTokenEx(userToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
            {
                detail = $"could not duplicate the session token (error {Marshal.GetLastWin32Error()})";
                return false;
            }

            // A null block is not fatal - CreateProcessAsUser then hands the child the service's
            // own environment - but it is the environment of SYSTEM in session 0, so say so
            // rather than let a mysterious launch failure be investigated from scratch.
            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
                _log.LogWarning("Session monitor could not build the user's environment block (error {Error}); falling back to the service's own", Marshal.GetLastWin32Error());

            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                // Without the desktop the process starts with nowhere to draw and the effect is lost.
                lpDesktop = @"winsta0\default",
                dwFlags = StartfUseShowWindow,
                // Hidden rather than merely minimised: the operator should not have a Task
                // Manager button on their taskbar for the lifetime of the machine. Whether the
                // window has to be *visible* for the effect is unknown - a visible Notepad does
                // nothing and an invisible counter poll does nothing either, so visibility was
                // never isolated. If the hashrate drops back with this, that is itself a finding.
                wShowWindow = SwHide,
            };

            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var command = Path.Combine(system, kind.Executable);

            if (!File.Exists(command))
            {
                detail = $"{command} is not on this node";
                return false;
            }

            // System32 rather than null: a null working directory hands the child whatever the
            // service happens to be sitting in, which is the agent's install directory. Nothing
            // about a monitor should depend on that, and the DLL search order reads it.
            if (!CreateProcessAsUser(primaryToken, command, null, IntPtr.Zero, IntPtr.Zero, false,
                    CreateUnicodeEnvironment | CreateNoWindow, environment, system, ref startup, out var info))
            {
                detail = $"CreateProcessAsUser failed (error {Marshal.GetLastWin32Error()})";
                return false;
            }

            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);

            return Settle(kind, session, info.dwProcessId, out pid, out detail);
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseShowWindow = 0x00000001;
    private const short SwHide = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint desiredAccess, IntPtr attributes,
        int impersonationLevel, int tokenType, out IntPtr duplicate);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, string? commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

