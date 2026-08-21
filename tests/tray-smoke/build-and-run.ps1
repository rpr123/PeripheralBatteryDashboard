[CmdletBinding()]
param(
    [string]$BuildDirectory = 'dist',
    [string]$OutputDirectory = 'dist-tray-smoke'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = [IO.Path]::GetFullPath((Join-Path $testRoot '..\..'))
$buildRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $BuildDirectory))
$outputRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
$projectPrefix = $projectRoot.TrimEnd('\') + '\'
if (-not $buildRoot.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "BuildDirectory must stay inside the repository: $buildRoot"
}
if (-not $outputRoot.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must stay inside the repository: $outputRoot"
}
$runtime = Join-Path $buildRoot 'PeripheralBatteryDashboard.Runtime.dll'
$source = Join-Path $testRoot 'TrayRuntimeSmoke.cs'
$output = Join-Path $outputRoot 'TrayRuntimeSmoke.exe'

if (-not (Test-Path -LiteralPath $runtime)) {
    throw "Runtime DLL not found: $runtime"
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$compilerCandidates = @(
    'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe',
    'C:\Program Files (x86)\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe',
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) {
    throw 'No C# compiler was found.'
}

$references = @(
    $runtime,
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Drawing.dll',
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Windows.Forms.dll',
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Xaml.dll',
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll',
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll',
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationFramework.dll'
)

$arguments = @(
    '/nologo',
    '/target:exe',
    '/platform:x64',
    '/optimize+',
    '/utf8output',
    "/out:$output"
)
$arguments += $references | ForEach-Object { "/reference:$_" }
$arguments += $source

& $compiler @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Harness compilation failed with exit code $LASTEXITCODE"
}

& $output $runtime
if ($LASTEXITCODE -ne 0) {
    throw "Harness failed with exit code $LASTEXITCODE"
}
