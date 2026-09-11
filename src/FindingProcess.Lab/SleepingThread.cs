using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FindingProcess.Lab;

internal sealed class SleepingThread : IDisposable
{
    private readonly SafeWaitHandle handle;
    private bool resumed;
    private SleepingThread(SafeWaitHandle handle) => this.handle = handle;

    // The polling fixture has ONE console caller. Pause only that thread, only while
    // it is in NtDelayExecution (Thread.Sleep), outside console and loader locks.
    // This is deliberately not a general-purpose suspend-all implementation.
    internal static SleepingThread Pause(Process target, uint tid)
    {
        if (tid == 0) throw new InvalidOperationException("No attested fixture main thread.");
        var thread = Native.OpenThread(0x0002 | 0x0008 | 0x0040, false, tid);
        Native.Check(!thread.IsInvalid, "Open fixture main thread");
        var suspended = false;
        var context = Marshal.AllocHGlobal(1232 + 16);
        var aligned = (nint)((context.ToInt64() + 15) & ~15L);
        try
        {
            if (Native.GetProcessIdOfThread(thread) != target.Id) throw new InvalidOperationException("Thread PID mismatch.");
            var module = NativeLibrary.Load("ntdll.dll");
            long start;
            try
            {
                var localStart = NativeLibrary.GetExport(module, "NtDelayExecution").ToInt64();
                using var local = Process.GetCurrentProcess();
                var localModule = local.Modules.Cast<ProcessModule>().Single(m => m.ModuleName.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase));
                var remoteModule = target.Modules.Cast<ProcessModule>().Single(m => m.FileName.Equals(localModule.FileName, StringComparison.OrdinalIgnoreCase));
                start = remoteModule.BaseAddress.ToInt64() + localStart - localModule.BaseAddress.ToInt64();
            }
            finally { NativeLibrary.Free(module); }
            var watch = Stopwatch.StartNew();
            long lastRip = 0;
            while (watch.Elapsed.TotalSeconds < 3)
            {
                var previous = Native.SuspendThread(thread);
                Native.Check(previous != uint.MaxValue, "Pause fixture main thread");
                suspended = true;
                if (previous != 0) throw new InvalidOperationException("Fixture was already suspended; refusing rebind.");
                Marshal.Copy(new byte[1232], 0, aligned, 1232);
                Marshal.WriteInt32(aligned, 48, 0x00100001); // AMD64 CONTEXT_CONTROL
                Native.Check(Native.GetThreadContext(thread, aligned), "Inspect paused fixture instruction pointer");
                lastRip = Marshal.ReadInt64(aligned, 248);
                if (lastRip >= start && lastRip < start + 32)
                {
                    suspended = false; // ownership moves to the lease
                    return new SleepingThread(thread);
                }
                Native.Check(Native.ResumeThread(thread) != uint.MaxValue, "Resume probe attempt");
                suspended = false;
                Thread.Sleep(1);
            }
            throw new InvalidOperationException($"No safe sleeping point found (RIP=0x{lastRip:X}, NtDelayExecution=0x{start:X}); no detach performed.");
        }
        catch
        {
            if (suspended) Native.ResumeThread(thread);
            thread.Dispose();
            throw;
        }
        finally { Marshal.FreeHGlobal(context); }
    }

    public void Dispose()
    {
        if (resumed) return;
        resumed = true;
        try { Native.Check(Native.ResumeThread(handle) != uint.MaxValue, "Resume migrated fixture main thread"); }
        finally { handle.Dispose(); }
    }
}
