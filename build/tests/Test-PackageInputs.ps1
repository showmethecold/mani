# Exercise rejection paths before an invalid request can start a build or create output.
$ErrorActionPreference = 'Stop'
$builder = Join-Path $PSScriptRoot '../Build-InternalPackage.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('mani-package-inputs-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$previousPassword = $env:MANI_SIGNING_PASSWORD
try {
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Unrelated Test Publisher', $rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1), [DateTimeOffset]::UtcNow.AddDays(1))
        try { [IO.File]::WriteAllBytes((Join-Path $testRoot 'wrong-publisher.pfx'), $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, 'test-password')) }
        finally { $certificate.Dispose() }
    } finally { $rsa.Dispose() }
    $cases = @(
        @{ Name = 'three-part-version'; Arguments = @{ Version = '0.2.1' }; Error = 'four integers' },
        @{ Name = 'overflow-version'; Arguments = @{ Version = '0.2.65536.0' }; Error = 'four integers' },
        @{ Name = 'non-increasing-upgrade'; Arguments = @{ Version = '0.2.1.0'; UpgradeVersion = '0.2.1.0' }; Error = 'must be greater' },
        @{ Name = 'missing-ci-key'; Arguments = @{ RequireSigningKey = $true }; Error = 'persistent signing key' },
        @{ Name = 'incorrect-password'; Arguments = @{ SigningKey = (Join-Path $testRoot 'wrong-publisher.pfx') }; Error = 'password' },
        @{ Name = 'wrong-publisher'; Arguments = @{ SigningKey = (Join-Path $testRoot 'wrong-publisher.pfx') }; Password = 'test-password'; Error = 'CN=Mani Internal Test' }
    )
    foreach ($case in $cases) {
        $env:MANI_SIGNING_PASSWORD = $case.Password
        $arguments = $case.Arguments
        $output = Join-Path $testRoot $case.Name
        $caught = $null
        try { & $builder @arguments -OutputDirectory $output } catch { $caught = $_ }
        if (!$caught -or $caught.ToString() -notmatch [regex]::Escape($case.Error)) { throw "Expected rejection was not observed: $($case.Name)" }
        if (Test-Path -LiteralPath $output) { throw "Rejected input created build output: $($case.Name)" }
    }
    Write-Output "$($cases.Count) package input checks passed."
} finally {
    $env:MANI_SIGNING_PASSWORD = $previousPassword
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$resolvedTestRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected test cleanup directory.' }
    Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
}
