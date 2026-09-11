using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FindingProcess.Lab;

internal static class LabRunner
{
    internal static int Run(string outputDirectory)
    {
        var directory = Path.GetFullPath(Path.Combine(outputDirectory,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]));
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "fixture.json");
        var reportPath = Path.Combine(directory, "result.json");
        var events = new List<object>();
        Process? fixture = null;
        FixtureState? initial = null;
        var evidence = new HandoffEvidence();
        var passed = false;
        string? failure = null;
        void Record(string stage, object data)
        {
            events.Add(new { Utc = DateTime.UtcNow, Stage = stage, Data = data });
            JsonFile.Write(Path.Combine(directory, "progress.json"), events);
        }
        try
        {
            fixture = StartFixture(statePath);
            Record("fixture-started", new { fixture.Id });
            initial = WaitForState(statePath, fixture, state => state.Tick >= 2);
            JsonFile.Write(Path.Combine(directory, "before.json"), initial);
            var beforeMemory = ReadMemory(initial.Identity);
            Require(Convert.ToHexString(beforeMemory) == initial.Identity.Nonce, "Initial memory read differs from fixture nonce.");
            Record("baseline", initial);

            var first = Observe(statePath, directory, initial.Identity, "first", 7, 80, 25);
            Require(first.OutputObserved, "First input/output challenge was not observed.");
            Record("first-helper-exited", first);
            var afterFirst = WaitForState(statePath, fixture, state => state.Value == 7);
            var independent = WaitForState(statePath, fixture, state => state.Tick >= afterFirst.Tick + 3);
            Record("progress-with-no-helper", independent);

            var second = Observe(statePath, directory, initial.Identity, "second", 11, 97, 31);
            var final = WaitForState(statePath, fixture, state => state.Value == 18 && state.Columns == 97 && state.Rows == 31);
            var afterMemory = ReadMemory(initial.Identity);
            JsonFile.Write(Path.Combine(directory, "after.json"), final);
            evidence = new HandoffEvidence
            {
                SameProcess = initial.Identity == final.Identity &&
                    fixture.StartTime.ToUniversalTime().Ticks == initial.Identity.StartedUtcTicks,
                SameMemory = beforeMemory.SequenceEqual(afterMemory),
                ProgressContinues = final.Tick > initial.Tick,
                HelperExited = true,
                PostHocTarget = true,
                // These observations use the original console and a second observer.
                // None are evidence of Windows Terminal owning input, output, or resize.
                TerminalOwnsIo = false,
                OutputAfterHelperExit = false,
                InputAfterHelperExit = false,
                ResizeAfterHelperExit = false,
                UsesRelay = false
            };
            Require(second.OutputObserved && evidence.SameProcess && evidence.SameMemory && evidence.ProgressContinues,
                "Console baseline did not preserve process, memory, progress, or I/O.");
            Require(first.HelperPid != second.HelperPid, "Observer must be a separate, already-exited process.");
            passed = true;
            Record("baseline-verified", new { Evidence = evidence, Final = final });
        }
        catch (Exception error)
        {
            failure = error.ToString();
            Record("failed", new { Error = failure });
        }
        finally
        {
            if (fixture is not null)
            {
                try
                {
                    if (!fixture.HasExited && initial is not null)
                        RunHelper(statePath, Path.Combine(directory, "cleanup.json"), initial.Identity, "quit", 0, 80, 25);
                    if (!fixture.WaitForExit(2000))
                    {
                        // This Process holds the handle returned for OUR fixture only. No process-tree kill.
                        fixture.Kill();
                        fixture.WaitForExit(5000);
                        Record("fixture-cleanup", new { Forced = true, fixture.Id });
                    }
                    else Record("fixture-cleanup", new { Forced = false, fixture.Id });
                }
                catch (Exception error)
                {
                    passed = false;
                    failure = (failure ?? "") + "\nCleanup: " + error;
                    Record("cleanup-failed", new { Error = error.ToString() });
                    // The fixture also has a 90-second self-expiry as a final bounded fallback.
                }
                fixture.Dispose();
            }
            JsonFile.Write(reportPath, new
            {
                SchemaVersion = 1,
                Experiment = "attach-console-baseline",
                OsVersion = Environment.OSVersion.VersionString,
                Runtime = RuntimeInformation.FrameworkDescription,
                BaselinePassed = passed,
                HandoffStatus = "not-attempted",
                Evidence = evidence,
                Failure = failure,
                Note = "Original-console I/O only. This is not a Windows Terminal handoff.",
                Events = events
            });
        }
        Console.WriteLine($"Console baseline: {(passed ? "PASS" : "FAIL")}");
        Console.WriteLine("Direct Windows Terminal handoff: NOT ATTEMPTED");
        Console.WriteLine($"Report: {reportPath}");
        if (failure is not null) Console.Error.WriteLine(failure);
        return passed ? 0 : 1;
    }

    internal static Process StartFixture(string statePath, int lifetimeSeconds = 90)
        => StartHidden("fixture", statePath, lifetimeSeconds.ToString());

    internal static Process StartHidden(params string[] arguments)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
        if (!Path.GetFileNameWithoutExtension(executable).Equals("FindingProcess.Lab", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run the built apphost executable, not 'dotnet FindingProcess.Lab.dll'.");
        var command = new StringBuilder(Quote(executable) + " " + string.Join(" ", arguments.Select(Quote)));
        var startup = new Native.StartupInfo
        {
            Size = Marshal.SizeOf<Native.StartupInfo>(), Flags = 1, ShowWindow = 0,
            Title = "Finding Process isolated fixture"
        };
        Native.Check(Native.CreateProcessW(executable, command, 0, 0, false, 0x10, 0, null,
            ref startup, out var created), "CreateProcess(fixture, CREATE_NEW_CONSOLE)");
        try
        {
            var process = Process.GetProcessById((int)created.Pid);
            _ = process.Handle; // Pin identity before closing the original creation handle.
            return process;
        }
        finally
        {
            Native.CloseHandle(created.Thread);
            Native.CloseHandle(created.Process);
        }
    }

    private static string Quote(string value)
    {
        // These arguments are absolute file names, never a shell command.
        if (value.Contains('"') || value.EndsWith('\\')) throw new ArgumentException("Invalid file argument.");
        return '"' + value + '"';
    }

    internal static FixtureState WaitForState(string path, Process fixture, Func<FixtureState, bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (fixture.HasExited) throw new IOException($"Fixture exited early ({fixture.ExitCode}).");
            if (File.Exists(path))
            {
                var state = JsonFile.Read<FixtureState>(path);
                if (state.Error is not null) throw new IOException(state.Error);
                if (predicate(state)) return state;
            }
            Thread.Sleep(30);
        }
        throw new TimeoutException("Timed out waiting for fixture evidence.");
    }

    internal static Observation Observe(string statePath, string directory, Identity identity, string label,
        int amount, short columns, short rows)
    {
        var report = Path.Combine(directory, $"{label}-observer.json");
        RunHelper(statePath, report, identity, Guid.NewGuid().ToString("N"), amount, columns, rows);
        return JsonFile.Read<Observation>(report);
    }

    internal static void RunHelper(string statePath, string report, Identity identity,
        string challenge, int amount, short columns, short rows)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var value in new[] { "observe", statePath, report, identity.StartedUtcTicks.ToString(), challenge,
                     amount.ToString(), columns.ToString(), rows.ToString() }) start.ArgumentList.Add(value);
        using var helper = Process.Start(start) ?? throw new IOException("Could not start observer.");
        var stderr = helper.StandardError.ReadToEndAsync();
        var stdout = helper.StandardOutput.ReadToEndAsync();
        if (!helper.WaitForExit(12000))
        {
            helper.Kill(); // Only our helper, never the attached target.
            helper.WaitForExit(3000);
            throw new TimeoutException("Console observer timed out.");
        }
        Task.WaitAll(stderr, stdout);
        if (helper.ExitCode != 0) throw new IOException($"Observer failed ({helper.ExitCode}): {stderr.Result}");
    }

    private static byte[] ReadMemory(Identity identity)
    {
        using var process = Process.GetProcessById(identity.Pid);
        _ = process.Handle;
        Require(process.StartTime.ToUniversalTime().Ticks == identity.StartedUtcTicks, "PID reused before memory read.");
        using var handle = Native.OpenProcess(0x10 | 0x1000, false, identity.Pid);
        Native.Check(!handle.IsInvalid, "OpenProcess(read-only memory)");
        var buffer = new byte[32];
        Native.Check(Native.ReadProcessMemory(handle, (nint)identity.Address, buffer, (nuint)buffer.Length, out var read),
            "ReadProcessMemory(fixture)");
        Require(read == (nuint)buffer.Length, "Incomplete memory read.");
        return buffer;
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
