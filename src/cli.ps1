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
& (Join-Path $buildDirectory 'FindingProcess.Lab.exe') @args
exit $LASTEXITCODE
