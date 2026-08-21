[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$NoClean,

    [string]$OutputDirectory = 'dist'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceRoot = Join-Path $projectRoot 'src'
$programSource = Join-Path $sourceRoot 'Program.cs'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    throw '출력 폴더 이름이 비어 있습니다.'
}
$distRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
}

function Find-RoslynCompiler {
    $candidates = New-Object System.Collections.Generic.List[string]

    if (${env:ProgramFiles(x86)}) {
        $vsRoot = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022'
        foreach ($edition in @('BuildTools', 'Community', 'Professional', 'Enterprise')) {
            $candidates.Add((Join-Path $vsRoot "$edition\MSBuild\Current\Bin\Roslyn\csc.exe"))
        }

        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            $found = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\Roslyn\csc.exe'
            foreach ($path in @($found)) {
                if (-not [string]::IsNullOrWhiteSpace($path)) {
                    $candidates.Insert(0, $path.Trim())
                }
            }
        }
    }

    foreach ($path in $candidates) {
        if (Test-Path -LiteralPath $path) {
            return (Resolve-Path -LiteralPath $path).Path
        }
    }

    throw 'VS 2022 Build Tools의 Roslyn csc.exe를 찾지 못했습니다. Desktop development with .NET 구성 요소를 설치하세요.'
}

function Find-FrameworkRoot {
    $candidates = New-Object System.Collections.Generic.List[string]

    if (${env:ProgramFiles(x86)}) {
        $referenceBase = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework'
        $candidates.Add((Join-Path $referenceBase 'v4.8'))
        $candidates.Add((Join-Path $referenceBase 'v4.8.1'))
    }

    $candidates.Add((Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'))
    $candidates.Add((Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'))

    foreach ($path in $candidates) {
        if ((Test-Path -LiteralPath (Join-Path $path 'mscorlib.dll')) -and
            (Test-Path -LiteralPath (Join-Path $path 'System.dll'))) {
            return (Resolve-Path -LiteralPath $path).Path
        }
    }

    throw '.NET Framework 4.x 참조 어셈블리를 찾지 못했습니다. .NET Framework 4.8 Developer Pack 또는 Runtime을 설치하세요.'
}

function Resolve-FrameworkAssembly {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$PrimaryRoot
    )

    $runtime64 = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $runtime32 = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $searchRoots = @(
        $PrimaryRoot,
        (Join-Path $PrimaryRoot 'WPF'),
        $runtime64,
        (Join-Path $runtime64 'WPF'),
        $runtime32,
        (Join-Path $runtime32 'WPF')
    ) | Select-Object -Unique

    foreach ($root in $searchRoots) {
        $candidate = Join-Path $root $Name
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "필요한 .NET Framework 어셈블리를 찾지 못했습니다: $Name"
}

function Invoke-CSharpCompiler {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Description,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Host "[build] $Description"
    & $script:CscPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description 컴파일 실패 (exit code $LASTEXITCODE)"
    }
}

if (-not (Test-Path -LiteralPath $programSource)) {
    throw "진입점 소스가 없습니다: $programSource"
}

$distFull = [IO.Path]::GetFullPath($distRoot)
$projectFull = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\') + '\'
if (-not $distFull.StartsWith($projectFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "안전하지 않은 출력 경로입니다: $distFull"
}

if ((Test-Path -LiteralPath $distRoot) -and -not $NoClean) {
    Remove-Item -LiteralPath $distRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $distRoot -Force | Out-Null

$script:CscPath = Find-RoslynCompiler
$frameworkRoot = Find-FrameworkRoot

$assemblyNames = @(
    'mscorlib.dll',
    'System.dll',
    'System.Core.dll',
    'System.Configuration.dll',
    'System.Data.dll',
    'System.Drawing.dll',
    'System.Runtime.dll',
    'System.Web.Extensions.dll',
    'System.Windows.Forms.dll',
    'System.Xaml.dll',
    'System.Xml.dll',
    'WindowsBase.dll',
    'PresentationCore.dll',
    'PresentationFramework.dll'
)
$frameworkReferences = @($assemblyNames | ForEach-Object { Resolve-FrameworkAssembly -Name $_ -PrimaryRoot $frameworkRoot })

$commonArguments = @(
    '/noconfig',
    '/nostdlib+',
    '/platform:x64',
    '/langversion:latest',
    '/utf8output',
    '/warn:4',
    '/deterministic+',
    '/define:TRACE'
)
if ($Configuration -eq 'Debug') {
    $commonArguments += @('/debug:full', '/optimize-')
}
else {
    $commonArguments += @('/debug-', '/optimize+')
}
$commonArguments += @($frameworkReferences | ForEach-Object { "/reference:$_" })

$runtimeName = 'PeripheralBatteryDashboard.Runtime.dll'
$runtimePath = Join-Path $distRoot $runtimeName
$runtimePdb = [IO.Path]::ChangeExtension($runtimePath, '.pdb')
$runtimeSources = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -File -Recurse |
    Where-Object { -not [string]::Equals($_.FullName, $programSource, [StringComparison]::OrdinalIgnoreCase) } |
    Sort-Object FullName |
    ForEach-Object { $_.FullName })
if ($runtimeSources.Count -eq 0) {
    throw '공용 런타임으로 컴파일할 C# 소스가 없습니다.'
}

$runtimeArguments = @($commonArguments) + @(
    '/target:library',
    "/out:$runtimePath"
) + $runtimeSources
if ($Configuration -eq 'Debug') {
    $runtimeArguments += "/pdb:$runtimePdb"
}
Invoke-CSharpCompiler -Description $runtimeName -Arguments $runtimeArguments

$runtimeReference = "/reference:$runtimePath"
$guiPath = Join-Path $distRoot 'PeripheralBatteryDashboard.exe'
$guiPdb = [IO.Path]::ChangeExtension($guiPath, '.pdb')
$guiArguments = @($commonArguments) + @(
    $runtimeReference,
    '/target:winexe',
    '/main:PeripheralBatteryDashboard.Program',
    "/out:$guiPath",
    $programSource
)
if ($Configuration -eq 'Debug') {
    $guiArguments += "/pdb:$guiPdb"
}
$manifestPath = Join-Path $projectRoot 'app.manifest'
if (Test-Path -LiteralPath $manifestPath) {
    $guiArguments += "/win32manifest:$manifestPath"
}
$iconPath = Join-Path $projectRoot 'app.ico'
if (Test-Path -LiteralPath $iconPath) {
    $guiArguments += "/win32icon:$iconPath"
}
Invoke-CSharpCompiler -Description 'PeripheralBatteryDashboard.exe' -Arguments $guiArguments

$diagnosticsPath = Join-Path $distRoot 'PeripheralBatteryDashboard.Diagnostics.exe'
$diagnosticsPdb = [IO.Path]::ChangeExtension($diagnosticsPath, '.pdb')
$diagnosticsArguments = @($commonArguments) + @(
    $runtimeReference,
    '/target:exe',
    '/main:PeripheralBatteryDashboard.Program',
    "/out:$diagnosticsPath",
    $programSource
)
if ($Configuration -eq 'Debug') {
    $diagnosticsArguments += "/pdb:$diagnosticsPdb"
}
if (Test-Path -LiteralPath $manifestPath) {
    $diagnosticsArguments += "/win32manifest:$manifestPath"
}
Invoke-CSharpCompiler -Description 'PeripheralBatteryDashboard.Diagnostics.exe' -Arguments $diagnosticsArguments

$configSource = Join-Path $projectRoot 'app.config'
if (-not (Test-Path -LiteralPath $configSource)) {
    throw "앱 구성 파일이 없습니다: $configSource"
}
Copy-Item -LiteralPath $configSource -Destination ($guiPath + '.config') -Force
Copy-Item -LiteralPath $configSource -Destination ($diagnosticsPath + '.config') -Force

foreach ($directoryName in @('Profiles', 'Plugins', 'docs')) {
    $source = Join-Path $projectRoot $directoryName
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $distRoot $directoryName) -Recurse -Force
    }
}

foreach ($documentName in @('README.md', 'AGENTS.md', 'CODEX-PROMPTS.md', 'DEVICE-ADDING.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE')) {
    $source = Join-Path $projectRoot $documentName
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $distRoot $documentName) -Force
    }
}

# Keep packaged source-derived text byte-stable across Git checkout settings.
# Git stores these files with LF endings, while a Windows working tree may use CRLF.
$utf8NoBom = New-Object Text.UTF8Encoding($false)
Get-ChildItem -LiteralPath $distRoot -Recurse -File | Where-Object {
    $_.Extension -in @('.config', '.json', '.md', '.txt') -or $_.Name -eq 'LICENSE'
} | ForEach-Object {
    $text = [IO.File]::ReadAllText($_.FullName)
    $normalized = $text.Replace("`r`n", "`n").Replace("`r", "`n")
    [IO.File]::WriteAllText($_.FullName, $normalized, $utf8NoBom)
}

Write-Host ''
Write-Host "빌드 완료: $distRoot"
Get-ChildItem -LiteralPath $distRoot -File | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0,-52} {1,10:N0} bytes" -f $_.Name, $_.Length)
}
