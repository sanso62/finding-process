using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FindingProcess.Lab;

internal sealed class CodexConsoleHandles : IDisposable
{
    private sealed class Saved(nint slot, nint backup, bool inherit)
    {
        internal readonly nint Slot = slot, Backup = backup;
        internal readonly bool Inherit = inherit;
        internal bool Closed = true;
    }
    private readonly Process target;
    private readonly List<Saved> saved = [];
    private bool committed;
    private CodexConsoleHandles(Process target) => this.target = target;

    internal static CodexConsoleHandles Rebind(Process target, nint reader, uint inputMode, uint outputMode, string resultPath, bool forceFailure = false)
    {
        var lease = new CodexConsoleHandles(target);
        var fresh = OpenInTarget(target, resultPath);
        try
        {
            foreach (var (handle, mode) in new[] { (fresh[0], inputMode), (fresh[1], outputMode) })
            {
                Native.Check(CodexNative.DuplicateHandle(target.Handle, handle, -1, out var local, 0, false, 2), "Access owned destination console mode");
                try { Native.Check(Native.SetConsoleMode(local, mode), "Restore owned destination console mode"); }
                finally { Native.CloseHandle(local); }
            }
            using var memory = Native.OpenProcess(0x10, false, target.Id);
            var basic = Marshal.AllocHGlobal(48);
            nint parameters;
            try
            {
                CodexNative.CheckStatus(CodexNative.NtQueryInformationProcess(target.Handle, 0, basic, 48, out _), "Read Codex console parameters");
                parameters = ReadPointer(memory, Marshal.ReadIntPtr(basic, 8) + 0x20);
            }
            finally { Marshal.FreeHGlobal(basic); }
            var replacements = new[] {
                (ReadPointer(memory, parameters + 0x20), fresh[0]),
                (ReadPointer(memory, parameters + 0x28), fresh[1]),
                (ReadPointer(memory, parameters + 0x30), fresh[1]),
                (reader, fresh[0])
            }.Where(p => p.Item1 != 0).DistinctBy(p => p.Item1);
            var snapshot = Marshal.AllocHGlobal(1024 * 1024);
            try
            {
                CodexNative.CheckStatus(CodexNative.NtQueryInformationProcess(target.Handle, 51, snapshot, 1024 * 1024, out _), "Read Codex handle flags");
                var count = Marshal.ReadInt64(snapshot);
                if (count < 0 || count > (1024 * 1024 - 16) / 40) throw new IOException("Invalid Codex handle snapshot.");
                foreach (var (slot, source) in replacements)
                {
                    uint? attributes = null;
                    for (var i = 0; i < count; i++)
                        if (Marshal.ReadIntPtr(snapshot, 16 + i * 40) == slot) attributes = (uint)Marshal.ReadInt32(snapshot, 16 + i * 40 + 32);
                    if (attributes is null || (attributes.Value & 1) != 0) throw new IOException("Console handle is missing or protected from close.");
                    Native.Check(CodexNative.DuplicateHandle(target.Handle, slot, -1, out var backup, 0, false, 3), "Back up Codex console handle");
                    var entry = new Saved(slot, backup, (attributes.Value & 2) != 0);
                    lease.saved.Add(entry);
                    Native.Check(CodexNative.DuplicateHandle(target.Handle, source, target.Handle, out var replacement, 0, entry.Inherit, 2), "Bind Codex-owned console handle");
                    if (replacement != slot)
                    {
                        lease.CloseRemote(replacement);
                        throw new IOException("Console handle slot changed; restoring the original handles.");
                    }
                    entry.Closed = false;
                    if (forceFailure) throw new IOException("Forced destination console handle failure.");
                }
            }
            finally { Marshal.FreeHGlobal(snapshot); }
            return lease;
        }
        catch { lease.Dispose(); throw; }
        finally { foreach (var handle in fresh) lease.CloseRemote(handle); }
    }

    internal void Commit() => committed = true;
    public void Dispose()
    {
        try
        {
            if (!committed)
                foreach (var entry in saved.AsEnumerable().Reverse())
                {
                    if (!entry.Closed) CloseRemote(entry.Slot);
                    var fillers = new List<nint>();
                    try
                    {
                        var restored = false;
                        for (var i = 0; i < 65536; i++)
                        {
                            Native.Check(CodexNative.DuplicateHandle(-1, entry.Backup, target.Handle, out var handle, 0, entry.Inherit, 2), "Restore Codex console handle");
                            if (handle == entry.Slot) { restored = true; break; }
                            fillers.Add(handle);
                        }
                        if (!restored) throw new IOException("Could not restore the Codex console handle slot.");
                    }
                    finally { foreach (var handle in fillers) CloseRemote(handle); }
                }
        }
        finally { foreach (var entry in saved) Native.CloseHandle(entry.Backup); saved.Clear(); }
    }

    private void CloseRemote(nint handle)
    {
        if (handle == 0 || handle == -1) return;
        Native.Check(CodexNative.DuplicateHandle(target.Handle, handle, -1, out var copy, 0, false, 3), "Close temporary Codex console handle");
        Native.CloseHandle(copy);
    }

    private static nint ReadPointer(Microsoft.Win32.SafeHandles.SafeProcessHandle memory, nint address)
    {
        var bytes = new byte[8];
        Native.Check(Native.ReadProcessMemory(memory, address, bytes, 8, out var count) && count == 8, "Read Codex console pointer");
        return (nint)BitConverter.ToInt64(bytes);
    }

    private static nint[] OpenInTarget(Process target, string resultPath)
    {
        using var process = Native.OpenProcess(0x43A, false, target.Id);
        Native.Check(!process.IsInvalid, "Open Codex for owned console handles");
        var allocation = Native.VirtualAllocEx(process, 0, 8192, 0x3000, 4);
        Native.Check(allocation != 0, "Allocate owned console routine");
        var finished = true;
        var module = NativeLibrary.Load("kernel32.dll");
        try
        {
            using var self = Process.GetCurrentProcess();
            long Resolve(string name)
            {
                var address = NativeLibrary.GetExport(module, name).ToInt64();
                var owner = self.Modules.Cast<ProcessModule>().Single(m => address >= m.BaseAddress.ToInt64() && address < m.BaseAddress.ToInt64() + m.ModuleMemorySize);
                var remote = target.Modules.Cast<ProcessModule>().Single(m => string.Equals(m.FileName, owner.FileName, StringComparison.OrdinalIgnoreCase));
                return remote.BaseAddress.ToInt64() + address - owner.BaseAddress.ToInt64();
            }
            var data = allocation + 4096;
            var payload = new byte[256];
            Encoding.Unicode.GetBytes("CONIN$\0").CopyTo(payload, 128);
            Encoding.Unicode.GetBytes("CONOUT$\0").CopyTo(payload, 160);
            var code = new List<byte>();
            void Emit(params byte[] bytes) => code.AddRange(bytes);
            void Pointer(long value) => code.AddRange(BitConverter.GetBytes(value));
            Emit(0x53, 0x48, 0x83, 0xEC, 0x40); // rbx + shadow space and three stack arguments.
            Emit(0x48, 0xBB); Pointer(data.ToInt64());
            for (var i = 0; i < 2; i++)
            {
                Emit(0x48, 0xB9); Pointer(data.ToInt64() + 128 + i * 32);
                Emit(0xBA, 0, 0, 0, 0xC0, 0x41, 0xB8, 3, 0, 0, 0, 0x45, 0x31, 0xC9);
                Emit(0x48, 0xC7, 0x44, 0x24, 0x20, 3, 0, 0, 0);
                Emit(0x48, 0xC7, 0x44, 0x24, 0x28, 0, 0, 0, 0);
                Emit(0x48, 0xC7, 0x44, 0x24, 0x30, 0, 0, 0, 0);
                Emit(0x48, 0xB8); Pointer(Resolve("CreateFileW")); Emit(0xFF, 0xD0);
                Emit(0x48, 0x89, 0x43, (byte)(i * 8));
            }
            Emit(0x31, 0xC0, 0x48, 0x83, 0xC4, 0x40, 0x5B, 0xC3);
            var bytes = code.ToArray();
            Native.Check(Native.WriteProcessMemory(process, data, payload, (nuint)payload.Length, out var dataWritten) && dataWritten == (nuint)payload.Length, "Write console names");
            Native.Check(Native.WriteProcessMemory(process, allocation, bytes, (nuint)bytes.Length, out var codeWritten) && codeWritten == (nuint)bytes.Length, "Write owned console routine");
            Native.Check(Native.VirtualProtectEx(process, allocation, 4096, 0x20, out _), "Protect owned console routine");
            Native.Check(Native.FlushInstructionCache(process, allocation, (nuint)bytes.Length), "Flush owned console routine");
            using var thread = Native.CreateRemoteThread(process, 0, 0, allocation, 0, 0, out _);
            Native.Check(!thread.IsInvalid, "Open console handles inside Codex");
            finished = false;
            if (Native.WaitForSingleObject(thread, 8000) != 0)
            {
                File.WriteAllText(resultPath + ".owned-console.pending", "Owned console routine still active; retained allocation and guardians.");
                throw new TimeoutException("Codex console handle routine did not finish.");
            }
            finished = true;
            var result = new byte[16];
            Native.Check(Native.ReadProcessMemory(process, data, result, 16, out var read) && read == 16, "Read owned console handles");
            var handles = new[] { (nint)BitConverter.ToInt64(result), (nint)BitConverter.ToInt64(result, 8) };
            if (handles.Any(h => h == 0 || h == -1))
            {
                var cleanup = new CodexConsoleHandles(target);
                foreach (var handle in handles) cleanup.CloseRemote(handle);
                throw new IOException("Codex could not open its own destination console handles.");
            }
            return handles;
        }
        finally { NativeLibrary.Free(module); if (finished) Native.VirtualFreeEx(process, allocation, 0, 0x8000); }
    }
}
