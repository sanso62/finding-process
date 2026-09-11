using System.Text.Json;

namespace FindingProcess.Lab;

public sealed record Identity(int Pid, long StartedUtcTicks, string Nonce, long Address);
public sealed record FixtureState(Identity Identity, long Tick, long Value, string LastInput,
    short Columns, short Rows, string? Error, uint MainThreadId = 0);
public sealed record Observation(int HelperPid, Identity Identity, string Challenge,
    string Screen, bool OutputObserved, short Columns, short Rows, uint[] ConsoleProcesses);

// Unknown evidence is deliberately not treated as success. An API HRESULT is only diagnostic.
public sealed record HandoffEvidence
{
    public int? ApiHResult { get; init; }
    public bool SameProcess { get; init; }
    public bool SameMemory { get; init; }
    public bool ProgressContinues { get; init; }
    public bool HelperExited { get; init; }
    public bool PostHocTarget { get; init; }
    public bool TerminalOwnsIo { get; init; }
    public bool OutputAfterHelperExit { get; init; }
    public bool InputAfterHelperExit { get; init; }
    public bool ResizeAfterHelperExit { get; init; }
    public bool UsesRelay { get; init; }
    public bool DirectHandoffVerified => SameProcess && SameMemory && ProgressContinues &&
        HelperExited && PostHocTarget && TerminalOwnsIo && OutputAfterHelperExit &&
        InputAfterHelperExit && ResizeAfterHelperExit && !UsesRelay;
}

internal static class JsonFile
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    internal static void Write<T>(string path, T value)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temp = fullPath + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, fullPath, overwrite: true);
    }

    internal static T Read<T>(string path)
    {
        // The fixture atomically replaces snapshots. Allow replacement while a reader
        // holds the previous file open (Windows otherwise rejects Move/Replace).
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(file, Options)
            ?? throw new InvalidDataException($"Empty JSON: {path}");
    }
}
