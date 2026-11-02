param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$Certificate,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$DisplayVersion = 'Beta 0.1',
    [version]$SetupVersion = '0.1.1.0',
    [string]$ExpectedPackageName = 'Mani.Beta.Test',
    [string]$SigningKey = '',
    [switch]$UnsignedForTesting,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
if ($SetupVersion.Build -lt 0 -or $SetupVersion.Revision -lt 0 -or
    $SetupVersion.Major -gt 65535 -or $SetupVersion.Minor -gt 65535 -or
    $SetupVersion.Build -gt 65535 -or $SetupVersion.Revision -gt 65535) { throw 'SetupVersion must contain four integers between 0 and 65535.' }
if ($DisplayVersion -notmatch '^[A-Za-z0-9 .-]{1,48}$') { throw 'Invalid display version.' }
if ((!$SigningKey -and !$UnsignedForTesting) -or ($SigningKey -and $UnsignedForTesting)) { throw 'Supply a signing key or explicitly select UnsignedForTesting.' }
if ($ExpectedPackageName -notmatch '^Mani\.[A-Za-z0-9.-]+$') { throw 'Unexpected package name.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packagePath = [IO.Path]::GetFullPath($Package)
$certificatePath = [IO.Path]::GetFullPath($Certificate)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw 'Use a new setup output directory.' }
if (!(Test-Path -LiteralPath $packagePath -PathType Leaf) -or [IO.Path]::GetExtension($packagePath) -ne '.msix') { throw 'Supply an existing signed MSIX package.' }
if (!(Test-Path -LiteralPath $certificatePath -PathType Leaf) -or [IO.Path]::GetExtension($certificatePath) -ne '.cer') { throw 'Supply the public signing certificate.' }
$publicCertificate = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificateFromFile($certificatePath)
try {
    if ($publicCertificate.HasPrivateKey -or $publicCertificate.NotBefore -gt (Get-Date) -or $publicCertificate.NotAfter -le (Get-Date)) { throw 'Expected a currently valid public certificate.' }
    $eku = $publicCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }
    $constraints = $publicCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' }
    if (!$eku -or '1.3.6.1.5.5.7.3.3' -notin $eku.EnhancedKeyUsages.Value -or $constraints.CertificateAuthority) { throw 'Expected an end-entity code signing certificate.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $packagePath
    if (!$signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $publicCertificate.Thumbprint -or
        $signature.Status -in @('HashMismatch', 'NotSigned', 'NotSupportedFileFormat', 'Incompatible')) { throw 'The MSIX signature does not match the public certificate.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entry = $archive.GetEntry('AppxManifest.xml')
        if (!$entry) { throw 'Missing MSIX manifest.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $archive.Dispose() }
    $identity = $manifest.Package.Identity
    if ($identity.Name -ne $ExpectedPackageName -or $identity.Publisher -ne $publicCertificate.Subject -or $identity.ProcessorArchitecture -ne 'x64') { throw 'Unexpected MSIX identity, publisher or architecture.' }
    if ($manifest.Package.Dependencies.PackageDependency) { throw 'The installer requires a self-contained MSIX.' }
    if ($ExpectedPackageName -eq 'Mani.Beta.Test') {
        & python (Join-Path $PSScriptRoot 'verify_package_assets.py') --package $packagePath
        if ($LASTEXITCODE) { throw 'The Beta MSIX has missing or invalid app icons.' }
    }
    $applications = @($manifest.Package.Applications.Application)
    if ($applications.Count -ne 1 -or $applications[0].Id -notmatch '^[A-Za-z0-9.]+$') { throw 'Expected one launchable application.' }
    $signingCertificate = $null
    if ($SigningKey) {
        $signingCertificate = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile([IO.Path]::GetFullPath($SigningKey), $env:MANI_SIGNING_PASSWORD,
            [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        if (!$signingCertificate.HasPrivateKey -or $signingCertificate.Thumbprint -ne $publicCertificate.Thumbprint) {
            $signingCertificate.Dispose(); throw 'The setup signing key must match the MSIX signing certificate.'
        }
    }
    try {
        $framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
        $compiler = Join-Path $framework 'csc.exe'
        if (!(Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework compiler is required.' }
        New-Item -ItemType Directory -Path $outputRoot | Out-Null
        $setupPath = Join-Path $outputRoot 'Mani-Beta-0.1-Windows-x64-Setup.exe'
        $packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $certificateHash = (Get-FileHash -LiteralPath $certificatePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $values = [ordered]@{
            DisplayVersion=$DisplayVersion; PackageName=$identity.Name; Publisher=$identity.Publisher;
            PackageVersion=$identity.Version; ApplicationId=$applications[0].Id;
            PackageSha256=$packageHash; CertificateSha256=$certificateHash; CertificateThumbprint=$publicCertificate.Thumbprint
        }
        $generated = '[assembly: System.Reflection.AssemblyTitle("Mani Setup")]' + "`n" +
            '[assembly: System.Reflection.AssemblyProduct("Mani")]' + "`n" +
            '[assembly: System.Reflection.AssemblyCompany("Mani")]' + "`n" +
            ('[assembly: System.Reflection.AssemblyVersion("' + $SetupVersion.ToString() + '")]') + "`nnamespace Mani.Setup { internal static class BuildInfo {`n"
        foreach ($item in $values.GetEnumerator()) { $generated += 'internal const string ' + $item.Key + ' = ' + (ConvertTo-Json -Compress -InputObject ([string]$item.Value)) + ";`n" }
        $generated += 'internal const long PackageBytes = ' + (Get-Item -LiteralPath $packagePath).Length + ";`n} }`n"
        $generatedPath = Join-Path $outputRoot 'BuildInfo.cs'
        [IO.File]::WriteAllText($generatedPath, $generated, [Text.UTF8Encoding]::new($false))
        $setupSource = Join-Path $PSScriptRoot 'setup'
        $arguments = @('/nologo', '/target:winexe', '/platform:x64', '/warnaserror', '/utf8output',
            ('/out:' + $setupPath), ('/win32manifest:' + (Join-Path $setupSource 'setup.manifest')),
            ('/win32icon:' + (Join-Path $repoRoot 'src/Mani.App/Assets/AppIcon.ico')),
            '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll',
            ('/resource:' + $packagePath + ',Mani.Setup.Msix'), ('/resource:' + $certificatePath + ',Mani.Setup.Certificate'),
            ('/resource:' + (Join-Path $setupSource 'Install.ps1') + ',Mani.Setup.Install.ps1'),
            (Join-Path $setupSource 'Setup.cs'), (Join-Path $setupSource 'SetupCore.cs'), $generatedPath)
        if ($Configuration -eq 'Release') { $arguments += @('/optimize+', '/debug-') } else { $arguments += @('/optimize-', '/debug:pdbonly') }
        & $compiler @arguments
        if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }
        $signatureStatus = 'UnsignedForTesting'
        if ($signingCertificate) {
            $signed = Set-AuthenticodeSignature -LiteralPath $setupPath -Certificate $signingCertificate -HashAlgorithm SHA256
            if (!$signed.SignerCertificate -or $signed.SignerCertificate.Thumbprint -ne $publicCertificate.Thumbprint -or $signed.Status -in @('HashMismatch', 'NotSigned')) { throw 'Setup signing failed.' }
            $signatureStatus = $signed.Status.ToString()
        }
        $metadata = [ordered]@{
            schemaVersion=1; displayVersion=$DisplayVersion; setupVersion=$SetupVersion.ToString(); configuration=$Configuration;
            setupFile=[IO.Path]::GetFileName($setupPath); setupSha256=(Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant();
            setupBytes=(Get-Item -LiteralPath $setupPath).Length; signatureStatus=$signatureStatus;
            packageName=$identity.Name; packageVersion=$identity.Version; packageSha256=$packageHash;
            certificateThumbprint=$publicCertificate.Thumbprint; certificateSha256=$certificateHash;
            acceptance='Built wrapper only. Installation, trust consent, upgrade and cancellation require separate validation.'
        }
        $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputRoot 'setup-info.json') -Encoding utf8
        Write-Output "Setup created: $setupPath"
    } finally { if ($signingCertificate) { $signingCertificate.Dispose() } }
} finally { $publicCertificate.Dispose() }
