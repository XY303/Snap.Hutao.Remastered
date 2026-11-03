#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$FullAnalysis,
    [switch]$ReadyToRun,
    [switch]$NoRestore,
    [ValidateRange(1, 256)]
    [int]$MaxCpuCount = [Environment]::ProcessorCount
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repository 'src\Snap.Hutao.Remastered\Snap.Hutao.Remastered\Snap.Hutao.Remastered.csproj'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = $null
if (Test-Path -LiteralPath $vswhere) {
    $msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
}

$buildArguments = @(
    $project, '/t:Build', "/p:Configuration=$Configuration", '/p:Platform=x64',
    "/m:$MaxCpuCount", '/nr:true', '/v:minimal', '/nologo', '/clp:PerformanceSummary'
)
if (!$NoRestore) { $buildArguments += '/restore' }
if ($FullAnalysis) { $buildArguments += '/p:FullAnalysis=true' }
if ($ReadyToRun) { $buildArguments += '/p:ReadyToRunOnBuild=true' }
if (!$ReadyToRun -and !$FullAnalysis) { $buildArguments += '/p:PublishReadyToRun=false' }

$timer = [Diagnostics.Stopwatch]::StartNew()
if ($msbuild) {
    & $msbuild @buildArguments
} else {
    & dotnet msbuild @buildArguments
}
$buildExit = $LASTEXITCODE
$timer.Stop()
Write-Host ("Build finished in {0:N1}s (exit {1})." -f $timer.Elapsed.TotalSeconds, $buildExit)
exit $buildExit
