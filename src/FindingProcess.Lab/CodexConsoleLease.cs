using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FindingProcess.Lab;

internal sealed class CodexConsoleLease : IDisposable
{
    private readonly Process target;
    private readonly int destinationPid;
    private readonly bool consoleReader;
    private readonly List<SafeWaitHandle> threads = [];
    private nint semaphore, cachedInput, backup;
    private bool committed;
    private bool cachedClosed;
    internal nint StandardInput { get; private set; }
    internal nint ReaderHandle => cachedInput;

    private CodexConsoleLease(Process target, int destinationPid, bool consoleReader) { this.target = target; this.destinationPid = destinationPid; this.consoleReader = consoleReader; }

    internal static CodexConsoleLease Pause(Process target, int destinationPid, bool consoleReader = true)
    {
        var watch = Stopwatch.StartNew();
        Exception? last = null;
        while (watch.Elapsed.TotalSeconds < 3)
        {
            var lease = new CodexConsoleLease(target, destinationPid, consoleReader);
            try { lease.Capture(); return lease; }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { last = error; lease.Dispose(); }
            Thread.Sleep(20);
        }
        throw new InvalidOperationException("Codex의 콘솔 입력이 안전한 대기 상태가 아닙니다. 기존 연결을 유지합니다.", last);
    }

    private void Capture()
    {
        using var memory = Native.OpenProcess(0x10, false, target.Id);
        Native.Check(!memory.IsInvalid, "Read Codex process");
        var allocation = Marshal.AllocHGlobal(1250);
        var context = (nint)((allocation.ToInt64() + 15) & ~15L);
        var contexts = new List<(long Rip, long Count, long Handles)>();
        try
        {
            target.Refresh();
            var ids = new HashSet<int>();
            foreach (ProcessThread item in target.Threads)
            {
                ids.Add(item.Id);
                var thread = Native.OpenThread(0x4A, false, (uint)item.Id);
                if (thread.IsInvalid) { thread.Dispose(); throw new IOException("Codex thread changed."); }
                if (Native.GetProcessIdOfThread(thread) != target.Id) { thread.Dispose(); throw new IOException("Codex thread identity changed."); }
                var previous = Native.SuspendThread(thread);
                if (previous == uint.MaxValue) { thread.Dispose(); throw new IOException("Could not pause Codex thread."); }
                threads.Add(thread);
                if (previous != 0) throw new InvalidOperationException("Codex thread was already suspended.");
                Marshal.Copy(new byte[1232], 0, context, 1232);
                Marshal.WriteInt32(context, 48, 0x100003);
                Native.Check(Native.GetThreadContext(thread, context), "Read Codex thread context");
                contexts.Add((Marshal.ReadInt64(context, 248), Marshal.ReadInt64(context, 200), Marshal.ReadInt64(context, 136)));
            }
            target.Refresh();
            if (!ids.SetEquals(target.Threads.Cast<ProcessThread>().Select(t => t.Id))) throw new IOException("Codex threads changed during capture.");
            var ntdll = target.Modules.Cast<ProcessModule>().Single(m => m.ModuleName.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase));
            var local = NativeLibrary.Load("ntdll.dll");
            try
            {
                using var self = Process.GetCurrentProcess();
                var localModule = self.Modules.Cast<ProcessModule>().Single(m => m.ModuleName.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(ntdll.FileName, localModule.FileName, StringComparison.OrdinalIgnoreCase)) throw new IOException("Different ntdll images.");
                long Address(string name) => ntdll.BaseAddress.ToInt64() + NativeLibrary.GetExport(local, name).ToInt64() - localModule.BaseAddress.ToInt64();
                var waits = new[] { "NtWaitForSingleObject", "NtWaitForMultipleObjects", "NtWaitForAlertByThreadId",
                    "NtWaitForWorkViaWorkerFactory", "NtRemoveIoCompletion", "NtRemoveIoCompletionEx", "NtDelayExecution", "NtWaitForKeyedEvent" }
                    .Select(Address).ToList();
                if (!consoleReader)
                {
                    // Node's verified npm wrapper has a message-only window for signal handling.
                    var path = Path.Combine(Environment.SystemDirectory, "win32u.dll");
                    var remoteGui = target.Modules.Cast<ProcessModule>().SingleOrDefault(m => string.Equals(m.FileName, path, StringComparison.OrdinalIgnoreCase));
                    if (remoteGui is not null)
                    {
                        var gui = NativeLibrary.Load(path);
                        try { waits.Add(remoteGui.BaseAddress.ToInt64() + NativeLibrary.GetExport(gui, "NtUserGetMessage").ToInt64() - gui.ToInt64()); }
                        finally { NativeLibrary.Free(gui); }
                    }
                }
                if (contexts.Any(c => !waits.Any(start => c.Rip >= start && c.Rip < start + 32)))
                    throw new IOException("Process has a thread outside a supported wait; no console changes made: " +
                        string.Join(", ", contexts.Where(c => !waits.Any(start => c.Rip >= start && c.Rip < start + 32)).Select(c => $"RIP=0x{c.Rip:X}, ntdll+0x{c.Rip - ntdll.BaseAddress.ToInt64():X}")));
                if (!consoleReader) return;

                var basic = Marshal.AllocHGlobal(48);
                try
                {
                    CodexNative.CheckStatus(CodexNative.NtQueryInformationProcess(target.Handle, 0, basic, 48, out _), "Read Codex PEB");
                    var parameters = ReadPointer(memory, Marshal.ReadIntPtr(basic, 8) + 0x20);
                    StandardInput = ReadPointer(memory, parameters + 0x20);
                }
                finally { Marshal.FreeHGlobal(basic); }

                var inputs = InputHandles();
                if (!inputs.Contains(StandardInput)) throw new IOException("Codex stdin is not console input.");
                var pollStart = Address("NtWaitForMultipleObjects");
                var pollInputs = new List<nint>();
                foreach (var state in contexts.Where(c => c.Rip >= pollStart && c.Rip < pollStart + 32 && c.Count == 2))
                {
                    var first = ReadPointer(memory, (nint)state.Handles);
                    if (!inputs.Contains(first)) continue;
                    pollInputs.Add(first);
                    if (pollInputs.Count != 1) throw new IOException("Multiple Codex console readers.");
                    semaphore = Copy(ReadPointer(memory, (nint)state.Handles + 8));
                    RequireSemaphore(semaphore);
                }
                if (pollInputs.Count != 1) throw new IOException("Codex console poller not found.");
                var cached = inputs.Where(h => h != StandardInput && h != pollInputs[0]).ToArray();
                var bound = BoundInputs(cached);
                if (cached.Length == 0 || bound.Length > 1) throw new IOException("Unrecognized Codex console input layout.");
                cachedInput = bound.Length == 1 ? bound[0] : cached.Length == 1 ? cached[0]
                    : throw new IOException("Cannot identify the persistent Codex console reader.");
                var singleWait = Address("NtWaitForSingleObject");
                if (contexts.Any(c => c.Rip >= singleWait && c.Rip < singleWait + 32 && c.Count == cachedInput.ToInt64()))
                    throw new IOException("Cached input has a pending single-object wait.");
                // The persistent crossterm reader is replaced; the handle in the pending kernel
                // wait is left open. Its existing semaphore wakes it so it can reopen CONIN$.
                foreach (var state in contexts.Where(c => c.Rip >= pollStart && c.Rip < pollStart + 32))
                {
                    if (state.Count is < 1 or > 64) throw new IOException("Unknown wait layout.");
                    for (var i = 0; i < state.Count; i++)
                        if (ReadPointer(memory, (nint)state.Handles + i * 8) == cachedInput)
                            throw new IOException("Cached input has a pending wait.");
                }
            }
            finally { NativeLibrary.Free(local); }
        }
        finally { Marshal.FreeHGlobal(allocation); }
    }

    private nint[] BoundInputs(nint[] handles)
    {
        // Copies of unbound stdin may remain after an earlier migration. Determine binding
        // without touching the source console: only our new destination gets a temporary mode.
        var copies = new List<(nint Remote, nint Local)>();
        try
        {
            foreach (var handle in handles) copies.Add((handle, Copy(handle)));
            Native.FreeConsole();
            Native.Check(Native.AttachConsole((uint)destinationPid), "Inspect input binding at destination");
            using var input = Native.ConsoleDevice("CONIN$");
            Native.Check(Native.GetConsoleMode(input.DangerousGetHandle(), out var original), "Destination input mode");
            try
            {
                const uint probeMode = 14; // Includes line/echo bits, unlike the source's raw input.
                Native.Check(Native.SetConsoleMode(input.DangerousGetHandle(), probeMode), "Probe destination input binding");
                return copies.Where(h => !Native.GetConsoleMode(h.Local, out var mode) || mode != probeMode).Select(h => h.Remote).ToArray();
            }
            finally { Native.Check(Native.SetConsoleMode(input.DangerousGetHandle(), original), "Restore destination input mode"); }
        }
        finally
        {
            foreach (var handle in copies) Native.CloseHandle(handle.Local);
            Native.FreeConsole();
            Native.Check(Native.AttachConsole((uint)target.Id), "Return to Codex source console");
        }
    }

    private List<nint> InputHandles()
    {
        const uint size = 1024 * 1024;
        var buffer = Marshal.AllocHGlobal((int)size);
        var result = new List<nint>();
        try
        {
            CodexNative.CheckStatus(CodexNative.NtQueryInformationProcess(target.Handle, 51, buffer, size, out _), "Read Codex handles");
            var count = Marshal.ReadInt64(buffer);
            if (count < 0 || count > (size - 16) / 40) throw new IOException("Invalid handle snapshot.");
            var fileType = -1;
            for (var i = 0; i < count; i++)
                if (Marshal.ReadIntPtr(buffer, 16 + i * 40) == StandardInput) fileType = Marshal.ReadInt32(buffer, 16 + i * 40 + 28);
            if (fileType < 0) throw new IOException("Codex stdin missing from handle snapshot.");
            for (var i = 0; i < count; i++)
            {
                if (Marshal.ReadInt32(buffer, 16 + i * 40 + 28) != fileType) continue;
                var remote = Marshal.ReadIntPtr(buffer, 16 + i * 40);
                var local = Copy(remote);
                try
                {
                    if (CodexNative.GetFileType(local) == 2 && Native.GetNumberOfConsoleInputEvents(local, out _)) result.Add(remote);
                }
                finally { Native.CloseHandle(local); }
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private nint Copy(nint handle)
    {
        Native.Check(CodexNative.DuplicateHandle(target.Handle, handle, -1, out var copy, 0, false, 2), "Copy Codex handle");
        return copy;
    }

    internal void RebindCachedInput()
    {
        if (cachedInput == 0) return; // Already unbound following an earlier successful transfer.
        Native.Check(CodexNative.DuplicateHandle(target.Handle, cachedInput, -1, out backup, 0, false, 3), "Preserve Codex cached input");
        cachedClosed = true;
        Native.Check(CodexNative.DuplicateHandle(target.Handle, StandardInput, target.Handle, out var replacement, 0, false, 2), "Rebind Codex cached input");
        if (replacement != cachedInput)
        {
            // Never resume with the cached numeric handle referring to a different object.
            Native.Check(CodexNative.DuplicateHandle(target.Handle, replacement, -1, out var extra, 0, false, 3), "Undo unexpected handle allocation");
            Native.CloseHandle(extra);
            RestoreCachedInput(alreadyClosed: true);
            throw new IOException("Windows did not retain the cached input handle slot; original input restored.");
        }
        cachedClosed = false;
    }

    internal void Commit()
    {
        if (!CodexNative.ReleaseSemaphore(semaphore, 1, 0) && Marshal.GetLastWin32Error() != 298)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Wake Codex console poller");
        committed = true;
    }

    private void RestoreCachedInput(bool alreadyClosed = false)
    {
        if (backup == 0) return;
        if (!alreadyClosed)
        {
            Native.Check(CodexNative.DuplicateHandle(target.Handle, cachedInput, -1, out var replaced, 0, false, 3), "Remove replacement input");
            Native.CloseHandle(replaced);
        }
        var fillers = new List<nint>();
        try
        {
            for (var attempt = 0; attempt < 65536; attempt++)
            {
                Native.Check(CodexNative.DuplicateHandle(-1, backup, target.Handle, out var restored, 0, false, 2), "Restore original Codex input");
                if (restored == cachedInput) { Native.CloseHandle(backup); backup = 0; return; }
                fillers.Add(restored);
            }
            throw new IOException("Could not restore the original Codex input slot.");
        }
        finally
        {
            foreach (var filler in fillers)
                if (CodexNative.DuplicateHandle(target.Handle, filler, -1, out var local, 0, false, 3)) Native.CloseHandle(local);
        }
    }

    public void Dispose()
    {
        try { if (!committed) RestoreCachedInput(cachedClosed); }
        finally
        {
            if (backup != 0) { Native.CloseHandle(backup); backup = 0; }
            if (semaphore != 0) { Native.CloseHandle(semaphore); semaphore = 0; }
            foreach (var thread in threads) { Native.ResumeThread(thread); thread.Dispose(); }
            threads.Clear();
        }
    }

    private static nint ReadPointer(SafeProcessHandle process, nint address)
    {
        var bytes = new byte[8];
        Native.Check(Native.ReadProcessMemory(process, address, bytes, 8, out var read) && read == 8, "Read Codex pointer");
        return (nint)BitConverter.ToInt64(bytes);
    }

    private static void RequireSemaphore(nint handle)
    {
        var buffer = Marshal.AllocHGlobal(4096);
        try
        {
            CodexNative.CheckStatus(CodexNative.NtQueryObject(handle, 2, buffer, 4096, out _), "Inspect console waker");
            if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, 8), (ushort)Marshal.ReadInt16(buffer) / 2) != "Semaphore")
                throw new IOException("Unknown Codex console waker.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
