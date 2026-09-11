using System.Diagnostics;

namespace FindingProcess.Lab;

internal static class Observer
{
    internal static int Run(string statePath, string reportPath, long startedTicks,
        string challenge, int amount, short columns, short rows)
    {
        var initial = JsonFile.Read<FixtureState>(statePath);
        using var target = Process.GetProcessById(initial.Identity.Pid);
        // Hold the process handle for this whole operation and reject PID reuse / non-lab targets.
        _ = target.Handle;
        if (target.StartTime.ToUniversalTime().Ticks != startedTicks ||
            initial.Identity.StartedUtcTicks != startedTicks ||
            !string.Equals(target.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Target is not the original lab fixture; refusing console writes.");
        Native.FreeConsole(); // Detach ONLY this helper, never the target.
        Native.Check(Native.AttachConsole((uint)target.Id), "AttachConsole(observer)");
        try
        {
            using var input = Native.ConsoleDevice("CONIN$");
            using var output = Native.ConsoleDevice("CONOUT$");
            if (challenge != "quit") Resize(output.DangerousGetHandle(), columns, rows);
            var command = amount == 0 && challenge == "quit" ? "quit\r" : $"add {amount} {challenge}\r";
            WriteInput(input.DangerousGetHandle(), command);
            if (challenge == "quit") return 0;

            string screen = "";
            var watch = Stopwatch.StartNew();
            var observed = false;
            while (watch.Elapsed < TimeSpan.FromSeconds(8))
            {
                screen = ReadScreen(output.DangerousGetHandle());
                var state = JsonFile.Read<FixtureState>(statePath);
                observed = screen.Contains(challenge, StringComparison.Ordinal) &&
                    screen.Contains(initial.Identity.Nonce, StringComparison.Ordinal) &&
                    state.LastInput == command.TrimEnd('\r') && state.Columns == columns && state.Rows == rows;
                if (observed) break;
                Thread.Sleep(50);
            }
            Native.Check(Native.GetConsoleScreenBufferInfo(output.DangerousGetHandle(), out var info), "Observe size");
            JsonFile.Write(reportPath, new Observation(Environment.ProcessId, initial.Identity, challenge,
                screen, observed, (short)(info.Window.Right - info.Window.Left + 1),
                (short)(info.Window.Bottom - info.Window.Top + 1), Native.ConsoleProcesses()));
            return observed ? 0 : 2;
        }
        finally { Native.FreeConsole(); }
    }

    private static void WriteInput(nint input, string text)
    {
        var records = text.SelectMany(character => new[]
        {
            new Native.InputRecord { EventType = 1, KeyDown = 1, RepeatCount = 1,
                VirtualKey = character == '\r' ? (ushort)13 : (ushort)0, UnicodeChar = character },
            new Native.InputRecord { EventType = 1, KeyDown = 0, RepeatCount = 1,
                VirtualKey = character == '\r' ? (ushort)13 : (ushort)0, UnicodeChar = character }
        }).ToArray();
        Native.Check(Native.WriteConsoleInputW(input, records, (uint)records.Length, out var written), "Write console input");
        if (written != records.Length) throw new IOException("Incomplete console input write.");
    }

    private static string ReadScreen(nint output)
    {
        Native.Check(Native.GetConsoleScreenBufferInfo(output, out var info), "Read screen size");
        var firstRow = Math.Max(0, info.Cursor.Y - 40);
        var length = info.Size.X * (Math.Min(info.Size.Y, info.Cursor.Y + 1) - firstRow);
        var buffer = new char[length];
        Native.Check(Native.ReadConsoleOutputCharacterW(output, buffer, (uint)length,
            new Native.Coord(0, (short)firstRow), out var read), "Read screen characters");
        // This API returns a counted buffer, not a null-terminated string.
        return new string(buffer, 0, checked((int)read));
    }

    private static void Resize(nint output, short columns, short rows)
    {
        Native.Check(Native.GetConsoleScreenBufferInfo(output, out var info), "Read size before resize");
        // Enlarge buffer first, set viewport, then shrink buffer to avoid invalid rectangles.
        Native.Check(Native.SetConsoleScreenBufferSize(output,
            new Native.Coord(Math.Max(info.Size.X, columns), Math.Max(info.Size.Y, rows))), "Grow buffer");
        var window = new Native.Rect { Right = (short)(columns - 1), Bottom = (short)(rows - 1) };
        Native.Check(Native.SetConsoleWindowInfo(output, true, ref window), "Resize console window");
        Native.Check(Native.SetConsoleScreenBufferSize(output, new Native.Coord(columns, rows)), "Resize buffer");
    }
}
