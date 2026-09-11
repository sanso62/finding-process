using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FindingProcess.Lab;

internal static class Anchor
{
    internal static int Run(string statePath, string releasePath, int attachPid)
    {
        if (attachPid != 0)
        {
            Native.FreeConsole();
            Native.Check(Native.AttachConsole((uint)attachPid), "Attach rollback guardian");
        }
        var memory = Marshal.AllocHGlobal(32);
        try
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            Marshal.Copy(bytes, 0, memory, 32);
            using var process = Process.GetCurrentProcess();
            var identity = new Identity(process.Id, process.StartTime.ToUniversalTime().Ticks,
                Convert.ToHexString(bytes), memory.ToInt64());
            Native.Check(Native.GetConsoleScreenBufferInfo(Native.GetStdHandle(-11), out var info), "Anchor console");
            Native.Check(Native.SetConsoleMode(Native.GetStdHandle(-10), 8), "Anchor input mode");
            JsonFile.Write(statePath, new FixtureState(identity, 1, 0, "", info.Size.X, info.Size.Y, null));
            var watch = Stopwatch.StartNew();
            while (!File.Exists(releasePath) && watch.Elapsed.TotalSeconds < 300) Thread.Sleep(50);
            Native.FreeConsole(); // Leave the migrated target attached when this bootstrap process exits.
            return 0;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
}
