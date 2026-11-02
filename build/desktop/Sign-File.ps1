#Requires -Version 7.0
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
if (!$env:MANI_DESKTOP_SIGNING_KEY -or !$env:MANI_SIGNING_PASSWORD) { throw 'Signing credentials are missing.' }
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $env:MANI_DESKTOP_SIGNING_KEY, $env:MANI_SIGNING_PASSWORD,
    [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
try {
    if (!$certificate.HasPrivateKey) { throw 'The certificate has no signing key.' }
    $result = Set-AuthenticodeSignature -LiteralPath $Path -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
    # A private beta CA can be untrusted on the build machine; verify signer and digest.
    if ($result.Status -notin @('Valid','UnknownError','NotTrusted') -or $result.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) { throw "Signing failed: $($result.Status)" }
    if (!$result.TimeStamperCertificate) { throw 'The signature has no timestamp.' }
} finally { $certificate.Dispose() }
