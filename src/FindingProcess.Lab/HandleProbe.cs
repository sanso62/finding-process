using System.Runtime.InteropServices;

namespace FindingProcess.Lab;

internal static class HandleProbe
{
    // Cooperative control only, not a recovery mechanism. Establishes whether cached
    // unbound handles survive FreeConsole when protected from CloseHandle.
    internal static int Run(int destination, string path)
    {
        var handles = new[] { Native.GetStdHandle(-10), Native.GetStdHandle(-11), Native.GetStdHandle(-12) };
        var stages = new List<object>();
        foreach (var handle in handles.Distinct())
        {
            Native.Check(Native.SetHandleInformation(handle, 2, 2), "Protect standard handle");
        }
        var freed = Native.FreeConsole();
        stages.Add(new { Stage = "FreeConsole", Result = freed, Error = freed ? 0 : Marshal.GetLastWin32Error() });
        var attached = Native.AttachConsole((uint)destination);
        stages.Add(new { Stage = "AttachConsole", Result = attached, Error = Marshal.GetLastWin32Error() });
        foreach (var handle in handles.Distinct())
        {
            var usable = Native.GetConsoleMode(handle, out var mode);
            stages.Add(new { Stage = "cached-handle", Handle = handle.ToInt64(), Usable = usable, Mode = mode,
                Error = Marshal.GetLastWin32Error() });
            Native.SetHandleInformation(handle, 2, 0);
        }
        var text = "FP-HANDLE-PROBE\r\n";
        var written = Native.WriteConsoleW(handles[1], text, (uint)text.Length, out var count, 0);
        stages.Add(new { Stage = "cached-output", Result = written, Count = count, Error = Marshal.GetLastWin32Error() });
        JsonFile.Write(path, stages);
        return freed && attached && written ? 0 : 2;
    }
}
