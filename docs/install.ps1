# Kumo Official One-Line Installer & Updater for Windows 10 / 11 (x64)
# Usage: irm https://buiduchuy2010qn-prog.github.io/kumo/install.ps1 | iex

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

Write-Host ""
Write-Host "  Kumo - Pure Win32 Living Notch for Windows 10 & 11" -ForegroundColor Cyan
Write-Host "  Checking latest official release from GitHub..." -ForegroundColor Gray

$apiUrl = 'https://api.github.com/repos/buiduchuy2010qn-prog/kumo/releases/latest'
$headers = @{ 'User-Agent' = 'Kumo-PowerShell-Installer' }
$rel = Invoke-RestMethod -Uri $apiUrl -Headers $headers

$asset = $rel.assets | Where-Object { $_.name -like 'KumoSetup*.exe' } | Select-Object -First 1
if (-not $asset) {
    $asset = $rel.assets | Where-Object { $_.name -like '*.exe' } | Select-Object -First 1
}
if (-not $asset) {
    throw "Could not find KumoSetup.exe in release $($rel.tag_name)."
}

$kb = [Math]::Round($asset.size / 1024)
$outPath = Join-Path $env:TEMP $asset.name

Write-Host "  Downloading $($asset.name) ($kb KB)..." -ForegroundColor White
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $outPath -UseBasicParsing

Write-Host "  Installing $($rel.tag_name) (zero admin, keeps existing settings)..." -ForegroundColor Gray
$proc = Start-Process -FilePath $outPath -ArgumentList @('--silent', '--relaunch') -Wait -PassThru
Remove-Item -Path $outPath -Force -ErrorAction SilentlyContinue

if ($proc.ExitCode -eq 0) {
    Write-Host "  [OK] Kumo $($rel.tag_name) installed and launched at the top of your screen!" -ForegroundColor Green
    Write-Host ""
} else {
    throw "KumoSetup exited with code $($proc.ExitCode)."
}
