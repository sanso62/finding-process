using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FindingProcess.Lab;

internal static partial class Native
{
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeWaitHandle OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetProcessIdOfThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint SuspendThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetThreadContext(SafeWaitHandle thread, nint context);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetHandleInformation(nint handle, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualFreeEx(SafeProcessHandle process, nint address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] bytes, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualProtectEx(SafeProcessHandle process, nint address, nuint size, uint protection, out uint previous);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FlushInstructionCache(SafeProcessHandle process, nint address, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeWaitHandle CreateRemoteThread(SafeProcessHandle process, nint attributes, nuint stack,
        nint start, nint parameter, uint flags, out uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeThread(SafeWaitHandle thread, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);
}
