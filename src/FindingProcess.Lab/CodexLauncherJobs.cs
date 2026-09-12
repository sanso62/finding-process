using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FindingProcess.Lab;

internal static class CodexLauncherJobs
{
    internal static int Preserve(Process launcher, Process target)
    {
        // libuv keeps a KILL_ON_JOB_CLOSE job in the npm launcher. Retain those
        // exact jobs inside Codex before ending the launcher; do not alter job limits.
        using var reference = CodexNative.CreateJobObjectW(0, null);
        Native.Check(!reference.IsInvalid, "Identify Windows job handle type");
        using var self = Process.GetCurrentProcess();
        var jobType = Snapshot(self).Single(h => h.Handle == reference.DangerousGetHandle()).Type;
        var localJobs = new List<nint>();
        var retained = new List<nint>();
        var completed = false;
        try
        {
            foreach (var item in Snapshot(launcher).Where(h => h.Type == jobType))
            {
                Native.Check(CodexNative.DuplicateHandle(launcher.Handle, item.Handle, -1, out var job, 0, false, 2), "Preserve launcher job locally");
                localJobs.Add(job);
                Native.Check(CodexNative.IsProcessInJob(target.SafeHandle, job, out var member), "Verify Codex job membership");
                if (!member) continue;
                var limits = Marshal.AllocHGlobal(144);
                uint flags;
                try
                {
                    Native.Check(CodexNative.QueryInformationJobObject(job, 9, limits, 144, out _), "Read launcher job limits");
                    flags = (uint)Marshal.ReadInt32(limits, 16);
                }
                finally { Marshal.FreeHGlobal(limits); }
                if ((flags & 0x2000) == 0) continue; // Only jobs that kill members on final handle close.
                Native.Check(CodexNative.DuplicateHandle(-1, job, target.Handle, out var remote, 0, false, 2), "Retain launcher job inside Codex");
                retained.Add(remote);
                // Verify the retained handle before allowing the launcher to exit.
                Native.Check(CodexNative.DuplicateHandle(target.Handle, remote, -1, out var check, 0, false, 2), "Verify retained Codex job handle");
                try
                {
                    Native.Check(CodexNative.IsProcessInJob(target.SafeHandle, check, out member), "Verify retained Codex job membership");
                    if (!member) throw new IOException("Retained job does not contain the selected Codex.");
                }
                finally { Native.CloseHandle(check); }
            }
            if (retained.Count == 0) throw new IOException("Cannot verify the npm launcher's Codex lifetime job; launcher was not terminated.");
            completed = true;
            return retained.Count;
        }
        finally
        {
            if (!completed)
                foreach (var remote in retained)
                    if (CodexNative.DuplicateHandle(target.Handle, remote, -1, out var copy, 0, false, 3)) Native.CloseHandle(copy);
            foreach (var job in localJobs) Native.CloseHandle(job);
        }
    }

    private static List<(nint Handle, int Type)> Snapshot(Process process)
    {
        const int size = 1024 * 1024;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            CodexNative.CheckStatus(CodexNative.NtQueryInformationProcess(process.Handle, 51, buffer, size, out _), "Inspect launcher job handles");
            var count = Marshal.ReadInt64(buffer);
            if (count < 0 || count > (size - 16) / 40) throw new IOException("Invalid launcher handle snapshot.");
            var result = new List<(nint, int)>();
            for (var i = 0; i < count; i++) result.Add((Marshal.ReadIntPtr(buffer, 16 + i * 40), Marshal.ReadInt32(buffer, 16 + i * 40 + 28)));
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
