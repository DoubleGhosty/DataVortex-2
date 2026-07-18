using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DataVortex.Core.Licensing;
using Microsoft.Extensions.Logging;

namespace DataVortex.App.Services;

/// <summary>Release-only runtime watchdog against dynamic tampering (a debugger attached to lift the token from
/// memory, single-step the gate, etc.). On detection it deliberately does NOT react at the check site — throwing
/// or shutting down there would flag the exact instruction to neutralise. Instead it schedules, after a RANDOM
/// delay, a silent <see cref="ILicenseGate.Trip"/> that makes every licensed capability go dark, and logs a vague
/// line. The distance between cause and effect is the point: the crash/denial surfaces far from the detection.
///
/// Never started in a Debug build — developers attach debuggers legitimately (see App startup).</summary>
public sealed class TamperGuard
{
    private readonly ILicenseGate _gate;
    private readonly ILogger<TamperGuard> _log;
    private DispatcherTimer? _timer;
    private bool _armed = true;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsDebuggerPresent();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, ref bool isPresent);

    public TamperGuard(ILicenseGate gate, ILogger<TamperGuard> log)
    {
        _gate = gate;
        _log = log;
    }

    public void Start()
    {
        Check();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    private void Check()
    {
        if (!_armed || !DebuggerAttached()) return;

        // Disarm so we schedule the reaction once, then react LATE and ELSEWHERE.
        _armed = false;
        _timer?.Stop();
        _log.LogDebug("Environment integrity check failed"); // intentionally vague
        var delayMs = Random.Shared.Next(20_000, 90_000);
        _ = Task.Delay(delayMs).ContinueWith(_ => _gate.Trip());
    }

    /// <summary>Several independent tells, OR-ed: the managed flag, the two native queries, and a timing probe a
    /// single-stepping debugger stretches. Any one firing is enough.</summary>
    private static bool DebuggerAttached()
    {
        if (Debugger.IsAttached || Debugger.IsLogging()) return true;

        try { if (IsDebuggerPresent()) return true; } catch { /* API unavailable */ }
        try
        {
            var present = false;
            if (CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, ref present) && present) return true;
        }
        catch { /* API unavailable */ }

        // A tight loop that normally runs in well under a millisecond; a step-debugger inflates it. The volatile
        // sink keeps the JIT from optimising the loop away.
        var sw = Stopwatch.StartNew();
        long acc = 0;
        for (int i = 0; i < 250_000; i++) acc += i;
        sw.Stop();
        return sw.ElapsedMilliseconds > 300 && acc != 0;
    }
}
