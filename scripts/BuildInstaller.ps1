#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InnoSetupCompiler,
    [Parameter(Mandatory)]
    [string]$SignTool,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$projectDirectory = Join-Path $repository 'src\Snap.Hutao.Remastered\Snap.Hutao.Remastered'
$projectPath = Join-Path $projectDirectory 'Snap.Hutao.Remastered.csproj'
$InnoSetupCompiler = (Resolve-Path -LiteralPath $InnoSetupCompiler).Path
$SignTool = (Resolve-Path -LiteralPath $SignTool).Path
if (!(Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'SnapHutaoRemastered\LocalSigning\identity.json'))) {
    throw 'No local signing identity. Follow docs/local-release.md to create one before building a signed installer.'
}
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repository 'artifacts\installer' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

Push-Location $repository
try {
    # A fresh output directory prevents stale assemblies/resources entering the installer.
    $payload = Join-Path $repository ('artifacts\installer-payload-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    & dotnet publish $projectPath -c Release -p:Platform=x64 -p:PublishProfile=FolderProfile "-p:PublishDir=$payload\"
    if ($LASTEXITCODE -ne 0) { throw "Release publish failed (exit $LASTEXITCODE)." }

    $project = [xml](Get-Content -LiteralPath $projectPath -Raw)
    $framework = [string]($project.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)
    $runtime = [string]($project.Project.PropertyGroup.RuntimeIdentifier | Where-Object { $_ } | Select-Object -First 1)
    $buildOutput = Join-Path $projectDirectory "bin\x64\Release\$framework\$runtime"
    Copy-Item -LiteralPath (Join-Path $buildOutput 'AppxManifest.xml') -Destination $payload -Force
    foreach ($directory in @('Assets', 'Resource')) {
        Copy-Item -LiteralPath (Join-Path $projectDirectory $directory) -Destination $payload -Recurse -Force
    }

    & (Join-Path $PSScriptRoot 'SignLocalRelease.ps1') `
        -PayloadDirectory $payload `
        -InnoSetupCompiler $InnoSetupCompiler `
        -SignTool $SignTool `
        -OutputDirectory $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Signed installer build failed (exit $LASTEXITCODE)." }
} finally {
    Pop-Location
}
