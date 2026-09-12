using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FindingProcess.Lab;

internal sealed record CodexLauncher(int Pid, long StartedUtcTicks, string ImagePath, string ImageSha256, string ScriptPath);
internal sealed record CodexTarget(int Pid, long StartedUtcTicks, string ImagePath, CodexLauncher? Launcher = null);

internal static class CodexProcess
{
    // This adapter depends on the Windows input implementation in this exact release.
    // A new binary must pass the console migration experiment before being added here.
    private const string SupportedSha256 = "BE96B992178B1E467C225800DA0D65F2C86D5EBA1EF0B14632F65DB381CBDFDE";
    private const string LauncherScriptSha256 = "61B0194F3BB6534439C8D26A3ED57D0805F84B884588B761795323EEB92FCF70";
    private static readonly Dictionary<string, (long Length, DateTime Modified, bool Supported)> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal static CodexTarget? Identify(Process process)
    {
        if (!process.ProcessName.Equals("codex", StringComparison.OrdinalIgnoreCase)) return null;
        var image = process.MainModule?.FileName;
        if (image is null || !Supported(image)) return null;
        var command = CommandLine(process);
        // Server and non-interactive modes do not have the CLI's console event reader.
        if (new[] { "app-server", "exec-server", " exec ", " review ", " mcp ", "--help", "--version" }
            .Any(word => command.Contains(word, StringComparison.OrdinalIgnoreCase))) return null;
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks, image, IdentifyLauncher(process));
    }

    private static CodexLauncher? IdentifyLauncher(Process target)
    {
        var basic = Marshal.AllocHGlobal(48);
        int parentPid;
        try
        {
            CodexNative.CheckStatus(CodexNative.NtQueryInformationProcess(target.Handle, 0, basic, 48, out _), "Read Codex parent");
            parentPid = checked((int)Marshal.ReadIntPtr(basic, 40));
        }
        finally { Marshal.FreeHGlobal(basic); }
        Process parent;
        try { parent = Process.GetProcessById(parentPid); }
        catch (ArgumentException) { return null; }
        using (parent)
        {
            if (parent.StartTime.ToUniversalTime() > target.StartTime.ToUniversalTime() || !parent.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase)) return null;
            var args = CodexNative.CommandLineToArgvW(CommandLine(parent), out var count);
            if (args == 0) throw new IOException("Cannot inspect Codex npm launcher.");
            try
            {
                if (count < 2) throw new IOException("Unrecognized Codex Node launcher.");
                var script = Path.GetFullPath(Marshal.PtrToStringUni(Marshal.ReadIntPtr(args, nint.Size))!);
                if (!Path.GetFileName(script).Equals("codex.js", StringComparison.OrdinalIgnoreCase) || Hash(script) != LauncherScriptSha256)
                    throw new IOException("Unverified Codex Node launcher; refusing to leave a signal-forwarding parent behind.");
                var image = parent.MainModule?.FileName ?? throw new IOException("Codex launcher image unavailable.");
                return new(parent.Id, parent.StartTime.ToUniversalTime().Ticks, image, Hash(image), script);
            }
            finally { CodexNative.LocalFree(args); }
        }
    }

    internal static Process ValidateLauncher(CodexLauncher launcher)
    {
        var process = Process.GetProcessById(launcher.Pid);
        try
        {
            _ = process.Handle;
            if (process.StartTime.ToUniversalTime().Ticks != launcher.StartedUtcTicks ||
                !string.Equals(process.MainModule?.FileName, launcher.ImagePath, StringComparison.OrdinalIgnoreCase) ||
                Hash(launcher.ImagePath) != launcher.ImageSha256 || Hash(launcher.ScriptPath) != LauncherScriptSha256)
                throw new IOException("Codex npm launcher identity changed.");
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static Process Validate(CodexTarget target)
    {
        var process = Process.GetProcessById(target.Pid);
        try
        {
            _ = process.Handle;
            if (process.StartTime.ToUniversalTime().Ticks != target.StartedUtcTicks ||
                !string.Equals(process.MainModule?.FileName, target.ImagePath, StringComparison.OrdinalIgnoreCase) ||
                !Supported(target.ImagePath, refresh: true) || Identify(process) != target)
                throw new InvalidOperationException("선택한 Codex CLI의 신원이 변경되었거나 지원하는 빌드가 아닙니다.");
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    private static bool Supported(string path, bool refresh = false)
    {
        var file = new FileInfo(path);
        if (!refresh && Cache.TryGetValue(path, out var cached) && cached.Length == file.Length && cached.Modified == file.LastWriteTimeUtc)
            return cached.Supported;
        using var stream = File.OpenRead(path);
        var supported = Convert.ToHexString(SHA256.HashData(stream)) == SupportedSha256;
        Cache[path] = (file.Length, file.LastWriteTimeUtc, supported);
        return supported;
    }

    private static string CommandLine(Process process)
    {
        CodexNative.NtQueryInformationProcess(process.Handle, 60, 0, 0, out var size);
        if (size is < 16 or > 131072) throw new IOException("Codex command line unavailable.");
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            CodexNative.CheckStatus(CodexNative.NtQueryInformationProcess(process.Handle, 60, buffer, size, out _), "Read Codex command line");
            return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, 8), (ushort)Marshal.ReadInt16(buffer) / 2) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
