using System.Diagnostics;

namespace FindingProcess.Lab;

internal static class RestoreDemo
{
    private sealed record Demo(string StatePath, Identity Target);

    internal static int Run(string manifestPath, TerminalHost host = TerminalHost.WindowsTerminal)
    {
        manifestPath = Path.GetFullPath(manifestPath);
        var liveDirectory = Path.GetDirectoryName(manifestPath)!;
        var artifacts = Directory.GetParent(liveDirectory)!;
        if (Path.GetFileName(manifestPath) != "demo.json" || !Path.GetFileName(liveDirectory).StartsWith("live-") ||
            artifacts.Name != "artifacts") throw new InvalidDataException("Use an artifacts/live-*/demo.json manifest.");
        var demo = JsonFile.Read<Demo>(manifestPath);
        if (!string.Equals(Path.GetFullPath(demo.StatePath), Path.Combine(liveDirectory, "fixture.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected demo state path.");
        var imagePath = Path.Combine(artifacts.Parent!.FullName,
            "src", "FindingProcess.Lab", "bin", "Release", "net9.0-windows", "FindingProcess.Lab.exe");
        using var target = RemoteRebind.Validate(demo.Target, imagePath);
        var before = JsonFile.Read<FixtureState>(demo.StatePath);
        if (before.Identity != demo.Target || before.Error is not null || before.MainThreadId == 0)
            throw new InvalidDataException("Demo state is not the live, healthy fixture.");

        var directory = Path.Combine(artifacts.FullName, "restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string FileIn(string name) => Path.Combine(directory, name);
        var release = FileIn("release");
        var title = $"FP-lab-restored-{target.Id}-{Guid.NewGuid().ToString("N")[..6]}";
        Process? anchor = null, guardian = null;
        var events = new List<object>();
        void Record(string stage, object data)
        {
            events.Add(new { Stage = stage, Utc = DateTime.UtcNow, Data = data });
            JsonFile.Write(FileIn("progress.json"), events);
        }
        try
        {
            JsonFile.Write(FileIn("before.json"), before);
            Record("existing-demo-validated", before);
            guardian = LabRunner.StartHidden("anchor", FileIn("guardian.json"), release, target.Id.ToString());
            var guardianState = LabRunner.WaitForState(FileIn("guardian.json"), guardian, _ => true);
            anchor = TerminalDestination.Start(host, directory, title, demo.Target);
            var anchorState = JsonFile.Read<FixtureState>(FileIn("anchor.json"));
            JsonFile.Write(FileIn("request.json"), new RebindRequest(demo.Target, anchorState.Identity,
                guardianState.Identity, before.MainThreadId, TargetImagePath: imagePath));
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
            };
            foreach (var arg in new[] { "rebind-worker", FileIn("request.json"), FileIn("api.json") }) start.ArgumentList.Add(arg);
            int workerPid;
            using (var worker = Process.Start(start) ?? throw new IOException("Could not start rebind worker."))
            {
                workerPid = worker.Id;
                var error = worker.StandardError.ReadToEndAsync();
                var output = worker.StandardOutput.ReadToEndAsync();
                // Do not kill a worker that may own a suspended live thread. Its bounded routine
                // unwinds the suspension itself; on timeout retain its diagnostics and return.
                if (!worker.WaitForExit(15000)) throw new TimeoutException("Restore worker still running; target was not terminated.");
                Task.WaitAll(error, output);
                if (worker.ExitCode != 0) throw new IOException($"Restore did not complete: {error.Result}; inspect api.json. Target was not terminated.");
            }
            Record("restore-worker-exited", new { Pid = workerPid });
            File.WriteAllText(release, "release");
            if (!anchor.WaitForExit(5000) || !guardian.WaitForExit(5000)) throw new TimeoutException("Bootstrap helpers have not exited.");
            var checkpoint = JsonFile.Read<FixtureState>(demo.StatePath);
            var after = LabRunner.WaitForState(demo.StatePath, target, s => s.Tick >= checkpoint.Tick + 2);
            using var identityCheck = RemoteRebind.Validate(demo.Target, imagePath);
            if (after.Identity != demo.Target || after.Value != before.Value) throw new IOException("Demo state changed during restore.");
            JsonFile.Write(FileIn("after.json"), after);
            JsonFile.Write(FileIn("session.json"), new
            {
                Directory = directory, StatePath = demo.StatePath, Title = title, Terminal = true, LiveDemo = true, Host = host.ToString(),
                Target = demo.Target, Before = before, After = after, WorkerPid = workerPid,
                AnchorPid = anchor.Id, GuardianPid = guardian.Id, AllHelpersExited = true,
                InputToTest = $"add 0 {Guid.NewGuid():N}", DirectHandoffVerified = false,
                HandoffStatus = "awaiting-terminal-io-verification"
            });
            Record("same-demo-running-in-terminal", after);
            Console.WriteLine($"Restored existing PID: {target.Id}; value retained: {after.Value}; destination: {host}");
            Console.WriteLine($"Session: {FileIn("session.json")}");
            return 0;
        }
        catch (Exception error)
        {
            Record("restore-failed", new { Error = error.ToString(), TargetExited = target.HasExited });
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(directory);
            return 1;
        }
        finally
        {
            // Only our bootstrap helpers receive an exit request. The live demo is never killed,
            // restarted, sent quit, or included in the disposable experiment cleanup path.
            File.WriteAllText(release, "release");
            anchor?.Dispose();
            guardian?.Dispose();
        }
    }
}
