using System.Diagnostics;

namespace FindingProcess.Lab;

internal sealed record SourceCompletion(bool MessageWritten, bool LauncherExited, bool OriginalCommandExited, int RetainedJobHandles);

internal static class SourceConsole
{
    private sealed record WaitIdentity(int Pid, long StartedUtcTicks);
    internal static SourceCompletion Complete(Process guardian, Process target, Process? launcher)
    {
        var retainedJobs = launcher is null ? 0 : CodexLauncherJobs.Preserve(launcher, target);
        Native.FreeConsole();
        Native.Check(Native.AttachConsole((uint)guardian.Id), "Attach original console for completion");
        try
        {
            if (Native.ConsoleProcesses().Contains((uint)target.Id))
                throw new IOException("Refusing to clear a console still containing the transferred Codex.");
            using var input = Native.ConsoleDevice("CONIN$");
            Native.Check(Native.GetConsoleMode(input.DangerousGetHandle(), out var inputMode), "Read original input mode");
            using (var previousOutput = Native.ConsoleDevice("CONOUT$"))
            {
                Native.Check(Native.GetConsoleMode(previousOutput.DangerousGetHandle(), out var previousMode), "Read original output mode");
                Native.Check(Native.SetConsoleMode(previousOutput.DangerousGetHandle(), previousMode | 4), "Reset original terminal display mode");
                const string reset = "\x1b[?1049l\x1b[?2004l\x1b[?1004l\x1b[0m\x1b[?25h";
                Native.Check(Native.WriteConsoleW(previousOutput.DangerousGetHandle(), reset, (uint)reset.Length, out var resetCount, 0)
                    && resetCount == reset.Length, "Leave original Codex display");
            }
            // Reopen after leaving the alternate buffer so the message remains with the shell.
            using var output = Native.ConsoleDevice("CONOUT$");
            Native.Check(Native.GetConsoleMode(output.DangerousGetHandle(), out var outputMode), "Read restored output mode");
            Native.Check(Native.SetConsoleMode(output.DangerousGetHandle(), outputMode | 4), "Enable original terminal completion message");
            // Leave scrollback intact, but remove the abandoned CLI screen and input modes.
            var message = $"\x1b[2J\x1b[H이관됨 · Codex PID {target.Id}\r\n";
            Native.Check(Native.WriteConsoleW(output.DangerousGetHandle(), message, (uint)message.Length, out var written, 0)
                && written == message.Length, "Write original terminal completion message");
            // Keystrokes left in the former CLI's queue must not become shell commands.
            Native.Check(CodexNative.FlushConsoleInputBuffer(input.DangerousGetHandle()), "Clear abandoned CLI input queue");
            Native.Check(Native.SetConsoleMode(input.DangerousGetHandle(), (inputMode | 7) & ~0x200u), "Restore original shell input mode");
            if (launcher is not null)
            {
                // This is the pinned, hash-verified npm wrapper that has already moved.
                // Terminate only that process, without running its signal-forwarding handler
                // or terminating its child. The native Codex retains its PID and memory.
                Native.Check(CodexNative.TerminateProcess(launcher.SafeHandle, 0), "Finish verified Codex npm launcher");
                if (!launcher.WaitForExit(5000)) throw new TimeoutException("Codex npm launcher has not exited.");
                if (target.HasExited) throw new IOException("Codex exited while completing the original command.");
            }
            var releasedWaiter = ReleasePreviousCommand(target);
            return new(true, launcher is not null, launcher is not null || releasedWaiter, retainedJobs);
        }
        finally { Native.FreeConsole(); }
    }

    private static bool ReleasePreviousCommand(Process target)
    {
        var cache = Path.Combine(ProjectPaths.Artifacts, "cli");
        if (!Directory.Exists(cache)) return false;
        var members = Native.ConsoleProcesses();
        var released = false;
        foreach (var directory in Directory.EnumerateDirectories(cache))
            foreach (var ownerFile in Directory.EnumerateFiles(directory, "wait-*.json.owner.json"))
                try
                {
                    var owner = JsonFile.Read<WaitIdentity>(ownerFile);
                    if (!members.Contains((uint)owner.Pid)) continue;
                    using var waiter = Process.GetProcessById(owner.Pid);
                    _ = waiter.Handle;
                    if (waiter.StartTime.ToUniversalTime().Ticks != owner.StartedUtcTicks) continue;
                    var waitFile = ownerFile[..^".owner.json".Length];
                    var targets = JsonFile.Read<WaitIdentity[]>(waitFile);
                    if (!targets.Any(t => t.Pid == target.Id && t.StartedUtcTicks == target.StartTime.ToUniversalTime().Ticks)) continue;
                    File.WriteAllText(waitFile + ".moved", "transferred");
                    released = true;
                }
                catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or
                    UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
                { /* A previous command may exit while its registration is being inspected. */ }
        return released;
    }
}
