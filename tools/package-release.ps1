[CmdletBinding()]
param(
    [string]$InputDirectory = 'dist',
    [string]$OutputDirectory = 'artifacts',
    [string]$Version = '1.0.4'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$inputRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $InputDirectory))
$outputRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
$projectPrefix = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\') + '\'

if (-not $inputRoot.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "InputDirectory must stay inside the repository: $inputRoot"
}
if (-not $outputRoot.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must stay inside the repository: $outputRoot"
}
if (-not (Test-Path -LiteralPath $inputRoot)) {
    throw "Build output does not exist: $inputRoot"
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-.][0-9A-Za-z.-]+)?$') {
    throw "Invalid version: $Version"
}

$requiredFiles = @(
    'PeripheralBatteryDashboard.exe',
    'PeripheralBatteryDashboard.exe.config',
    'PeripheralBatteryDashboard.Diagnostics.exe',
    'PeripheralBatteryDashboard.Diagnostics.exe.config',
    'PeripheralBatteryDashboard.Runtime.dll',
    'README.md',
    'AGENTS.md',
    'CODEX-PROMPTS.md',
    'DEVICE-ADDING.md',
    'THIRD-PARTY-NOTICES.md',
    'LICENSE',
    'Profiles\builtin.devices.json'
)
foreach ($name in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $inputRoot $name))) {
        throw "Required release file is missing: $name"
    }
}

$diagnostics = Join-Path $inputRoot 'PeripheralBatteryDashboard.Diagnostics.exe'
& $diagnostics --self-test
if ($LASTEXITCODE -ne 0) {
    throw "Self-test failed with exit code $LASTEXITCODE"
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$zipName = "PeripheralBatteryDashboard-v$Version-win-x64.zip"
$zipPath = Join-Path $outputRoot $zipName
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

$stagingRoot = Join-Path $outputRoot (".package-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
try {
    Get-ChildItem -LiteralPath $inputRoot -Force | Where-Object { $_.Extension -ne '.pdb' } |
        Copy-Item -Destination $stagingRoot -Recurse -Force
    Compress-Archive -Path (Join-Path $stagingRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
$hashLine = "{0}  {1}" -f $hash.Hash, $zipName
$hashPath = Join-Path $outputRoot 'SHA256SUMS.txt'
[IO.File]::WriteAllText($hashPath, $hashLine + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))

Write-Host "Package: $zipPath"
Write-Host "SHA256: $($hash.Hash)"
