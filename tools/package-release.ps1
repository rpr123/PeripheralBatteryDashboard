[CmdletBinding()]
param(
    [string]$InputDirectory = 'dist',
    [string]$OutputDirectory = 'artifacts',
    [string]$Version = '1.1.2'
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
    'Plugins\README.md',
    'Plugins\SamplePlugin.cs.txt',
    'Profiles\builtin.devices.json',
    'docs\images\dashboard-overview.png',
    'docs\images\tray-device-icons.png'
)
foreach ($name in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $inputRoot $name))) {
        throw "Required release file is missing: $name"
    }
}

$builtInProfilePath = Join-Path $inputRoot 'Profiles\builtin.devices.json'
$builtInProfileDocument = Get-Content -LiteralPath $builtInProfilePath -Raw | ConvertFrom-Json
if ($builtInProfileDocument.SchemaVersion -ne 1 -or
    @($builtInProfileDocument.Profiles).Count -ne 0) {
    throw 'Public releases must contain a SchemaVersion 1 built-in profile document with zero active profiles.'
}
$shippedPluginProfiles = @(Get-ChildItem -LiteralPath (Join-Path $inputRoot 'Plugins') `
    -Filter '*.devices.json' -File -Recurse)
if ($shippedPluginProfiles.Count -ne 0) {
    throw 'Public releases must not auto-register device profiles from Plugins.'
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
    foreach ($name in $requiredFiles) {
        $sourcePath = Join-Path $inputRoot $name
        $destinationPath = Join-Path $stagingRoot $name
        $destinationParent = Split-Path -Parent $destinationPath
        if (-not (Test-Path -LiteralPath $destinationParent)) {
            New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
        }
        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
    }
    Compress-Archive -Path (Join-Path $stagingRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$expectedEntries = @($requiredFiles | ForEach-Object { $_.Replace('\', '/') } | Sort-Object)
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $actualEntries = @($archive.Entries |
        Where-Object { -not [string]::IsNullOrEmpty($_.Name) } |
        ForEach-Object { $_.FullName.Replace('\', '/') } |
        Sort-Object)
    if (($actualEntries.Count -ne $expectedEntries.Count) -or
        (Compare-Object -ReferenceObject $expectedEntries -DifferenceObject $actualEntries)) {
        throw 'Release ZIP contents do not match the exact public allowlist.'
    }
}
finally {
    $archive.Dispose()
}

$verificationRoot = Join-Path $outputRoot (".verify-" + [Guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $zipPath -DestinationPath $verificationRoot -Force
    foreach ($name in $requiredFiles) {
        $sourceHash = (Get-FileHash -LiteralPath (Join-Path $inputRoot $name) -Algorithm SHA256).Hash
        $extractedHash = (Get-FileHash -LiteralPath (Join-Path $verificationRoot $name) -Algorithm SHA256).Hash
        if (-not [string]::Equals($sourceHash, $extractedHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Release ZIP verification hash mismatch: $name"
        }
    }

    $verifiedDiagnostics = Join-Path $verificationRoot 'PeripheralBatteryDashboard.Diagnostics.exe'
    & $verifiedDiagnostics --self-test
    if ($LASTEXITCODE -ne 0) {
        throw "Extracted release self-test failed with exit code $LASTEXITCODE"
    }
}
finally {
    if (Test-Path -LiteralPath $verificationRoot) {
        Remove-Item -LiteralPath $verificationRoot -Recurse -Force
    }
}

$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
$hashLine = "{0}  {1}" -f $hash.Hash, $zipName
$hashPath = Join-Path $outputRoot 'SHA256SUMS.txt'
[IO.File]::WriteAllText($hashPath, $hashLine + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))

Write-Host "Package: $zipPath"
Write-Host "SHA256: $($hash.Hash)"
