using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FindingProcess.Lab;

internal static class CodexNative
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern nint CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] internal static extern nint LocalFree(nint memory);
    [DllImport("ntdll.dll")] internal static extern int NtQueryInformationProcess(nint process, int info, nint buffer, uint size, out uint needed);
    [DllImport("ntdll.dll")] internal static extern int NtQueryObject(nint handle, int info, nint buffer, uint size, out uint needed);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool DuplicateHandle(nint sourceProcess, nint source,
        nint destinationProcess, out nint destination, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")] internal static extern uint GetFileType(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ReleaseSemaphore(nint semaphore, int amount, nint previous);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool FlushConsoleInputBuffer(nint input);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateJobObjectW(nint security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool IsProcessInJob(SafeProcessHandle process, nint job, out bool member);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool QueryInformationJobObject(nint job, int info, nint buffer, uint size, out uint returned);

    internal static void CheckStatus(int status, string operation)
    {
        if (status < 0) throw new IOException($"{operation}: NTSTATUS 0x{status:X8}");
    }
}
