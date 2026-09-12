using System.Diagnostics;

namespace FindingProcess.Lab;

internal static class CurrentTerminal
{
    internal const string WaitFileVariable = "FINDING_PROCESS_WAIT_FILE";
    private sealed record WaitTarget(int Pid, long StartedUtcTicks, string ImagePath);

    internal static int Run(TransferTarget? fixture, CodexTarget? codex)
    {
        using var target = codex is not null ? CodexProcess.Validate(codex)
            : RemoteRebind.Validate(fixture!.Identity, fixture.ImagePath);
        using var launcher = codex?.Launcher is { } parent ? CodexProcess.ValidateLauncher(parent) : null;
        if (Native.ConsoleProcesses().Contains((uint)target.Id))
            throw new IOException("이미 현재 터미널에 연결된 프로세스입니다.");

        // The command's PowerShell waits after the picker exits, leaving console input
        // exclusively to the transferred process. Record before moving so it also waits
        // if a later verification fails after the connection has already changed.
        var processes = launcher is null ? new[] { target } : new[] { target, launcher };
        var waitFile = Environment.GetEnvironmentVariable(WaitFileVariable);
        if (!string.IsNullOrEmpty(waitFile))
            JsonFile.Write(waitFile, processes.Select(p => new WaitTarget(p.Id,
                p.StartTime.ToUniversalTime().Ticks, p.MainModule!.FileName)).ToArray());

        using var input = Native.ConsoleDevice("CONIN$");
        using var output = Native.ConsoleDevice("CONOUT$");
        Native.Check(Native.GetConsoleMode(input.DangerousGetHandle(), out var inputMode), "Save terminal input mode");
        Native.Check(Native.GetConsoleMode(output.DangerousGetHandle(), out var outputMode), "Save terminal output mode");
        try
        {
            return codex is not null ? CodexHandoff.Run(codex, TerminalHost.Current)
                : RestoreDemo.Run(fixture!, TerminalHost.Current);
        }
        finally
        {
            // Direct apphost invocation has no launcher to wait on its behalf.
            if (string.IsNullOrEmpty(waitFile))
            {
                var attached = Native.ConsoleProcesses();
                var moved = processes.Where(p => !p.HasExited && attached.Contains((uint)p.Id)).ToArray();
                try { foreach (var process in moved) process.WaitForExit(); }
                finally
                {
                    if (moved.Length != 0)
                    {
                        const string reset = "\x1b[?1049l\x1b[?2004l\x1b[?1004l\x1b[0m\x1b[?25h";
                        Native.SetConsoleMode(output.DangerousGetHandle(), outputMode | 4);
                        Native.WriteConsoleW(output.DangerousGetHandle(), reset, (uint)reset.Length, out _, 0);
                    }
                    Native.SetConsoleMode(input.DangerousGetHandle(), inputMode);
                    Native.SetConsoleMode(output.DangerousGetHandle(), outputMode);
                }
            }
        }
    }
}
