param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$projDir = $PSScriptRoot
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$ico = Join-Path $projDir "Kumo.ico"
$outExe = Join-Path $projDir "Kumo.exe"
$objDir = Join-Path $projDir "obj"

$rawVer = $Version
if ([string]::IsNullOrWhiteSpace($rawVer)) { $rawVer = $env:KUMO_VERSION }
if ([string]::IsNullOrWhiteSpace($rawVer)) { $rawVer = "1.1.0" }
$dispVer = $rawVer.Trim()
if ($dispVer.StartsWith("v") -or $dispVer.StartsWith("V")) {
    $dispVer = $dispVer.Substring(1).Trim()
}
if ([string]::IsNullOrWhiteSpace($dispVer)) { $dispVer = "1.1.0" }
$setupFileName = "KumoSetup-v$dispVer.exe"
$outSetup = Join-Path $projDir $setupFileName

$numMatches = [regex]::Matches($dispVer, '\d+')
$parts = @(0, 0, 0, 0)
if ($numMatches.Count -eq 0) {
    $parts = @(1, 1, 0, 0)
} else {
    for ($i = 0; $i -lt [Math]::Min(4, $numMatches.Count); $i++) {
        $val = [long]$numMatches[$i].Value
        $parts[$i] = [int]($val % 65535)
    }
}
$asmVer = "$($parts[0]).$($parts[1]).$($parts[2]).$($parts[3])"

if (-not (Test-Path $ico)) {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $projDir "GenerateLogo.ps1") -OutDir $projDir
}
if (-not (Test-Path $objDir)) {
    New-Item -ItemType Directory -Path $objDir | Out-Null
}

$buildInfoPath = Join-Path $objDir "BuildInfo.g.cs"
$safeDisp = $dispVer.Replace('\', '\\').Replace('"', '\"')
$buildInfoCode = @"
using System.Reflection;

[assembly: AssemblyVersion("$asmVer")]
[assembly: AssemblyFileVersion("$asmVer")]
[assembly: AssemblyInformationalVersion("$safeDisp")]

internal static class KumoBuildInfo
{
    public const string Version = "$safeDisp";
    public const string Tag = "v$safeDisp";
    public const string RepoSlug = "buiduchuy2010qn-prog/kumo";
}
"@
[System.IO.File]::WriteAllText($buildInfoPath, $buildInfoCode, (New-Object System.Text.UTF8Encoding($false)))

$sources = @(
    $buildInfoPath,
    (Join-Path $projDir "src\NativeMethods.cs"),
    (Join-Path $projDir "src\HardwareMonitor.cs"),
    (Join-Path $projDir "src\SystemOsdMonitor.cs"),
    (Join-Path $projDir "src\MediaLyricsManager.cs"),
    (Join-Path $projDir "src\UpdaterService.cs"),
    (Join-Path $projDir "src\AppState.cs"),
    (Join-Path $projDir "src\SettingsForm.cs"),
    (Join-Path $projDir "src\NotchForm.cs"),
    (Join-Path $projDir "src\Program.cs")
)

Write-Host "1/3 Compiling Kumo.exe (Dynamic v$dispVer | PE $asmVer x64)..."
$winrt = (Get-ChildItem "C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Runtime.WindowsRuntime\*\System.Runtime.WindowsRuntime.dll" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
if (-not $winrt) {
    $winrt = (Get-ChildItem "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETCore\v4.5\System.Runtime.WindowsRuntime.dll" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
}
$sysRuntime = (Get-ChildItem "C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Runtime\*\System.Runtime.dll" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
if (-not $sysRuntime) {
    $sysRuntime = (Get-ChildItem "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\Facades\System.Runtime.dll" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
}

$winmdArgs = @()
if (Test-Path "C:\Windows\System32\WinMetadata\Windows.Media.winmd") {
    $winmdArgs = @(
        "/r:C:\Windows\System32\WinMetadata\Windows.Media.winmd",
        "/r:C:\Windows\System32\WinMetadata\Windows.Foundation.winmd",
        "/r:C:\Windows\System32\WinMetadata\Windows.Storage.winmd"
    )
} else {
    $unionWinmd = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\UnionMetadata\*\Windows.winmd" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
    if ($unionWinmd) {
        $winmdArgs = @("/r:$($unionWinmd.FullName)")
    } else {
        throw "Could not locate Windows WinRT metadata (.winmd)"
    }
}

& $csc /target:winexe /platform:x64 /optimize+ "/win32icon:$ico" "/out:$outExe" `
    "/r:System.dll" "/r:System.Core.dll" "/r:System.Drawing.dll" "/r:System.Windows.Forms.dll" "/r:System.Management.dll" `
    "/r:$winrt" "/r:$sysRuntime" `
    $winmdArgs `
    $sources
if ($LASTEXITCODE -ne 0) {
    throw "Kumo.exe compilation failed with exit code $LASTEXITCODE"
}

Write-Host "2/3 Compressing Kumo payload for Official Setup..."
$payloadGz = Join-Path $objDir "Kumo.exe.gz"
$rawBytes = [System.IO.File]::ReadAllBytes($outExe)
$fsOut = New-Object System.IO.FileStream($payloadGz, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
$gz = New-Object System.IO.Compression.GZipStream($fsOut, [System.IO.Compression.CompressionLevel]::Optimal)
$gz.Write($rawBytes, 0, $rawBytes.Length)
$gz.Close()
$fsOut.Close()

Write-Host "3/3 Compiling Official Overwrite-Ready Installer $setupFileName..."
$setupSrc = Join-Path $projDir "installer\SetupProgram.cs"
& $csc /target:winexe /platform:x64 /optimize+ "/win32icon:$ico" "/resource:$payloadGz,Kumo.Payload.gz" "/out:$outSetup" `
    /r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll `
    $buildInfoPath $setupSrc
if ($LASTEXITCODE -ne 0) {
    throw "KumoSetup.exe compilation failed with exit code $LASTEXITCODE"
}

Write-Host "Compiled Kumo & KumoSetup v$dispVer successfully!"
Get-Item $outExe, $outSetup | Select-Object FullName, Length, LastWriteTime