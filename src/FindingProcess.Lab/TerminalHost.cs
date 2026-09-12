using System.Diagnostics;

namespace FindingProcess.Lab;

internal enum TerminalHost { Current, Hidden, WindowsTerminal, VSCode }

internal static class TerminalDestination
{
    internal static Process Start(TerminalHost host, string directory, string title, Identity target)
    {
        var statePath = Path.Combine(directory, "anchor.json");
        var release = Path.Combine(directory, "release");
        if (host == TerminalHost.Current)
        {
            var anchor = LabRunner.StartHidden("anchor", statePath, release, Environment.ProcessId.ToString());
            try { LabRunner.WaitForState(statePath, anchor, _ => true); return anchor; }
            catch { anchor.Dispose(); throw; }
        }
        if (host == TerminalHost.Hidden)
            return LabRunner.StartHidden("anchor", statePath, release, "0");

        ProcessStartInfo launch;
        if (host == TerminalHost.WindowsTerminal)
        {
            launch = new("wt.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-w", "new", "new-tab", "--title", title, "--suppressApplicationTitle",
                         Environment.ProcessPath!, "anchor", statePath, release, "0" }) launch.ArgumentList.Add(arg);
        }
        else
        {
            var extension = Path.Combine(ProjectPaths.Root, "src", "FindingProcess.VSCode");
            if (!File.Exists(Path.Combine(extension, "package.json")))
                throw new FileNotFoundException("Run from the repository root; src/FindingProcess.VSCode is required.");
            var code = FindCode();
            var workspace = Path.Combine(directory, "vscode-workspace");
            var profile = Path.Combine(directory, "vscode-profile");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(Path.Combine(profile, "User"));
            // Scope preferences and extension loading to a dedicated window/profile. Never change
            // the user's VS Code settings or install into their normal extension directory.
            JsonFile.Write(Path.Combine(profile, "User", "settings.json"), new Dictionary<string, object>
            {
                ["security.workspace.trust.enabled"] = false,
                ["workbench.startupEditor"] = "none",
                ["workbench.secondarySideBar.defaultVisibility"] = "hidden",
                ["chat.disableAIFeatures"] = true,
                ["git.openRepositoryInParentFolders"] = "never",
                ["window.title"] = title,
                ["editor.accessibilitySupport"] = "on",
                ["terminal.integrated.enablePersistentSessions"] = false,
                ["terminal.integrated.shellIntegration.enabled"] = false,
                ["terminal.integrated.defaultLocation"] = "editor",
                ["telemetry.telemetryLevel"] = "off",
                ["update.mode"] = "none"
            });
            // PowerShell is the terminal's normal shell. While the attached process runs it waits
            // without reading input; all FindingProcess handoff processes can exit. Keeping this
            // shell alive preserves node-pty's root process and therefore terminal resize support.
            var script = Path.Combine(directory, "vscode-shell.ps1");
            File.WriteAllText(script, $$"""
                $ErrorActionPreference = 'Stop'
                $attachedTarget = Get-Process -Id {{target.Pid}}
                if ($attachedTarget.StartTime.ToUniversalTime().Ticks -ne {{target.StartedUtcTicks}}) { throw 'Target PID was reused.' }
                try {
                    & {{Quote(Environment.ProcessPath!)}} anchor {{Quote(statePath)}} {{Quote(release)}} 0
                    if ($LASTEXITCODE -ne 0) { throw 'Terminal anchor failed.' }
                    $attachedTarget.WaitForExit()
                }
                finally { $attachedTarget.Dispose() }
                """, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            JsonFile.Write(Path.Combine(workspace, "handoff.json"), new
            {
                Version = 1, Title = title, Script = script,
                Shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe")
            });
            // ShellExecute separates this GUI from the runner's redirected stdout/stderr.
            // Otherwise a pipeline invoking the lab can wait for the entire VS Code window.
            launch = new(code) { UseShellExecute = true };
            foreach (var arg in new[] { "--new-window", "--user-data-dir", profile,
                         "--extensions-dir", Path.Combine(profile, "extensions"),
                         "--extensionDevelopmentPath=" + extension, workspace }) launch.ArgumentList.Add(arg);
        }
        using var starter = Process.Start(launch) ?? throw new IOException("Could not launch destination terminal.");
        var watch = Stopwatch.StartNew();
        var timeout = host == TerminalHost.VSCode ? 60 : 15;
        while (!File.Exists(statePath) && watch.Elapsed.TotalSeconds < timeout)
        {
            var failure = Path.Combine(directory, "vscode-error.json");
            if (File.Exists(failure)) throw new IOException(File.ReadAllText(failure));
            Thread.Sleep(50);
        }
        if (!File.Exists(statePath)) throw new TimeoutException($"{host} destination did not become ready.");
        return RemoteRebind.Validate(JsonFile.Read<FixtureState>(statePath).Identity);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static string FindCode()
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(Path.Combine(p.Trim('"'), "..", "Code.exe")))
            .Concat(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe")
            });
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("VS Code (Code.exe) was not found. Install VS Code or add its bin folder to PATH.");
    }
}
