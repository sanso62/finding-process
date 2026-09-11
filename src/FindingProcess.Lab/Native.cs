using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FindingProcess.Lab;

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Coord(short x, short y) { internal short X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { internal short Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BufferInfo
    {
        internal Coord Size, Cursor;
        internal ushort Attributes;
        internal Rect Window;
        internal Coord MaximumWindowSize;
    }
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    internal struct InputRecord
    {
        [FieldOffset(0)] internal ushort EventType;
        [FieldOffset(4)] internal int KeyDown;
        [FieldOffset(8)] internal ushort RepeatCount;
        [FieldOffset(10)] internal ushort VirtualKey;
        [FieldOffset(14)] internal ushort UnicodeChar;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        internal int Size;
        internal string? Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, Reserved2Size;
        internal nint Reserved2, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInfo { internal nint Process, Thread; internal uint Pid, Tid; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint GetStdHandle(int kind);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetConsoleScreenBufferInfo(nint output, out BufferInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleScreenBufferSize(nint output, Coord size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleWindowInfo(nint output, [MarshalAs(UnmanagedType.Bool)] bool absolute, ref Rect rect);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadConsoleOutputCharacterW(nint output,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] text,
        uint length, Coord start, out uint read);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteConsoleInputW(nint input, InputRecord[] records, uint length, out uint written);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteConsoleW(nint output, string text, uint length, out uint written, nint reserved);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNumberOfConsoleInputEvents(nint input, out uint count);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadConsoleInputW(nint input, [Out] InputRecord[] records, uint length, out uint read);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleMode(nint handle, uint mode);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetConsoleProcessList([Out] uint[] processes, uint length);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessW(string application, StringBuilder commandLine,
        nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(SafeProcessHandle process, nint address,
        [Out] byte[] bytes, nuint size, out nuint read);

    internal static void Check(bool success, string api)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), api);
    }

    internal static SafeFileHandle ConsoleDevice(string name)
    {
        var handle = CreateFileW(name, 0xC0000000, 3, 0, 3, 0, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Open {name}");
        }
        return handle;
    }

    internal static uint[] ConsoleProcesses()
    {
        var items = new uint[16];
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var count = GetConsoleProcessList(items, (uint)items.Length);
            Check(count != 0, nameof(GetConsoleProcessList));
            if (count <= items.Length) return items[..(int)count];
            items = new uint[count];
        }
        throw new IOException("Console process list kept changing.");
    }
}
