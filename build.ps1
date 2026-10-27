$ErrorActionPreference = "Stop"
$projDir = $PSScriptRoot
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$ico = Join-Path $projDir "Kumo.ico"
$outExe = Join-Path $projDir "Kumo.exe"
$outSetup = Join-Path $projDir "KumoSetup.exe"
$objDir = Join-Path $projDir "obj"

if (-not (Test-Path $ico)) {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $projDir "GenerateLogo.ps1") -OutDir $projDir
}
if (-not (Test-Path $objDir)) {
    New-Item -ItemType Directory -Path $objDir | Out-Null
}

$sources = @(
    (Join-Path $projDir "src\NativeMethods.cs"),
    (Join-Path $projDir "src\HardwareMonitor.cs"),
    (Join-Path $projDir "src\MediaLyricsManager.cs"),
    (Join-Path $projDir "src\AppState.cs"),
    (Join-Path $projDir "src\SettingsForm.cs"),
    (Join-Path $projDir "src\NotchForm.cs"),
    (Join-Path $projDir "src\Program.cs")
)

Write-Host "1/3 Compiling Kumo.exe (Official v1.0.0 x64)..."
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

Write-Host "3/3 Compiling Official Installer KumoSetup.exe..."
$setupSrc = Join-Path $projDir "installer\SetupProgram.cs"
& $csc /target:winexe /platform:x64 /optimize+ "/win32icon:$ico" "/resource:$payloadGz,Kumo.Payload.gz" "/out:$outSetup" /r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll $setupSrc
if ($LASTEXITCODE -ne 0) {
    throw "KumoSetup.exe compilation failed with exit code $LASTEXITCODE"
}

Write-Host "Compiled Kumo & KumoSetup successfully!"
Get-Item $outExe, $outSetup | Select-Object FullName, Length, LastWriteTime