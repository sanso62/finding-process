using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FindingProcess.Lab;

internal sealed record RebindRequest(Identity Target, Identity Destination, Identity Guardian, uint TargetMainThreadId,
    bool ForceAttachFailure = false, string? TargetImagePath = null);

internal static class RemoteRebind
{
    // A fixed x64 routine calling Windows console APIs. It never edits application instructions,
    // stack, managed state, or handle table entries. Only the runner's attested fixtures are accepted.
    internal static int Run(string requestPath, string resultPath)
    {
        var request = JsonFile.Read<RebindRequest>(requestPath);
        using var target = Validate(request.Target, request.TargetImagePath);
        using var destination = Validate(request.Destination);
        using var guardian = Validate(request.Guardian);
        using var handle = Native.OpenProcess(0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400, false, target.Id);
        Native.Check(!handle.IsInvalid, "OpenProcess(lab fixture rebind)");
        Native.Check(Native.IsWow64Process2(handle, out var machine, out var nativeMachine), "Target architecture");
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || machine != 0 || nativeMachine != 0x8664)
            throw new PlatformNotSupportedException("The experimental routine requires native x64 Windows processes.");

        var allocation = Native.VirtualAllocEx(handle, 0, 8192, 0x3000, 0x04);
        Native.Check(allocation != 0, "Allocate rebind routine");
        var canRelease = true;
        try
        {
            var data = allocation + 4096;
            var code = Build(target, data, request.ForceAttachFailure ? 0 : request.Destination.Pid, request.Guardian.Pid);
            Native.Check(Native.WriteProcessMemory(handle, allocation, code, (nuint)code.Length, out var written)
                && written == (nuint)code.Length, "Write fixed rebind routine");
            Native.Check(Native.VirtualProtectEx(handle, allocation, 4096, 0x20, out _), "Make routine executable");
            Native.Check(Native.FlushInstructionCache(handle, allocation, (nuint)code.Length), "Flush instruction cache");
            using var quietThread = SleepingThread.Pause(target, request.TargetMainThreadId);
            using var thread = Native.CreateRemoteThread(handle, 0, 0, allocation, 0, 0, out var threadId);
            Native.Check(!thread.IsInvalid, "Run console rebind in lab fixture");
            canRelease = false;
            if (Native.WaitForSingleObject(thread, 8000) != 0)
                throw new TimeoutException("Rebind thread did not finish. Allocation retained until fixture exits; never free live code.");
            canRelease = true;
            Native.Check(Native.GetExitCodeThread(thread, out var exitCode), "Read rebind thread result");
            var bytes = new byte[256];
            Native.Check(Native.ReadProcessMemory(handle, data, bytes, (nuint)bytes.Length, out _), "Read rebind diagnostics");
            var values = Enumerable.Range(0, 32).Select(i => BitConverter.ToInt64(bytes, i * 8)).ToArray();
            JsonFile.Write(resultPath, new { HelperPid = Environment.ProcessId, ThreadId = threadId,
                ExitCode = exitCode, Status = values[24], Handles = values[..3], OriginalFlags = values[3..6],
                OriginalModes = values[6..9], ApiResults = values[9..24], RestoreFlagsResults = values[26..29],
                DirectHandoffVerified = false, Note = "API result only; independent post-exit I/O verification required." });
            return exitCode == 1 && values[24] == 1 ? 0 : 2;
        }
        finally
        {
            if (canRelease) Native.VirtualFreeEx(handle, allocation, 0, 0x8000);
        }
    }

    internal static Process Validate(Identity identity, string? expectedImagePath = null)
    {
        var process = Process.GetProcessById(identity.Pid);
        try
        {
            _ = process.Handle;
            if (process.StartTime.ToUniversalTime().Ticks != identity.StartedUtcTicks ||
                !string.Equals(process.MainModule?.FileName, expectedImagePath ?? Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(process.MainModule?.FileName) != "FindingProcess.Lab.exe")
                throw new InvalidOperationException("Identity is not a live lab process.");
            using var handle = Native.OpenProcess(0x10, false, process.Id);
            Native.Check(!handle.IsInvalid, "Open attested lab process");
            var nonce = new byte[32];
            Native.Check(Native.ReadProcessMemory(handle, (nint)identity.Address, nonce, 32, out var read), "Read lab attestation");
            if (read != 32 || Convert.ToHexString(nonce) != identity.Nonce)
                throw new InvalidOperationException("Live lab memory nonce does not match.");
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    private static byte[] Build(Process target, nint data, int destination, int guardian)
    {
        using var local = Process.GetCurrentProcess();
        var module = NativeLibrary.Load("kernel32.dll");
        try
        {
            long Resolve(string name)
            {
                var address = NativeLibrary.GetExport(module, name).ToInt64();
                var owner = local.Modules.Cast<ProcessModule>().Single(m => address >= m.BaseAddress.ToInt64() &&
                    address < m.BaseAddress.ToInt64() + m.ModuleMemorySize);
                var remote = target.Modules.Cast<ProcessModule>().Single(m =>
                    string.Equals(m.FileName, owner.FileName, StringComparison.OrdinalIgnoreCase));
                return remote.BaseAddress.ToInt64() + address - owner.BaseAddress.ToInt64();
            }
            var asm = new Routine();
            asm.Bytes(0x53, 0x48, 0x83, 0xEC, 0x20); // preserve rbx; aligned call shadow space
            asm.Bytes(0x48, 0xBB); asm.Int64(data.ToInt64());
            void Call(string name, int result, params Argument[] arguments)
            {
                for (var i = 0; i < arguments.Length; i++) asm.Argument(i, arguments[i]);
                asm.Bytes(0x48, 0xB8); asm.Int64(Resolve(name)); asm.Bytes(0xFF, 0xD0);
                if (result >= 0) { asm.Bytes(0x48, 0x89, 0x83); asm.Int32(result * 8); }
            }
            for (var i = 0; i < 3; i++) Call("GetStdHandle", i, new Argument((uint)(-10 - i)));
            for (var i = 0; i < 3; i++)
            {
                Call("GetHandleInformation", 9 + i, new(i * 8, true), new(data.ToInt64() + (3 + i) * 8));
                asm.FailTo("end");
                Call("GetConsoleMode", 12 + i, new(i * 8, true), new(data.ToInt64() + (6 + i) * 8));
                asm.FailTo("end");
            }
            for (var i = 0; i < 3; i++)
            {
                Call("SetHandleInformation", 15 + i, new(i * 8, true), new(2), new(2));
                asm.FailTo("restore");
            }
            Call("FreeConsole", 18);
            asm.FailTo("restore");
            Call("AttachConsole", 19, new Argument(destination));
            asm.FailTo("rollback");
            Call("SetConsoleMode", 20, new(0, true), new(6 * 8, true));
            asm.FailTo("rollback");
            Call("GetConsoleMode", 21, new(8, true), new(data.ToInt64() + 25 * 8));
            asm.FailTo("rollback");
            asm.SetStatus(1); asm.Jump("restore");
            asm.Label("rollback");
            Call("FreeConsole", 29);
            Call("AttachConsole", 22, new Argument(guardian));
            asm.FailTo("rollback-failed");
            Call("SetConsoleMode", 23, new(0, true), new(6 * 8, true));
            asm.FailTo("rollback-failed");
            asm.SetStatus(2);
            asm.Jump("restore");
            asm.Label("rollback-failed");
            asm.SetStatus(3);
            asm.Label("restore");
            for (var i = 0; i < 3; i++)
                Call("SetHandleInformation", 26 + i, new(i * 8, true), new(2), new((3 + i) * 8, true));
            for (var i = 0; i < 3; i++) asm.FailFieldTo(26 + i, "restore-failed");
            asm.Jump("end");
            asm.Label("restore-failed");
            asm.SetStatus(4);
            asm.Label("end");
            asm.Bytes(0x8B, 0x83); asm.Int32(24 * 8); // status -> eax (thread exit code)
            asm.Bytes(0x48, 0x83, 0xC4, 0x20, 0x5B, 0xC3);
            return asm.Finish();
        }
        finally { NativeLibrary.Free(module); }
    }

    private readonly record struct Argument(long Value, bool Field = false);
    private sealed class Routine
    {
        private readonly List<byte> code = [];
        private readonly Dictionary<string, int> labels = [];
        private readonly List<(int Position, string Label)> branches = [];
        internal void Bytes(params byte[] bytes) => code.AddRange(bytes);
        internal void Int32(int value) => code.AddRange(BitConverter.GetBytes(value));
        internal void Int64(long value) => code.AddRange(BitConverter.GetBytes(value));
        internal void Argument(int index, Argument arg)
        {
            if (arg.Field)
            {
                Bytes(index < 2 ? (byte)0x48 : (byte)0x4C, 0x8B, new byte[] { 0x8B, 0x93, 0x83, 0x8B }[index]);
                Int32(checked((int)arg.Value));
            }
            else
            {
                Bytes(index < 2 ? (byte)0x48 : (byte)0x49, new byte[] { 0xB9, 0xBA, 0xB8, 0xB9 }[index]);
                Int64(arg.Value);
            }
        }
        internal void SetStatus(int value) { Bytes(0xC7, 0x83); Int32(24 * 8); Int32(value); }
        internal void FailTo(string label) { Bytes(0x85, 0xC0, 0x0F, 0x84); Branch(label); }
        internal void FailFieldTo(int field, string label) { Bytes(0x8B, 0x83); Int32(field * 8); FailTo(label); }
        internal void Jump(string label) { Bytes(0xE9); Branch(label); }
        private void Branch(string label) { branches.Add((code.Count, label)); Int32(0); }
        internal void Label(string label) => labels.Add(label, code.Count);
        internal byte[] Finish()
        {
            foreach (var branch in branches)
            {
                var bytes = BitConverter.GetBytes(labels[branch.Label] - branch.Position - 4);
                for (var i = 0; i < 4; i++) code[branch.Position + i] = bytes[i];
            }
            if (code.Count > 4096) throw new InvalidOperationException("Routine exceeds executable allocation.");
            return code.ToArray();
        }
    }
}
