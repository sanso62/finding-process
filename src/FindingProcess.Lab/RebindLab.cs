using System.Diagnostics;

namespace FindingProcess.Lab;

internal static class RebindLab
{
    internal static int Cleanup(string sessionPath)
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sessionPath));
        var identity = System.Text.Json.JsonSerializer.Deserialize<Identity>(json.RootElement.GetProperty("Target"))!;
        Process process;
        try { process = RemoteRebind.Validate(identity); }
        catch (ArgumentException) { return 0; } // Already exited; never act on another PID.
        using (process)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(sessionPath))!;
            LabRunner.RunHelper(Path.Combine(directory, "fixture.json"), Path.Combine(directory, "cleanup.json"),
                identity, "quit", 0, 80, 25);
            if (!process.WaitForExit(3000)) throw new TimeoutException("Fixture did not exit after quit.");
            JsonFile.Write(Path.Combine(directory, "cleanup-result.json"), new { Pid = process.Id, Exited = true, process.ExitCode });
            return 0;
        }
    }

    internal static int Run(bool terminal, bool rollbackTest = false)
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "rebind-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        string FileIn(string name) => Path.Combine(directory, name);
        var fixturePath = FileIn("fixture.json");
        var anchorPath = FileIn("anchor.json");
        var guardianPath = FileIn("guardian.json");
        var release = FileIn("release");
        var title = "FP-lab-" + Path.GetFileName(directory)[7..15];
        var events = new List<object>();
        void Record(string stage, object data)
        {
            events.Add(new { Stage = stage, Utc = DateTime.UtcNow, Data = data });
            JsonFile.Write(FileIn("progress.json"), events);
        }
        Process? fixture = null, anchor = null, guardian = null;
        var keepFixture = false;
        try
        {
            fixture = LabRunner.StartFixture(fixturePath, 300);
            var before = LabRunner.WaitForState(fixturePath, fixture, s => s.Tick >= 2);
            Record("before", before);
            JsonFile.Write(FileIn("before.json"), before);
            LabRunner.Observe(fixturePath, directory, before.Identity, "before", 7, 80, 25);
            guardian = LabRunner.StartHidden("anchor", guardianPath, release, fixture.Id.ToString());
            var guardianState = LabRunner.WaitForState(guardianPath, guardian, _ => true);
            if (terminal)
            {
                var launch = new ProcessStartInfo("wt.exe") { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "-w", "new", "new-tab", "--title", title, "--suppressApplicationTitle",
                             Environment.ProcessPath!, "anchor", anchorPath, release, "0" }) launch.ArgumentList.Add(arg);
                using var starter = Process.Start(launch) ?? throw new IOException("Could not launch Windows Terminal.");
                var watch = Stopwatch.StartNew();
                while (!File.Exists(anchorPath) && watch.Elapsed.TotalSeconds < 15) Thread.Sleep(50);
                if (!File.Exists(anchorPath)) throw new TimeoutException("Windows Terminal anchor did not start.");
                anchor = RemoteRebind.Validate(JsonFile.Read<FixtureState>(anchorPath).Identity);
            }
            else anchor = LabRunner.StartHidden("anchor", anchorPath, release, "0");
            var anchorState = LabRunner.WaitForState(anchorPath, anchor, _ => true);
            Record("destination-ready", anchorState);
            var requestPath = FileIn("request.json");
            JsonFile.Write(requestPath, new RebindRequest(before.Identity, anchorState.Identity, guardianState.Identity,
                before.MainThreadId, rollbackTest));
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
            };
            foreach (var arg in new[] { "rebind-worker", requestPath, FileIn("api.json") }) start.ArgumentList.Add(arg);
            int workerPid;
            using (var worker = Process.Start(start) ?? throw new IOException("Could not start rebind worker."))
            {
                workerPid = worker.Id;
                var error = worker.StandardError.ReadToEndAsync();
                var output = worker.StandardOutput.ReadToEndAsync();
                if (!worker.WaitForExit(12000))
                {
                    worker.Kill(); worker.WaitForExit(3000);
                    throw new TimeoutException("Rebind worker timed out; fixture will be cleaned up.");
                }
                Task.WaitAll(error, output);
                if (worker.ExitCode != (rollbackTest ? 2 : 0)) throw new IOException($"Rebind worker failed: {error.Result}; inspect api.json.");
            }
            Record("rebind-worker-exited", new { Pid = workerPid });
            if (rollbackTest)
            {
                using var api = System.Text.Json.JsonDocument.Parse(File.ReadAllText(FileIn("api.json")));
                if (api.RootElement.GetProperty("Status").GetInt32() != 2) throw new IOException("Rollback API did not succeed.");
                var originalConsole = LabRunner.Observe(fixturePath, directory, before.Identity, "rollback", 0, 80, 25);
                if (!originalConsole.ConsoleProcesses.Contains((uint)guardian.Id) ||
                    originalConsole.ConsoleProcesses.Contains((uint)anchor.Id))
                    throw new IOException("Target did not return to original console.");
                Record("rollback-original-console-verified", originalConsole);
            }
            File.WriteAllText(release, "release");
            if (!anchor.WaitForExit(5000) || !guardian.WaitForExit(5000)) throw new TimeoutException("Anchor/guardian did not exit.");
            Record("all-helpers-exited", new { Worker = workerPid, Anchor = anchor.Id, Guardian = guardian.Id });
            var after = LabRunner.WaitForState(fixturePath, fixture, s => s.Tick > before.Tick + 6);
            using var verified = RemoteRebind.Validate(before.Identity);
            if (after.Identity != before.Identity || after.Value != 7) throw new IOException("Fixture state not retained.");
            JsonFile.Write(FileIn("after.json"), after);
            if (!terminal)
            {
                var observed = LabRunner.Observe(fixturePath, directory, before.Identity, "after", 11, 97, 31);
                if (!observed.OutputObserved) throw new IOException("Rebound console did not carry I/O.");
                after = LabRunner.WaitForState(fixturePath, fixture, s => s.Value == 18 && s.Columns == 97 && s.Rows == 31);
                Record("new-console-io-verified", after);
            }
            var challenge = Guid.NewGuid().ToString("N");
            JsonFile.Write(FileIn("session.json"), new
            {
                Directory = directory, Title = title, Terminal = terminal, Target = before.Identity,
                Before = before, After = after, WorkerPid = workerPid, AnchorPid = anchor.Id, GuardianPid = guardian.Id,
                AllHelpersExited = true, InputToTest = $"add 11 {challenge}",
                HandoffStatus = rollbackTest ? "rollback-verified" : terminal ? "awaiting-terminal-io-verification" : "hidden-console-rebind-verified",
                DirectHandoffVerified = false
            });
            Record("mechanism-verified", new { SamePidAndMemory = true, terminal });
            keepFixture = terminal;
            Console.WriteLine($"{(rollbackTest ? "Rollback" : "Rebind mechanism")}: PASS; Windows Terminal direct I/O: {(terminal ? "AWAITING VERIFICATION" : "NOT TESTED")}");
            Console.WriteLine($"Session: {FileIn("session.json")}");
            if (terminal) Console.WriteLine($"Fixture expires after 300 seconds. In {title}, enter: add 11 {challenge}");
            return 0;
        }
        catch (Exception error)
        {
            Record("failed", new { Error = error.ToString() });
            JsonFile.Write(FileIn("result.json"), new { Passed = false, DirectHandoffVerified = false, Error = error.ToString() });
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(directory);
            return 1;
        }
        finally
        {
            File.WriteAllText(release, "release");
            foreach (var process in new[] { guardian, anchor, keepFixture ? null : fixture })
            {
                if (process is null) continue;
                try
                {
                    if (!process.HasExited)
                    {
                        if (process == fixture && File.Exists(fixturePath))
                        {
                            try
                            {
                                var state = JsonFile.Read<FixtureState>(fixturePath);
                                LabRunner.RunHelper(fixturePath, FileIn("cleanup.json"), state.Identity, "quit", 0, 80, 25);
                            }
                            catch (Exception cleanupError) { Record("graceful-cleanup-failed", cleanupError.Message); }
                        }
                        if (!process.WaitForExit(1500)) { process.Kill(); process.WaitForExit(3000); }
                    }
                }
                finally { process.Dispose(); }
            }
            if (keepFixture) fixture?.Dispose();
        }
    }
}
