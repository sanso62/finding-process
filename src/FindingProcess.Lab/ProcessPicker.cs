using System.Text;

namespace FindingProcess.Lab;

internal static class ProcessPicker
{
    internal static int Run(TerminalHost host = TerminalHost.WindowsTerminal, bool listOnly = false)
    {
        var entries = ProcessCatalog.Read();
        if (listOnly || Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            foreach (var entry in entries) Console.WriteLine(Label(entry));
            return 0;
        }
        if (entries.Count == 0) { Console.WriteLine("실행 중인 프로세스가 없습니다."); return 0; }

        var originalControlC = Console.TreatControlCAsInput;
        var originalCursor = Console.CursorVisible;
        var originalColor = Console.ForegroundColor;
        var selected = 0;
        var rowCount = Math.Min(entries.Count, Math.Min(10, Math.Max(1, Console.WindowHeight - 3)));
        var top = 0;
        TransferTarget? target = null;
        var hint = "↑↓ 선택 · Enter 이관 · Ctrl+C 종료";
        try
        {
            Console.TreatControlCAsInput = true;
            Console.CursorVisible = false;
            // Reserve just the list's lines in the existing console, retaining shell history.
            for (var row = 0; row <= rowCount; row++) Console.WriteLine();
            top = Console.CursorTop - rowCount - 1;
            while (true)
            {
                top = Math.Clamp(top, 0, Math.Max(0, Console.BufferHeight - rowCount - 2));
                var first = Math.Clamp(selected - rowCount / 2, 0, Math.Max(0, entries.Count - rowCount));
                for (var row = 0; row < rowCount; row++)
                {
                    var index = first + row;
                    Console.ForegroundColor = index == selected ? ConsoleColor.Cyan : originalColor;
                    WriteLineAt(top + row, (index == selected ? "> " : "  ") + Label(entries[index]));
                }
                Console.ForegroundColor = originalColor;
                WriteLineAt(top + rowCount, $"{selected + 1}/{entries.Count}  {hint}");
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)) return 0;
                if (key.Key == ConsoleKey.UpArrow) selected = Math.Max(0, selected - 1);
                if (key.Key == ConsoleKey.DownArrow) selected = Math.Min(entries.Count - 1, selected + 1);
                if (key.Key != ConsoleKey.Enter) continue;
                target = entries[selected].Target;
                if (target is not null) break;
                hint = "이 프로세스는 아직 이관 미지원 · ↑↓ 선택 · Ctrl+C 종료";
            }
        }
        finally
        {
            Console.ForegroundColor = originalColor;
            Console.SetCursorPosition(0, Math.Min(top + rowCount + 1, Console.BufferHeight - 1));
            Console.CursorVisible = originalCursor;
            Console.TreatControlCAsInput = originalControlC;
        }

        Console.WriteLine($"PID {target.Identity.Pid} → {host} 연결 중...");
        // Allow the native operation to unwind safely if Ctrl+C arrives during the handoff.
        ConsoleCancelEventHandler finishTransfer = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += finishTransfer;
        try { return RestoreDemo.Run(target, host); }
        catch (Exception error) { Console.Error.WriteLine($"이관하지 못했습니다: {error.Message}"); return 1; }
        finally { Console.CancelKeyPress -= finishTransfer; }
    }

    private static string Label(ProcessEntry entry) =>
        $"{entry.Pid,7}  {entry.Name}  [{(entry.Target is null ? "미지원" : "이관 가능")}]";

    private static void WriteLineAt(int row, string value)
    {
        var width = Math.Max(0, Math.Min(Console.WindowWidth, Console.BufferWidth) - 1);
        var text = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune)) continue;
            var size = rune.Value is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or
                >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or >= 0xFE10 and <= 0xFE6F or
                >= 0xFF01 and <= 0xFF60 or >= 0x1F300 and <= 0x1FAFF or >= 0x20000 ? 2 : 1;
            if (used + size > width) break;
            text.Append(rune); used += size;
        }
        Console.SetCursorPosition(0, row);
        Console.Write(text.ToString() + new string(' ', width - used));
    }
}
