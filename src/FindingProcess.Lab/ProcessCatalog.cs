using System.Diagnostics;

namespace FindingProcess.Lab;

internal sealed record TransferTarget(string StatePath, string ImagePath, Identity Identity);
internal sealed record ProcessEntry(int Pid, string Name, TransferTarget? Target, CodexTarget? Codex = null)
{
    internal bool CanTransfer => Target is not null || Codex is not null;
}

internal static class ProcessCatalog
{
    internal static List<ProcessEntry> Read()
    {
        var targets = new Dictionary<int, TransferTarget>();
        if (Directory.Exists(ProjectPaths.Artifacts))
            foreach (var directory in Directory.EnumerateDirectories(ProjectPaths.Artifacts))
            {
                var path = Path.Combine(directory, "fixture.json");
                if (!File.Exists(path)) continue;
                try
                {
                    var state = JsonFile.Read<FixtureState>(path);
                    if (state.Error is not null || state.MainThreadId == 0 || state.Tick <= 0) continue;
                    using var process = Process.GetProcessById(state.Identity.Pid);
                    var image = process.MainModule?.FileName;
                    if (image is null || !(ProjectPaths.IsWithin(image, ProjectPaths.Artifacts) ||
                        ProjectPaths.IsWithin(image, Path.Combine(ProjectPaths.Root, "src", "FindingProcess.Lab", "bin")))) continue;
                    using var validated = RemoteRebind.Validate(state.Identity, image);
                    targets[process.Id] = new(path, image, state.Identity);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or
                    System.ComponentModel.Win32Exception or System.Text.Json.JsonException or UnauthorizedAccessException) { }
            }

        using var current = Process.GetCurrentProcess();
        var entries = new List<ProcessEntry>();
        foreach (var process in Process.GetProcesses())
            using (process)
                try
                {
                    if (process.Id == current.Id || process.SessionId != current.SessionId) continue;
                    CodexTarget? codex = null;
                    try { codex = CodexProcess.Identify(process); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    entries.Add(new(process.Id, process.ProcessName, targets.GetValueOrDefault(process.Id), codex));
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return entries.OrderByDescending(p => p.CanTransfer).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Pid).ToList();
    }
}
