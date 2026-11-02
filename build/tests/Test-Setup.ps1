param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo ('artifacts/setup-tests/' + [guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new test output directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
$builder = Join-Path $repo 'build/Build-Setup.ps1'
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$windowsPowerShell = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin/10.0.26100.0/x64'
$results = [Collections.Generic.List[object]]::new()
$priorPassword = $env:MANI_SIGNING_PASSWORD
$pfx = Join-Path $output 'fixture-key.pfx'
$packageName = 'Mani.Setup.Fixture' + [guid]::NewGuid().ToString('N').Substring(0,8)
$certificateBefore = @(Get-ChildItem Cert:/LocalMachine/TrustedPeople | ForEach-Object Thumbprint | Sort-Object)
$packagesBefore = @(Get-AppxPackage | ForEach-Object PackageFullName | Sort-Object)
function Pass([string]$Name) { $results.Add(@{ name=$Name; passed=$true }); Write-Output "PASS $Name" }
function Reject([string]$Name, [hashtable]$Arguments, [string]$Expected) {
    $destination = Join-Path $output $Name
    $caught = $null
    try { & $builder @Arguments -OutputDirectory $destination } catch { $caught = $_ }
    if (!$caught -or $caught.ToString() -notmatch [regex]::Escape($Expected)) { throw "Missing expected rejection: $Name ($caught)" }
    if (Test-Path -LiteralPath $destination) { throw "Rejected build created output: $Name" }
    Pass $Name
}
try {
    $payload = Join-Path $output 'fixture-payload'; New-Item -ItemType Directory -Path $payload | Out-Null
    $source = Join-Path $output 'Fixture.cs'
    [IO.File]::WriteAllText($source, 'class Fixture { [System.STAThread] static void Main() {} }')
    & (Join-Path $framework 'csc.exe') /nologo /target:winexe /platform:x64 ('/out:' + (Join-Path $payload 'Fixture.exe')) $source
    if ($LASTEXITCODE) { throw 'Fixture compile failed.' }
    Copy-Item -LiteralPath (Join-Path $repo 'src/Mani.App/Assets/Square44x44Logo.targetsize-256.png') -Destination (Join-Path $payload 'Logo.png')
    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" IgnorableNamespaces="uap rescap">
<Identity Name="$packageName" Publisher="CN=Mani Setup Fixture" Version="1.0.0.0" ProcessorArchitecture="x64"/>
<Properties><DisplayName>Mani Setup Fixture</DisplayName><PublisherDisplayName>Mani Test</PublisherDisplayName><Logo>Logo.png</Logo></Properties>
<Resources><Resource Language="en-US"/></Resources><Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.26100.0" MaxVersionTested="10.0.26100.0"/></Dependencies>
<Applications><Application Id="App" Executable="Fixture.exe" EntryPoint="Windows.FullTrustApplication"><uap:VisualElements DisplayName="Mani Setup Fixture" Description="Installer validation fixture" BackgroundColor="transparent" Square150x150Logo="Logo.png" Square44x44Logo="Logo.png"/></Application></Applications>
<Capabilities><rescap:Capability Name="runFullTrust"/></Capabilities></Package>
"@
    [IO.File]::WriteAllText((Join-Path $payload 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))
    $msix = Join-Path $output 'fixture.msix'
    & (Join-Path $sdk 'makeappx.exe') pack /d $payload /p $msix /o | Out-File (Join-Path $output 'makeappx.log')
    if ($LASTEXITCODE) { throw 'Fixture package failed.' }
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Mani Setup Fixture', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $eku = [Security.Cryptography.OidCollection]::new(); [void]$eku.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $true))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
        $cert = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(2))
        try {
            $cer = Join-Path $output 'fixture.cer'
            [IO.File]::WriteAllBytes($cer, $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
            [IO.File]::WriteAllBytes($pfx, $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, 'fixture-only'))
            $thumbprint = $cert.Thumbprint
        } finally { $cert.Dispose() }
        $wrongCert = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(3))
        try { [IO.File]::WriteAllBytes((Join-Path $output 'wrong.cer'), $wrongCert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert)) }
        finally { $wrongCert.Dispose() }
    } finally { $rsa.Dispose() }
    & (Join-Path $sdk 'signtool.exe') sign /fd SHA256 /f $pfx /p fixture-only $msix | Out-File (Join-Path $output 'signtool.log')
    if ($LASTEXITCODE) { throw 'Fixture signing failed.' }
    $base = @{ Package=$msix; Certificate=$cer; ExpectedPackageName=$packageName; UnsignedForTesting=$true }
    Reject 'signing-choice-required' @{ Package=$msix; Certificate=$cer } 'Supply a signing key'
    Reject 'short-setup-version' ($base + @{ SetupVersion='0.1' }) 'four integers'
    Reject 'wrong-package-name' @{ Package=$msix; Certificate=$cer; UnsignedForTesting=$true } 'Unexpected MSIX identity'
    Reject 'wrong-certificate' @{ Package=$msix; Certificate=(Join-Path $output 'wrong.cer'); UnsignedForTesting=$true; ExpectedPackageName=$packageName } 'signature does not match'
    $unsigned = Join-Path $output 'unsigned'
    & $builder @base -OutputDirectory $unsigned
    $setup = Join-Path $unsigned 'Mani-Beta-0.1-Windows-x64-Setup.exe'
    $report = Join-Path $output 'embedded.json'
    $process = Start-Process -FilePath $setup -ArgumentList @('--verify-payload', ('"' + $report + '"')) -WindowStyle Hidden -PassThru -Wait -RedirectStandardError (Join-Path $output 'embedded-errors.txt')
    if ($process.ExitCode) { throw ('Embedded payload check failed: ' + (Get-Content (Join-Path $output 'embedded-errors.txt') -Raw)) }
    $verified = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (!$verified.valid -or $verified.installed -or $verified.certificateImported -or $verified.packageVersion -ne '1.0.0.0') { throw 'Invalid embedded verification result.' }
    Pass 'embedded-msix-verification-without-install-or-trust'
    Add-Type -TypeDefinition @'
public static class SetupFixtureBytes {
    public static int Find(byte[] data, byte[] pattern) {
        for (int i = 0; i <= data.Length - pattern.Length; i++) {
            bool match = true;
            for (int j = 0; j < 64; j++) if (data[i+j] != pattern[j]) { match=false; break; }
            if (match) return i;
        }
        return -1;
    }
}
'@
    $exeBytes=[IO.File]::ReadAllBytes($setup); $msixBytes=[IO.File]::ReadAllBytes($msix)
    $offset=[SetupFixtureBytes]::Find($exeBytes,$msixBytes)
    if ($offset -lt 0) { throw 'Embedded fixture resource not found.' }
    $exeBytes[$offset + [int]($msixBytes.Length / 2)] = $exeBytes[$offset + [int]($msixBytes.Length / 2)] -bxor 1
    $tampered=Join-Path $output 'Tampered-Setup.exe'; [IO.File]::WriteAllBytes($tampered,$exeBytes)
    $tamperedReport=Join-Path $output 'tampered.json'; $tamperedError=Join-Path $output 'tampered-error.txt'
    $tamperedProcess=Start-Process -FilePath $tampered -ArgumentList @('--verify-payload', ('"' + $tamperedReport + '"')) -WindowStyle Hidden -PassThru -Wait -RedirectStandardError $tamperedError
    if ($tamperedProcess.ExitCode -eq 0 -or (Get-Content $tamperedError -Raw) -notmatch 'checksum mismatch') { throw 'Tampered embedded MSIX was not rejected.' }
    Pass 'tampered-embedded-msix-rejected-before-install-or-trust'

    $layout=Join-Path $output 'layout'; New-Item -ItemType Directory -Path $layout | Out-Null
    $layoutExe=Join-Path $layout 'SetupLayoutChecks.exe'
    & (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /warnaserror /main:SetupLayoutChecks ('/out:' + $layoutExe) ('/win32manifest:' + (Join-Path $repo 'build/setup/setup.manifest')) ('/win32icon:' + (Join-Path $repo 'src/Mani.App/Assets/AppIcon.ico')) /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll (Join-Path $repo 'build/setup/Setup.cs') (Join-Path $repo 'build/setup/SetupCore.cs') (Join-Path $unsigned 'BuildInfo.cs') (Join-Path $repo 'build/tests/SetupLayoutChecks.cs')
    if ($LASTEXITCODE) { throw 'Layout test compilation failed.' }
    $layoutProcess=Start-Process -FilePath $layoutExe -ArgumentList ('"' + $layout + '"') -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput (Join-Path $layout 'result.txt') -RedirectStandardError (Join-Path $layout 'error.txt')
    if ($layoutProcess.ExitCode) { throw ('Off-screen layout checks failed: ' + (Get-Content (Join-Path $layout 'error.txt') -Raw)) }
    Pass 'six-bilingual-offscreen-layout-and-consent-states'
    $env:MANI_SIGNING_PASSWORD = 'fixture-only'
    $signedOutput = Join-Path $output 'signed'
    & $builder -Package $msix -Certificate $cer -ExpectedPackageName $packageName -SigningKey $pfx -OutputDirectory $signedOutput
    $signed = Get-AuthenticodeSignature -LiteralPath (Join-Path $signedOutput 'Mani-Beta-0.1-Windows-x64-Setup.exe')
    if ($signed.SignerCertificate.Thumbprint -ne $thumbprint -or $signed.Status -in @('NotSigned','HashMismatch')) { throw 'Signed bootstrapper identity mismatch.' }
    Pass 'bootstrapper-signature-without-certificate-import'

    # Contract tests replace only Windows deployment commands inside a child process.
    # They execute the exact production script; no package installation or trust occurs.
    $script = Get-Content -LiteralPath (Join-Path $repo 'build/setup/Install.ps1') -Raw
    $mock = @'
$script:queries = 0
function Get-AppxPackage {
    param([string]$Name)
    $script:queries++
    $versions = $env:FIXTURE_VERSIONS.Split(',')
    $v = $versions[[Math]::Min($script:queries - 1, $versions.Length - 1)]
    if ($v -ne '-') { [pscustomobject]@{ Name=$Name; Publisher=$env:MANI_SETUP_PUBLISHER; Version=[version]$v; Status='Ok'; PackageFamilyName='Mani.Fixture_family' } }
}
function Get-AuthenticodeSignature {
    param([string]$LiteralPath)
    [pscustomobject]@{ SignerCertificate=[pscustomobject]@{ Thumbprint=$env:MANI_SETUP_THUMBPRINT }; Status='Valid'; StatusMessage='Valid test signature' }
}
function Add-AppxPackage {
    param([string]$Path)
    [IO.File]::AppendAllText($env:FIXTURE_TRACE, "install`n")
    if ($env:FIXTURE_FAIL -eq '1') { throw '0x80073D02: close the running app and try again.' }
}
'@
    $cases = @(
        @{ Name='fresh-inspection'; Mode='inspect'; Versions='-'; State='missing'; Installs=0; Success=$true },
        @{ Name='same-version-preserved'; Mode='inspect'; Versions='1.0.0.0'; State='installed'; Installs=0; Success=$true },
        @{ Name='newer-version-preserved'; Mode='inspect'; Versions='2.0.0.0'; State='newer'; Installs=0; Success=$true },
        @{ Name='successful-upgrade-readback'; Mode='install'; Versions='0.9.0.0,0.9.0.0,1.0.0.0'; State='installed'; Installs=1; Success=$true },
        @{ Name='newer-version-race'; Mode='install'; Versions='0.9.0.0,2.0.0.0'; State='newer'; Installs=0; Success=$true },
        @{ Name='running-app-recoverable-error'; Mode='install'; Versions='0.9.0.0'; State='failed'; Installs=1; Success=$false; Fail='1' },
        @{ Name='deployment-readback-failure'; Mode='install'; Versions='0.9.0.0'; State='failed'; Installs=1; Success=$false }
    )
    foreach ($case in $cases) {
        $trace = Join-Path $output ($case.Name + '.trace')
        $start = [Diagnostics.ProcessStartInfo]::new($windowsPowerShell)
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        $start.StandardOutputEncoding=[Text.Encoding]::UTF8; $start.StandardErrorEncoding=[Text.Encoding]::UTF8
        $start.Arguments='-NoProfile -NonInteractive -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($mock + "`n" + $script))
        $values=@{ PSModulePath=(Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/Modules'); MANI_SETUP_MODE=$case.Mode; MANI_SETUP_PACKAGE=$msix; MANI_SETUP_NAME=$packageName;
            MANI_SETUP_PUBLISHER='CN=Mani Setup Fixture'; MANI_SETUP_VERSION='1.0.0.0'; MANI_SETUP_SHA256=(Get-FileHash $msix).Hash;
            MANI_SETUP_THUMBPRINT=$thumbprint; FIXTURE_VERSIONS=$case.Versions; FIXTURE_FAIL=([string]$case.Fail); FIXTURE_TRACE=$trace }
        foreach ($key in $values.Keys) { $start.EnvironmentVariables[$key]=[string]$values[$key] }
        $child=[Diagnostics.Process]::Start($start); $stdout=$child.StandardOutput.ReadToEndAsync(); $stderr=$child.StandardError.ReadToEndAsync()
        if (!$child.WaitForExit(60000)) { $child.Kill(); throw 'Deployment contract test timed out.' }
        $text=$stdout.GetAwaiter().GetResult(); $errorText=$stderr.GetAwaiter().GetResult(); $child.Dispose()
        $actual=$text | ConvertFrom-Json
        $count=if (Test-Path $trace) { @(Get-Content $trace).Count } else { 0 }
        if ($actual.ok -ne $case.Success -or $actual.state -ne $case.State -or $count -ne $case.Installs) { throw "Deployment contract failed: $($case.Name) $text $errorText" }
        Pass $case.Name
    }
    $certificateAfter=@(Get-ChildItem Cert:/LocalMachine/TrustedPeople | ForEach-Object Thumbprint | Sort-Object)
    $packagesAfter=@(Get-AppxPackage | ForEach-Object PackageFullName | Sort-Object)
    if (($certificateBefore -join ',') -ne ($certificateAfter -join ',') -or ($packagesBefore -join ',') -ne ($packagesAfter -join ',')) {
        $difference = @{
            certificatesAdded = @($certificateAfter | Where-Object { $_ -notin $certificateBefore })
            certificatesRemoved = @($certificateBefore | Where-Object { $_ -notin $certificateAfter })
            packagesAdded = @($packagesAfter | Where-Object { $_ -notin $packagesBefore })
            packagesRemoved = @($packagesBefore | Where-Object { $_ -notin $packagesAfter })
        } | ConvertTo-Json -Depth 3
        $difference | Set-Content -LiteralPath (Join-Path $output 'host-state-difference.json')
        Write-Output $difference
        throw 'Tests changed package or certificate state.'
    }
    Pass 'host-packages-and-trust-unchanged'
    @{ passed=$true; checks=$results; package=$packageName; fixtureNeverInstalled=$true } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8
    Write-Output "$($results.Count) setup checks passed: $output"
} finally {
    $env:MANI_SIGNING_PASSWORD=$priorPassword
    if (Test-Path -LiteralPath $pfx) { Remove-Item -LiteralPath $pfx -Force }
}
