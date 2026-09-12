using System.Diagnostics;

namespace FindingProcess.Lab;

internal sealed record CodexRebindRequest(CodexTarget Target, Identity Destination, Identity Guardian,
    bool ForceAttachFailure = false, bool ForceLauncherAttachFailure = false, bool ForceOwnedConsoleFailure = false);
internal sealed record CodexTransferError(string Message, string Details);

internal static class CodexHandoff
{
    internal static int Run(CodexTarget selected, TerminalHost host)
    {
        using var target = CodexProcess.Validate(selected);
        var directory = Path.Combine(ProjectPaths.Artifacts, "codex-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string FileIn(string name) => Path.Combine(directory, name);
        var release = FileIn("release");
        Process? guardian = null, anchor = null;
        var workerRunning = false;
        try
        {
            guardian = LabRunner.StartHidden("anchor", FileIn("guardian.json"), release, target.Id.ToString());
            var original = LabRunner.WaitForState(FileIn("guardian.json"), guardian, _ => true);
            var title = $"FP-lab-codex-{target.Id}-{Guid.NewGuid():N}";
            anchor = TerminalDestination.Start(host, directory, title, new Identity(selected.Pid, selected.StartedUtcTicks, "", 0));
            var destination = JsonFile.Read<FixtureState>(FileIn("anchor.json"));
            JsonFile.Write(FileIn("request.json"), new CodexRebindRequest(selected, destination.Identity, original.Identity));
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "codex-worker", FileIn("request.json"), FileIn("api.json") }) start.ArgumentList.Add(arg);
            using var worker = Process.Start(start) ?? throw new IOException("Could not start Codex transfer worker.");
            workerRunning = true;
            var error = worker.StandardError.ReadToEndAsync();
            var output = worker.StandardOutput.ReadToEndAsync();
            // The worker owns its thread leases and releases them itself on every failure path.
            if (!worker.WaitForExit(20000)) throw new TimeoutException("Codex 이관 도우미가 아직 실행 중입니다. 프로세스를 종료하지 않았습니다.");
            workerRunning = false;
            Task.WaitAll(error, output);
            if (worker.ExitCode != 0) throw new IOException(File.Exists(FileIn("api.json.error.json"))
                ? JsonFile.Read<CodexTransferError>(FileIn("api.json.error.json")).Message : error.Result.Trim());
            File.WriteAllText(release, "release");
            if (!guardian.WaitForExit(5000) || !anchor.WaitForExit(5000)) throw new IOException("Codex 이관 도우미의 종료를 확인하지 못했습니다.");
            var completion = JsonFile.Read<SourceCompletion>(FileIn("api.json.source.json"));
            using var checkedTarget = CodexProcess.Validate(completion.LauncherExited ? selected with { Launcher = null } : selected);
            JsonFile.Write(FileIn("session.json"), new { Target = selected, Host = host.ToString(), Title = title,
                WorkerPid = worker.Id, GuardianPid = guardian.Id, AnchorPid = anchor.Id, AllHelpersExited = true,
                SameProcess = true, SourceCompletion = completion, DirectHandoffVerified = false,
                HandoffStatus = "awaiting-terminal-io-verification" });
            if (host != TerminalHost.Current)
            {
                Console.WriteLine($"Codex CLI PID {target.Id} → {host} 연결 완료");
                Console.WriteLine($"Session: {FileIn("session.json")}");
            }
            return 0;
        }
        finally
        {
            if (!workerRunning && !Directory.EnumerateFiles(directory, "*.pending").Any()) File.WriteAllText(release, "release");
            guardian?.Dispose(); anchor?.Dispose();
        }
    }

    internal static int Worker(string requestPath, string resultPath)
    {
        var request = JsonFile.Read<CodexRebindRequest>(requestPath);
        using var target = CodexProcess.Validate(request.Target);
        using var launcher = request.Target.Launcher is { } parent ? CodexProcess.ValidateLauncher(parent) : null;
        using var destination = RemoteRebind.Validate(request.Destination);
        using var guardian = RemoteRebind.Validate(request.Guardian);
        Native.FreeConsole();
        Native.Check(Native.AttachConsole((uint)target.Id), "Attach Codex source for input inspection");
        try
        {
            using (var lease = CodexConsoleLease.Pause(target, destination.Id))
            {
                using var launcherLease = launcher is null ? null : CodexConsoleLease.Pause(launcher, destination.Id, consoleReader: false);
                if (launcher is not null && !Native.ConsoleProcesses().Contains((uint)launcher.Id))
                    throw new IOException("Codex npm launcher is not attached to the original console.");
                using var input = Native.ConsoleDevice("CONIN$");
                Native.Check(Native.GetConsoleMode(input.DangerousGetHandle(), out var mode), "Codex input mode");
                using var originalOutput = Native.ConsoleDevice("CONOUT$");
                Native.Check(Native.GetConsoleMode(originalOutput.DangerousGetHandle(), out var outputMode), "Codex output mode");
                if ((mode & 6) != 0) throw new IOException($"Codex is not in raw console input mode (mode=0x{mode:X}).");
                lease.RebindCachedInput();
                Execute(target, request.ForceAttachFailure ? 0 : destination.Id, guardian.Id, resultPath);
                var launcherMoved = false;
                try
                {
                    if (launcher is not null)
                    {
                        Execute(launcher, request.ForceLauncherAttachFailure ? 0 : destination.Id, guardian.Id, resultPath + ".launcher.json");
                        launcherMoved = true;
                    }
                    Native.FreeConsole();
                    Native.Check(Native.AttachConsole((uint)target.Id), "Configure owned destination console handles");
                    using var handles = CodexConsoleHandles.Rebind(target, lease.ReaderHandle, mode, outputMode, resultPath, request.ForceOwnedConsoleFailure);
                    lease.Commit();
                    handles.Commit();
                }
                catch
                {
                    try
                    {
                        if (launcherMoved) Execute(launcher!, guardian.Id, destination.Id, resultPath + ".launcher-rollback.json");
                    }
                    finally { Execute(target, guardian.Id, destination.Id, resultPath + ".rollback.json"); }
                    throw;
                }
            }
            Native.FreeConsole();
            Native.Check(Native.AttachConsole((uint)target.Id), "Attach moved Codex for redraw");
            RefreshDisplay();
            if (launcher is not null && !Native.ConsoleProcesses().Contains((uint)launcher.Id))
                throw new IOException("Codex launcher did not join the destination console.");
            JsonFile.Write(resultPath + ".source.json", SourceConsole.Complete(guardian, target, launcher));
            return 0;
        }
        catch (Exception error)
        {
            JsonFile.Write(resultPath + ".error.json", new CodexTransferError(error.Message, error.ToString()));
            throw;
        }
        finally
        {
            Native.FreeConsole();
            if (!Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(resultPath))!, "*.pending").Any())
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(requestPath))!, "release"), "release");
        }
    }

    private static void Execute(Process target, int destination, int guardian, string resultPath)
    {
        using var process = Native.OpenProcess(0x43A, false, target.Id);
        Native.Check(!process.IsInvalid, "Open Codex for console transfer");
        Native.Check(Native.IsWow64Process2(process, out var machine, out var nativeMachine), "Codex architecture");
        if (machine != 0 || nativeMachine != 0x8664 || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new PlatformNotSupportedException("Codex 이관은 검증한 Windows x64 빌드만 지원합니다.");
        var allocation = Native.VirtualAllocEx(process, 0, 8192, 0x3000, 4);
        Native.Check(allocation != 0, "Allocate Codex console routine");
        var finished = true;
        try
        {
            var code = RemoteRebind.Build(target, allocation + 4096, destination, guardian, preserveConsoleModes: false);
            Native.Check(Native.WriteProcessMemory(process, allocation, code, (nuint)code.Length, out var written) && written == (nuint)code.Length, "Write fixed Codex console routine");
            Native.Check(Native.VirtualProtectEx(process, allocation, 4096, 0x20, out _), "Protect Codex console routine");
            Native.Check(Native.FlushInstructionCache(process, allocation, (nuint)code.Length), "Flush Codex console routine");
            using var thread = Native.CreateRemoteThread(process, 0, 0, allocation, 0, 0, out _);
            Native.Check(!thread.IsInvalid, "Start Codex console routine");
            finished = false;
            if (Native.WaitForSingleObject(thread, 8000) != 0)
            {
                File.WriteAllText(resultPath + ".pending", "Native routine still active; allocation and console guardians retained.");
                throw new TimeoutException("Codex console routine did not finish; retained its allocation and console guardians.");
            }
            finished = true;
            Native.Check(Native.GetExitCodeThread(thread, out var status), "Codex transfer result");
            var bytes = new byte[256];
            Native.Check(Native.ReadProcessMemory(process, allocation + 4096, bytes, 256, out var read) && read == 256, "Read Codex transfer diagnostics");
            JsonFile.Write(resultPath, new { Status = status, Values = Enumerable.Range(0, 32).Select(i => BitConverter.ToInt64(bytes, i * 8)).ToArray() });
            if (status != 1) throw new IOException($"Codex 콘솔 이관 실패 (status={status}). 진단: {resultPath}");
        }
        finally { if (finished) Native.VirtualFreeEx(process, allocation, 0, 0x8000); }
    }

    private static void RefreshDisplay()
    {
        // Force the CLI's own renderer to repaint its retained state. No screen relay or
        // commands are injected into the conversation. Only the new console is resized.
        using var output = Native.ConsoleDevice("CONOUT$");
        const string inputModes = "\x1b[?2004h\x1b[?1004h"; // Codex startup enables bracketed paste and focus reporting.
        Native.Check(Native.WriteConsoleW(output.DangerousGetHandle(), inputModes, (uint)inputModes.Length, out var written, 0)
            && written == inputModes.Length, "Restore Codex terminal input reporting");
        Native.Check(Native.GetConsoleScreenBufferInfo(output.DangerousGetHandle(), out var original), "Codex destination dimensions");
        if (original.Size.X <= 41 || original.Size.Y < 10) return;
        var smaller = original.Window;
        smaller.Right = (short)Math.Min(smaller.Right, original.Size.X - 2);
        Native.Check(Native.SetConsoleWindowInfo(output.DangerousGetHandle(), true, ref smaller), "Resize Codex destination window");
        Native.Check(Native.SetConsoleScreenBufferSize(output.DangerousGetHandle(), new Native.Coord((short)(original.Size.X - 1), original.Size.Y)), "Refresh Codex destination");
        Thread.Sleep(250);
        Native.Check(Native.SetConsoleScreenBufferSize(output.DangerousGetHandle(), original.Size), "Restore Codex destination size");
        Native.Check(Native.SetConsoleWindowInfo(output.DangerousGetHandle(), true, ref original.Window), "Restore Codex destination window");
    }
}
