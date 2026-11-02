param(
    [version]$Version = '0.2.0.0',
    [version]$UpgradeVersion = '0.2.1.0',
    [string]$OutputDirectory = '',
    [string]$SigningKey = '',
    [switch]$RequireSigningKey,
    [switch]$Beta,
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
foreach ($packageVersion in @($Version, $UpgradeVersion)) {
    if ($packageVersion.Build -lt 0 -or $packageVersion.Revision -lt 0 -or
        $packageVersion.Major -gt 65535 -or $packageVersion.Minor -gt 65535 -or
        $packageVersion.Build -gt 65535 -or $packageVersion.Revision -gt 65535) {
        throw 'MSIX versions must contain four integers between 0 and 65535.'
    }
}
if ($Version -ge $UpgradeVersion) { throw 'UpgradeVersion must be greater than Version.' }
if (($RequireSigningKey -or $env:GITHUB_ACTIONS -eq 'true') -and !$SigningKey) {
    throw 'A persistent signing key is required; CI must not generate a replacement certificate.'
}
$signingPassword = if ($env:MANI_SIGNING_PASSWORD) { $env:MANI_SIGNING_PASSWORD } else { '' }
$packageName = if ($Beta) { 'Mani.Beta.Test' } else { 'Mani.Internal' }
# Keep the existing package identity for upgrades; only the visible product name changes.
$displayName = if ($Beta) { 'Mani' } else { 'Mani Internal' }
# Reuse the tracked self-contained profile. Global MSBuild properties override
# its diagnostic opt-in; no ignored, machine-local publish profile is required.
$publishProfile = 'M2-Internal'
$diagnosticsProperty = if ($Beta) { '-p:EnableDiagnostics=false' } else { '-p:EnableDiagnostics=true' }
function Read-SigningCertificate([string]$Path, [string]$Password) {
    $loaded = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile($Path, $Password,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    try {
        if ($loaded.Subject -ne 'CN=Mani Internal Test' -or !$loaded.HasPrivateKey) {
            throw 'The signing key must contain the private key for CN=Mani Internal Test.'
        }
        if ($loaded.NotBefore -gt (Get-Date) -or $loaded.NotAfter -le (Get-Date)) { throw 'The signing certificate is not currently valid.' }
        $eku = $loaded.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }
        if (!$eku -or '1.3.6.1.5.5.7.3.3' -notin $eku.EnhancedKeyUsages.Value) { throw 'The certificate must allow code signing.' }
        return $loaded
    } catch {
        $loaded.Dispose()
        throw
    }
}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot ('artifacts/msix/' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ($SigningKey) { $SigningKey = [IO.Path]::GetFullPath($SigningKey) }
if ($SigningKey) {
    $preflightCertificate = Read-SigningCertificate $SigningKey $signingPassword
    $preflightCertificate.Dispose()
}
if (Test-Path -LiteralPath $outputRoot) { throw 'Use a new output directory to avoid packaging stale files.' }
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$publishRoot = Join-Path $outputRoot 'publish'
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin/10.0.26100.0/x64'
$makeAppx = Join-Path $sdkRoot 'makeappx.exe'
$signTool = Join-Path $sdkRoot 'signtool.exe'
if (!(Test-Path -LiteralPath $makeAppx) -or !(Test-Path -LiteralPath $signTool)) { throw 'Windows SDK 26100 x64 packaging tools are required.' }
Push-Location $repoRoot
try {
    $restore = @('restore', 'src/Mani.App/Mani.App.csproj', '--locked-mode', '-p:Platform=x64', "-p:PublishProfile=$publishProfile", $diagnosticsProperty, '-m:1')
    if ($Offline) { $restore += '-p:NuGetAudit=false' }
    & dotnet @restore
    if ($LASTEXITCODE) { throw 'Locked package restore failed.' }
    & dotnet publish src/Mani.App/Mani.App.csproj --no-restore -c Release -p:Platform=x64 "-p:PublishProfile=$publishProfile" $diagnosticsProperty -p:IsolatedFileActivationTest=false "-p:Version=$Version" -p:UseSharedCompilation=false -m:1 -o $publishRoot
    if ($LASTEXITCODE) { throw 'Self-contained publish failed.' }
    $generatedManifest = Join-Path $repoRoot 'src/Mani.App/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/AppxManifest.xml'
    [xml]$manifest = Get-Content -LiteralPath $generatedManifest -Raw
    $manifest.Package.Identity.Name = $packageName
    $manifest.Package.Identity.Publisher = 'CN=Mani Internal Test'
    $manifest.Package.Properties.DisplayName = $displayName
    $manifest.Package.Properties.PublisherDisplayName = 'Mani Internal Test'
    $manifest.Package.Applications.Application.VisualElements.DisplayName = $displayName
    if ($manifest.Package.Dependencies.PackageDependency) { throw 'The internal package must not depend on an installed Windows App Runtime.' }
    foreach ($required in @('coreclr.dll', 'hostfxr.dll', 'Microsoft.UI.Xaml.dll', 'resources.pri', 'libmpv-2.dll', 'LICENSE', 'licenses/IINA-NOTICE.txt')) {
        if (!(Test-Path -LiteralPath (Join-Path $publishRoot $required))) { throw "Missing self-contained package payload: $required" }
    }
    $pinned = Get-Content -LiteralPath config/mpv-runtime.json -Raw | ConvertFrom-Json
    if ((Get-FileHash -LiteralPath (Join-Path $publishRoot 'libmpv-2.dll') -Algorithm SHA256).Hash -ne $pinned.sha256) { throw 'Pinned libmpv hash mismatch.' }
    foreach ($entry in $pinned.files) {
        $file = Join-Path $publishRoot $entry.file
        if (!(Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -ne $entry.bytes -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Native package payload differs: $($entry.file)" }
    }
    if ($Beta -and (Test-Path -LiteralPath (Join-Path $publishRoot 'Assets/baseline-media.json'))) { throw 'Beta must not contain diagnostic fixtures.' }
    $manifest.Package.Identity.Version = $Version.ToString()
    $manifest.Save((Join-Path $publishRoot 'AppxManifest.xml'))
    & python build/verify_package_assets.py --layout $publishRoot --report (Join-Path $outputRoot 'assets-publish.json')
    if ($LASTEXITCODE) { throw 'Published app icons are missing or differ from the branding inventory.' }
    & python build/package_source.py --output $outputRoot
    if ($LASTEXITCODE) { throw 'Source provenance capture failed.' }
    $sourceProvenance = Get-Content -LiteralPath (Join-Path $outputRoot 'source-info.json') -Raw | ConvertFrom-Json
    if (!$SigningKey) {
        $SigningKey = Join-Path $outputRoot 'internal-test-key.pfx'
        $rsa = [Security.Cryptography.RSA]::Create(3072)
        try {
            $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Mani Internal Test', $rsa,
                [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $eku = [Security.Cryptography.OidCollection]::new()
            [void]$eku.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $true))
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
            $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddYears(1))
            [IO.File]::WriteAllBytes($SigningKey, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $signingPassword))
            $certificate.Dispose()
        } finally { $rsa.Dispose() }
    }
    $certificate = Read-SigningCertificate $SigningKey $signingPassword
    try {
        if ($certificate.Subject -ne 'CN=Mani Internal Test') { throw 'Signing certificate subject does not match the internal publisher.' }
        [IO.File]::WriteAllBytes((Join-Path $outputRoot "$packageName.cer"), $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        $packages = @()
        foreach ($packageVersion in @($Version, $UpgradeVersion)) {
            $manifest.Package.Identity.Version = $packageVersion.ToString()
            $manifest.Save((Join-Path $publishRoot 'AppxManifest.xml'))
            $package = Join-Path $outputRoot "$($packageName)_$($packageVersion)_x64.msix"
            & $makeAppx pack /d $publishRoot /p $package /o | Out-File (Join-Path $outputRoot "pack-$packageVersion.log")
            if ($LASTEXITCODE) { throw "MSIX packing failed for $packageVersion." }
            & python build/verify_package_assets.py --package $package --report (Join-Path $outputRoot "assets-$packageVersion.json")
            if ($LASTEXITCODE) { throw "MSIX icon verification failed for $packageVersion." }
            $signArguments = @('sign', '/fd', 'SHA256', '/f', $SigningKey)
            if ($signingPassword) { $signArguments += @('/p', $signingPassword) }
            & $signTool @signArguments $package | Out-File (Join-Path $outputRoot "sign-$packageVersion.log")
            if ($LASTEXITCODE) { throw "MSIX signing failed for $packageVersion." }
            $signArguments = $null
            $signature = Get-AuthenticodeSignature -LiteralPath $package
            if ($signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or $signature.Status -eq 'HashMismatch') {
                throw "MSIX signer or content hash mismatch for $packageVersion."
            }
            # A self-signed certificate is intentionally not installed into the build machine's trust store.
            $packages += @{ version = $packageVersion.ToString(); path = [IO.Path]::GetFileName($package); sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash }
        }
        @{ schemaVersion = 2; packages = $packages; certificateThumbprint = $certificate.Thumbprint;
            publisher = $certificate.Subject; selfContained = $true; libmpv = $pinned;
            sourceCommit = $sourceProvenance.commit; source = $sourceProvenance; packageName = $packageName; diagnostics = !$Beta;
            workflowRun = if ($env:GITHUB_ACTIONS -eq 'true') { "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID" } else { $null };
            appSha256 = (Get-FileHash -LiteralPath (Join-Path $publishRoot 'Mani.App.dll') -Algorithm SHA256).Hash;
            playbackSha256 = (Get-FileHash -LiteralPath (Join-Path $publishRoot 'Mani.Playback.dll') -Algorithm SHA256).Hash;
            acceptance = 'Built and internally signed. Trust, installation, upgrade, launch and clean-machine behavior require separate evidence.' } |
            ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'package-info.json') -Encoding utf8
        $downloadFiles = @($packages | ForEach-Object { $_.path }) + @("$packageName.cer", 'package-info.json', 'source-info.json', 'source-files.json', 'Mani-source.zip')
        if ($Beta) {
            $instructions = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Beta-INSTALL.txt') -Raw).
                Replace('{VERSION}', $Version.ToString()).Replace('{UPGRADE_VERSION}', $UpgradeVersion.ToString())
            $instructions | Set-Content -LiteralPath (Join-Path $outputRoot 'INSTALL.txt') -Encoding utf8
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Verify-Downloads.ps1') -Destination $outputRoot
            $downloadFiles += @('INSTALL.txt', 'Verify-Downloads.ps1')
        }
        $downloadFiles | ForEach-Object {
            $digest = (Get-FileHash -LiteralPath (Join-Path $outputRoot $_) -Algorithm SHA256).Hash.ToLowerInvariant()
            "$digest  $_"
        } | Set-Content -LiteralPath (Join-Path $outputRoot 'SHA256SUMS.txt') -Encoding utf8
    } finally { $certificate.Dispose() }
    Write-Output "Internal packages prepared in $outputRoot"
} finally { Pop-Location }
