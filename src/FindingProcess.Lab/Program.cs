namespace FindingProcess.Lab;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Cache stderr while it still refers to the launcher's pipe; observers detach their console.
        var errorOutput = Console.Error;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
            return args switch
            {
                [] or ["--terminal"] => ProcessPicker.Run(),
                ["--vscode"] => ProcessPicker.Run(TerminalHost.VSCode),
                ["list"] => ProcessPicker.Run(listOnly: true),
                ["baseline"] => LabRunner.Run("artifacts"),
                ["baseline", var directory] => LabRunner.Run(directory),
                ["restore-demo", var demoManifest] => RestoreDemo.Run(demoManifest),
                ["restore-demo", var vscodeManifest, "--vscode"] => RestoreDemo.Run(vscodeManifest, TerminalHost.VSCode),
                ["rebind-lab"] => RebindLab.Run(TerminalHost.Hidden),
                ["rebind-lab", "--terminal"] => RebindLab.Run(TerminalHost.WindowsTerminal),
                ["rebind-lab", "--vscode"] => RebindLab.Run(TerminalHost.VSCode),
                ["rebind-lab", "--rollback"] => RebindLab.Run(TerminalHost.Hidden, true),
                ["cleanup-session", var sessionPath] => RebindLab.Cleanup(sessionPath),
                ["rebind-worker", var request, var result] => RemoteRebind.Run(request, result),
                ["anchor", var stateFile, var releaseFile, var attach] when int.TryParse(attach, out var anchorPid)
                    => Anchor.Run(stateFile, releaseFile, anchorPid),
                ["handle-probe", var destination, var report] when int.TryParse(destination, out var targetPid)
                    => HandleProbe.Run(targetPid, report),
                ["fixture", var path, var seconds] when int.TryParse(seconds, out var duration) && duration is > 0 and <= 300
                    => Fixture.Run(path, duration),
                ["fixture", var livePath, "--forever"] => Fixture.Run(livePath, null),
                ["observe", var state, var report, var ticks, var challenge, var amount, var columns, var rows]
                    when long.TryParse(ticks, out var started) && int.TryParse(amount, out var add) &&
                         short.TryParse(columns, out var width) && short.TryParse(rows, out var height) &&
                         width is >= 40 and <= 120 && height is >= 10 and <= 40 &&
                         (challenge == "quit" || Guid.TryParseExact(challenge, "N", out _))
                    => Observer.Run(state, report, started, challenge, add, width, height),
                ["--help"] => Usage(0),
                _ => Usage(2)
            };
        }
        catch (Exception error)
        {
            errorOutput.WriteLine(error);
            return 1;
        }
    }

    private static int Usage(int status)
    {
        Console.WriteLine("finding-process [--terminal | --vscode]  (Up/Down: select, Enter: transfer, Ctrl+C: exit)");
        Console.WriteLine("finding-process list");
        Console.WriteLine("FindingProcess.Lab baseline [artifact-directory]");
        Console.WriteLine("FindingProcess.Lab rebind-lab [--terminal | --vscode | --rollback]");
        Console.WriteLine("FindingProcess.Lab fixture <state-file> --forever   (commands: add 5, quit)");
        Console.WriteLine("FindingProcess.Lab restore-demo <artifacts/live-*/demo.json> [--vscode]");
        Console.WriteLine("Creates its own hidden console fixture; verifies original-console I/O and state preservation.");
        Console.WriteLine("Experimental direct rebind is limited to attested native x64 lab fixtures. No arbitrary-PID recovery command is exposed.");
        return status;
    }
}
