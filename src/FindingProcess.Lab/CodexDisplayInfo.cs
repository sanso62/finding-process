using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FindingProcess.Lab;

internal sealed record CodexDisplayInfo(string? Directory, DateTime StartedLocal)
{
    internal static CodexDisplayInfo Read(Process process, CodexTarget target)
    {
        string? directory = null;
        try
        {
            // Display metadata only: never attach, pause, or write to the running CLI.
            // The adapter has already verified this process as the supported x64 build.
            using var memory = Native.OpenProcess(0x410, false, process.Id);
            Native.Check(!memory.IsInvalid, "Read Codex working directory");
            var basic = Marshal.AllocHGlobal(48);
            try
            {
                CodexNative.CheckStatus(CodexNative.NtQueryInformationProcess(memory.DangerousGetHandle(), 0, basic, 48, out _), "Read Codex process parameters");
                byte[] ReadBytes(nint address, int length)
                {
                    var bytes = new byte[length];
                    Native.Check(Native.ReadProcessMemory(memory, address, bytes, (nuint)length, out var read) && read == (nuint)length,
                        "Read Codex directory metadata");
                    return bytes;
                }
                var parameters = (nint)BitConverter.ToInt64(ReadBytes(Marshal.ReadIntPtr(basic, 8) + 0x20, 8));
                var descriptor = ReadBytes(parameters + 0x38, 16); // RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath
                var length = BitConverter.ToUInt16(descriptor);
                var capacity = BitConverter.ToUInt16(descriptor, 2);
                var pointer = (nint)BitConverter.ToInt64(descriptor, 8);
                if (length > 0 && length <= capacity && length % 2 == 0 && pointer != 0)
                {
                    var bytes = ReadBytes(pointer, length);
                    // A concurrent directory change must not produce a misleading label.
                    if (descriptor.SequenceEqual(ReadBytes(parameters + 0x38, 16)))
                        directory = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                }
            }
            finally { Marshal.FreeHGlobal(basic); }
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != target.StartedUtcTicks) directory = null;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { /* Missing display metadata must not hide an otherwise supported process. */ }
        return new(directory, new DateTime(target.StartedUtcTicks, DateTimeKind.Utc).ToLocalTime());
    }

    internal string FolderLabel(bool fullPath)
    {
        if (string.IsNullOrWhiteSpace(Directory)) return "폴더 확인 불가";
        if (fullPath) return Directory;
        var path = Path.TrimEndingDirectorySeparator(Directory);
        var name = Path.GetFileName(path);
        var parent = Path.GetDirectoryName(path);
        var parentName = parent is null ? "" : Path.GetFileName(parent);
        return string.IsNullOrEmpty(name) ? path : string.IsNullOrEmpty(parentName) ? name : $"{parentName}/{name}";
    }
}
