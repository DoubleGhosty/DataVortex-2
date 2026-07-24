using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DataVortex.Core.Licensing;
using Microsoft.Extensions.Logging;

namespace DataVortex.App.Services;

/// <summary>Release-only runtime watchdog against dynamic tampering (a debugger lifting the token from memory,
/// single-stepping the gate, or a binary patched after signing). On detection it deliberately does NOT react at the
/// check site — throwing/shutting down there would flag the exact instruction to neutralise. Instead it schedules,
/// after a RANDOM delay, a silent <see cref="ILicenseGate.Trip"/> that makes every licensed capability go dark, and
/// logs a vague line. The distance between cause and effect is the point. Never started in a Debug build (developers
/// attach debuggers legitimately).</summary>
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
        // One-time integrity check of the signed binary (inert until the exe is Authenticode-signed — Palier D.3).
        if (SignatureTampered()) { TripLater(); return; }

        CheckDebugger();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += (_, _) => CheckDebugger();
        _timer.Start();
    }

    private void CheckDebugger()
    {
        if (_armed && DebuggerAttached()) TripLater();
    }

    /// <summary>React LATE and ELSEWHERE: disarm, then schedule a silent trip after a random delay.</summary>
    private void TripLater()
    {
        if (!_armed) return;
        _armed = false;
        _timer?.Stop();
        _log.LogDebug("Environment integrity check failed"); // intentionally vague
        var delayMs = Random.Shared.Next(20_000, 90_000);
        _ = Task.Delay(delayMs).ContinueWith(_ => _gate.Trip());
    }

    /// <summary>Several independent tells, OR-ed: the managed flag, the two native queries, and a timing probe a
    /// single-stepping debugger stretches.</summary>
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

        var sw = Stopwatch.StartNew();
        long acc = 0;
        for (int i = 0; i < 250_000; i++) acc += i;
        sw.Stop();
        return sw.ElapsedMilliseconds > 300 && acc != 0;
    }

    // ---------------------------------------------------------------- Authenticode integrity (WinVerifyTrust)

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2, WTD_SAFER_FLAG = 0x100;
    private const int TRUST_E_BAD_DIGEST = unchecked((int)0x80096010); // signed, but the file bytes were changed after signing

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO { public uint cbStruct; public IntPtr pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData;
        public uint dwUIChoice; public uint fdwRevocationChecks; public uint dwUnionChoice; public IntPtr pFile;
        public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference;
        public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
    }

    /// <summary>True ONLY when the running exe carries a signature whose digest no longer matches the file (i.e. it
    /// was patched after signing). Unsigned (dev / current) or any other state ⇒ false, so it's inert until the exe
    /// is signed and never false-positives on a clean unsigned build. Fully guarded — never throws.</summary>
    private static bool SignatureTampered()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var file = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = Marshal.StringToCoTaskMemUni(exe),
            };
            IntPtr pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(file, pFile, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_SAFER_FLAG,
            };
            IntPtr pData = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_DATA>());
            Marshal.StructureToPtr(data, pData, false);
            try
            {
                int result = WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);

                // Close the trust state (frees hWVTStateData populated natively during verify).
                var updated = Marshal.PtrToStructure<WINTRUST_DATA>(pData);
                updated.dwStateAction = WTD_STATEACTION_CLOSE;
                Marshal.StructureToPtr(updated, pData, false);
                WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);

                return result == TRUST_E_BAD_DIGEST;
            }
            finally
            {
                Marshal.FreeCoTaskMem(file.pcwszFilePath);
                Marshal.FreeCoTaskMem(pFile);
                Marshal.FreeCoTaskMem(pData);
            }
        }
        catch { return false; }
    }
}
