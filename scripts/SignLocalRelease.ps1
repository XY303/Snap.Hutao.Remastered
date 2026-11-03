#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PayloadDirectory,
    [Parameter(Mandatory)]
    [string]$InnoSetupCompiler,
    [Parameter(Mandatory)]
    [string]$SignTool,
    [string]$OutputDirectory,
    [switch]$CreateCertificate
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$identityDirectory = Join-Path $env:LOCALAPPDATA 'SnapHutaoRemastered\LocalSigning'
$identityPath = Join-Path $identityDirectory 'identity.json'
$projectDirectory = Join-Path $repository 'src\Snap.Hutao.Remastered\Snap.Hutao.Remastered'
$manifest = [xml](Get-Content (Join-Path $projectDirectory 'Package.appxmanifest') -Raw)
$version = [string]$manifest.Package.Identity.Version
$PayloadDirectory = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$InnoSetupCompiler = (Resolve-Path -LiteralPath $InnoSetupCompiler).Path
$SignTool = (Resolve-Path -LiteralPath $SignTool).Path
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $repository "artifacts\local-signed-$version"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$payloadPrefix = $PayloadDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if ($OutputDirectory.Equals($PayloadDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $OutputDirectory.StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The output directory must be outside the source payload directory.'
}
$requiredFiles = @(
    'Snap.Hutao.Remastered.exe', 'Snap.Hutao.Remastered.dll', 'AppxManifest.xml',
    'resources.pri', 'coreclr.dll', 'hostfxr.dll', 'Microsoft.UI.Xaml.dll',
    'Snap.Hutao.Remastered.Native.dll', 'Snap.HPatch.dll', 'YaeAchievementLib.dll',
    'Snap.Hutao.Remastered.Deployment.exe', 'Assets\Logo.ico'
)
foreach ($file in $requiredFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $PayloadDirectory $file))) {
        throw "Incomplete self-contained payload: missing $file"
    }
}
$payloadManifest = [xml](Get-Content (Join-Path $PayloadDirectory 'AppxManifest.xml') -Raw)
if ([string]$payloadManifest.Package.Identity.Version -ne $version) {
    throw 'The payload and source manifest versions do not match.'
}

# Keep keys in the current user's Windows key store, outside the repository.
# Neither certificate is added to a trust store on the build machine.
if (!(Test-Path -LiteralPath $identityPath)) {
    if (!$CreateCertificate) {
        throw 'No local signing identity exists. Use -CreateCertificate once to create it.'
    }
    New-Item -ItemType Directory -Force -Path $identityDirectory | Out-Null
    $common = @{
        Type = 'Custom'
        CertStoreLocation = 'Cert:\CurrentUser\My'
        KeyAlgorithm = 'RSA'
        KeyLength = 3072
        HashAlgorithm = 'SHA256'
        KeyExportPolicy = 'NonExportable'
        Provider = 'Microsoft Software Key Storage Provider'
        NotBefore = (Get-Date).AddMinutes(-10)
    }
    $root = New-SelfSignedCertificate @common `
        -Subject 'CN=XY303 Snap.Hutao Local Code Signing Root' `
        -FriendlyName 'Snap.Hutao local code signing root (XY303)' `
        -KeyUsage CertSign, CRLSign, DigitalSignature `
        -TextExtension @('2.5.29.19={critical}{text}ca=true&pathlength=0', '2.5.29.37={text}1.3.6.1.5.5.7.3.3') `
        -NotAfter (Get-Date).AddYears(10)
    $leaf = New-SelfSignedCertificate @common `
        -Subject 'CN=XY303 Snap.Hutao Remastered Local Signing' `
        -FriendlyName 'Snap.Hutao local release signing (XY303)' `
        -Signer $root -KeyUsage DigitalSignature `
        -TextExtension @('2.5.29.19={critical}{text}ca=false', '2.5.29.37={text}1.3.6.1.5.5.7.3.3') `
        -NotAfter (Get-Date).AddYears(3)
    @{
        RootThumbprint = $root.Thumbprint
        SigningThumbprint = $leaf.Thumbprint
    } | ConvertTo-Json | Set-Content -LiteralPath $identityPath -Encoding utf8
}
$identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
foreach ($thumbprint in @($identity.RootThumbprint, $identity.SigningThumbprint)) {
    if ($thumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'Invalid local certificate thumbprint.' }
}
$root = Get-Item "Cert:\CurrentUser\My\$($identity.RootThumbprint)"
$leaf = Get-Item "Cert:\CurrentUser\My\$($identity.SigningThumbprint)"
if (!$leaf.HasPrivateKey) { throw 'The signing certificate private key is unavailable.' }
if ($root.Subject -ne 'CN=XY303 Snap.Hutao Local Code Signing Root' -or
    $leaf.Subject -ne 'CN=XY303 Snap.Hutao Remastered Local Signing') {
    throw 'The saved identity does not identify this local publisher.'
}
$rootConstraints = $root.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension] }
$leafConstraints = $leaf.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension] }
if (!$rootConstraints.CertificateAuthority -or !$rootConstraints.HasPathLengthConstraint -or
    $rootConstraints.PathLengthConstraint -ne 0 -or $leafConstraints.CertificateAuthority) {
    throw 'Expected a root CA limited to leaf certificates and a non-CA signing certificate.'
}
foreach ($certificate in @($root, $leaf)) {
    $eku = $certificate.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] }
    if (@($eku.EnhancedKeyUsages).Count -ne 1 -or $eku.EnhancedKeyUsages[0].Value -ne '1.3.6.1.5.5.7.3.3') {
        throw 'The local certificates must be restricted to code signing.'
    }
}
$chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
try {
    $chain.ChainPolicy.TrustMode = [Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust
    [void]$chain.ChainPolicy.CustomTrustStore.Add($root)
    $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
    [void]$chain.ChainPolicy.ApplicationPolicy.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
    if (!$chain.Build($leaf) -or $chain.ChainElements.Count -ne 2 -or
        $chain.ChainElements[1].Certificate.Thumbprint -ne $root.Thumbprint) {
        throw 'The signing certificate does not form a valid code-signing chain to the bundled root.'
    }
} finally {
    $chain.Dispose()
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
# A fresh staging directory prevents old files from leaking into later releases.
$stage = Join-Path $repository ('artifacts\local-signing-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -Path (Join-Path $PayloadDirectory '*') -Destination $stage -Recurse
$rootPath = Join-Path $OutputDirectory 'XY303-SnapHutao-LocalRoot.cer'
[IO.File]::WriteAllBytes($rootPath, $root.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
$leafPath = Join-Path $OutputDirectory 'XY303-SnapHutao-CodeSigning.cer'
[IO.File]::WriteAllBytes($leafPath, $leaf.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))

function Assert-SignedByLocalPublisher([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.SignerCertificate.Thumbprint -ne $leaf.Thumbprint -or
        !$signature.TimeStamperCertificate -or
        $signature.Status -in @('HashMismatch', 'NotSigned', 'NotSupportedFileFormat', 'Incompatible')) {
        throw "Missing or invalid timestamped local signature: $Path ($($signature.Status))"
    }
    Write-Host "Timestamped signature: $Path (Windows trust status: $($signature.Status))"
}

foreach ($file in @('Snap.Hutao.Remastered.exe', 'Snap.Hutao.Remastered.dll')) {
    $path = Join-Path $stage $file
    & $SignTool sign /sha1 $leaf.Thumbprint /s My /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $path" }
    Assert-SignedByLocalPublisher $path
}

# Inno's $q/$f placeholders quote the executable and the file being signed.
$signCommand = '$q' + $SignTool + '$q sign /sha1 ' + $leaf.Thumbprint + ' /s My /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $f'
$compilerArgs = @(
    '/Qp', "/DMyAppVersion=$version", '/DMyAppPublisher=XY303',
    "/DPublishDir=$stage", "/O$OutputDirectory",
    "/DRootCertificatePath=$rootPath", "/DRootCertificateThumbprint=$($root.Thumbprint)",
    '/DSignToolName=LocalSign', "/SLocalSign=$signCommand",
    (Join-Path $repository 'Installer\installer.iss')
)
& $InnoSetupCompiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Signed installer compilation failed.' }
$installer = Join-Path $OutputDirectory "Snap.Hutao.Remastered-$version-Setup.exe"
Assert-SignedByLocalPublisher $installer

$zipPath = Join-Path $OutputDirectory "Snap.Hutao.Remastered-$version-win-x64.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force
$files = @($installer, $zipPath, $rootPath, $leafPath)
$files | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($_))"
} | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8
Write-Host "Release: $OutputDirectory"
Write-Host "Root: $($root.Thumbprint); signer: $($leaf.Thumbprint)"
Write-Host 'The installer establishes root trust during installation; its first launch still requires user approval.'
Write-Host "Private keys remain non-exportable in Cert:\CurrentUser\My; identity metadata: $identityPath"
