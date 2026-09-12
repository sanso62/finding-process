$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$sources = @(Get-ChildItem "$PSScriptRoot/FindingProcess.Lab" -File | Where-Object { $_.Extension -in '.cs', '.csproj' })
$sources += Get-Item "$projectRoot/Directory.Build.props"
$fingerprint = ($sources | Sort-Object FullName | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join ''
$sha = [Security.Cryptography.SHA256]::Create()
try { $cacheKey = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($fingerprint)))).Replace('-', '').Substring(0, 20) }
finally { $sha.Dispose() }
$buildDirectory = Join-Path $projectRoot "artifacts/cli/$cacheKey"
[IO.Directory]::CreateDirectory($buildDirectory) | Out-Null
$buildLock = $null
try {
    while ($null -eq $buildLock) {
        try { $buildLock = [IO.File]::Open((Join-Path $buildDirectory 'build.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
        catch [IO.IOException] { Start-Sleep -Milliseconds 100 }
    }
    $marker = Join-Path $buildDirectory 'build.complete'
    if (!(Test-Path $marker)) {
        & dotnet build "$PSScriptRoot/FindingProcess.Lab/FindingProcess.Lab.csproj" -c Release -o $buildDirectory --nologo -v quiet | ForEach-Object { [Console]::Error.WriteLine($_) }
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        [IO.File]::WriteAllText($marker, $cacheKey)
    }
}
finally { if ($null -ne $buildLock) { $buildLock.Dispose() } }
$waitFile = $null
$terminal = $null
$previousWaitFile = $env:FINDING_PROCESS_WAIT_FILE
$waitingProcesses = @()
$commandStatus = 1
try {
    if ($args.Count -eq 0 -and ![Console]::IsInputRedirected -and ![Console]::IsOutputRedirected) {
        # Keep the normal shell waiting without a resident handoff helper or input relay.
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public sealed class FindingProcessTerminal : IDisposable {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetConsoleMode(SafeFileHandle handle, out uint mode);
    [DllImport("kernel32.dll")]
    static extern bool SetConsoleMode(SafeFileHandle handle, uint mode);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint GetConsoleProcessList([Out] uint[] items, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool WriteConsoleW(SafeFileHandle handle, string value, uint length, out uint written, IntPtr reserved);
    readonly SafeFileHandle input, output;
    readonly uint inputMode, outputMode;
    public bool Transferred;
    public FindingProcessTerminal() {
        input = CreateFileW("CONIN$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        output = CreateFileW("CONOUT$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (!GetConsoleMode(input, out inputMode) || !GetConsoleMode(output, out outputMode)) {
            int error = Marshal.GetLastWin32Error(); input.Dispose(); output.Dispose();
            throw new Win32Exception(error);
        }
    }
    public bool Contains(int pid) {
        var items = new uint[16];
        for (int attempt = 0; attempt < 4; attempt++) {
            uint count = GetConsoleProcessList(items, (uint)items.Length);
            if (count == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (count <= items.Length) return Array.IndexOf(items, (uint)pid, 0, (int)count) >= 0;
            items = new uint[count];
        }
        throw new InvalidOperationException("Console process list kept changing.");
    }
    public void Dispose() {
        if (Transferred) {
            const string reset = "\x1b[?1049l\x1b[?2004l\x1b[?1004l\x1b[0m\x1b[?25h";
            uint written;
            SetConsoleMode(output, outputMode | 4);
            WriteConsoleW(output, reset, (uint)reset.Length, out written, IntPtr.Zero);
        }
        SetConsoleMode(input, inputMode); SetConsoleMode(output, outputMode);
        input.Dispose(); output.Dispose();
    }
}
'@
        $terminal = New-Object FindingProcessTerminal
        $waitFile = Join-Path $buildDirectory ('wait-' + [Guid]::NewGuid().ToString('N') + '.json')
        $waitOwner = [Diagnostics.Process]::GetCurrentProcess()
        try {
            [IO.File]::WriteAllText($waitFile + '.owner.json', (@{
                Pid = $PID; StartedUtcTicks = $waitOwner.StartTime.ToUniversalTime().Ticks
            } | ConvertTo-Json))
        }
        finally { $waitOwner.Dispose() }
        $env:FINDING_PROCESS_WAIT_FILE = $waitFile
    }
    else { Remove-Item Env:FINDING_PROCESS_WAIT_FILE -ErrorAction SilentlyContinue }
    if ($null -ne $terminal) {
        # PowerShell's native-command screen bookkeeping races with Codex's redraw.
        # Start directly in the inherited console, without that output processing.
        $pickerStart = New-Object Diagnostics.ProcessStartInfo
        $pickerStart.FileName = Join-Path $buildDirectory 'FindingProcess.Lab.exe'
        $pickerStart.UseShellExecute = $false
        $pickerProcess = [Diagnostics.Process]::Start($pickerStart)
        try { $pickerProcess.WaitForExit(); $commandStatus = $pickerProcess.ExitCode }
        finally { $pickerProcess.Dispose() }
    }
    else {
        & (Join-Path $buildDirectory 'FindingProcess.Lab.exe') @args
        $commandStatus = $LASTEXITCODE
    }
    if ($null -ne $waitFile -and (Test-Path -LiteralPath $waitFile)) {
        $waitIdentities = Get-Content -LiteralPath $waitFile -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($identity in $waitIdentities) {
            $attachedProcess = $null
            try {
                $attachedProcess = [Diagnostics.Process]::GetProcessById([int]$identity.Pid)
                $null = $attachedProcess.Handle # Pin the identity before checking and waiting.
                if (!$attachedProcess.HasExited -and
                    $attachedProcess.StartTime.ToUniversalTime().Ticks -eq [long]$identity.StartedUtcTicks -and
                    $attachedProcess.MainModule.FileName -eq $identity.ImagePath -and
                    $terminal.Contains($attachedProcess.Id)) {
                    $waitingProcesses += $attachedProcess
                    $attachedProcess = $null
                    $terminal.Transferred = $true
                }
            }
            catch [ArgumentException] { } # The process may have already exited.
            finally { if ($null -ne $attachedProcess) { $attachedProcess.Dispose() } }
        }
        foreach ($attachedProcess in $waitingProcesses) {
            # A successful later handoff releases this old terminal's command as well.
            while (!$attachedProcess.WaitForExit(250)) {
                if (Test-Path -LiteralPath ($waitFile + '.moved')) { break }
            }
        }
    }
}
finally {
    foreach ($attachedProcess in $waitingProcesses) { $attachedProcess.Dispose() }
    if ($null -ne $terminal) { $terminal.Dispose() }
    if ($null -ne $waitFile) {
        Remove-Item -LiteralPath $waitFile,($waitFile + '.owner.json'),($waitFile + '.moved') -ErrorAction SilentlyContinue
    }
    $env:FINDING_PROCESS_WAIT_FILE = $previousWaitFile
}
exit $commandStatus
