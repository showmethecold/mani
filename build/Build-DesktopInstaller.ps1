#Requires -Version 7.0
param(
    [string]$Version = '0.5.9.0',
    [string]$OutputDirectory = 'artifacts/desktop-installer',
    [string]$Compiler,
    [string]$SigningKey,
    [switch]$UnsignedForTesting,
    [switch]$TestInstall
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or @(([version]$Version).Major,([version]$Version).Minor,([version]$Version).Build,([version]$Version).Revision | Where-Object {$_ -gt 65535}).Count) { throw 'Use a four-part Windows version.' }
if (!$SigningKey -and !$UnsignedForTesting) { throw 'Provide a signing key, or explicitly select UnsignedForTesting.' }
if ($TestInstall -and !$UnsignedForTesting) { throw 'Isolated test installers must not be signed as releases.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory; stale files must not enter an installer.' }
if (!$output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build output must be inside the workspace.' }
New-Item -ItemType Directory $output | Out-Null
if (!$Compiler) { $Compiler = & (Join-Path $PSScriptRoot 'Restore-InstallerTool.ps1') }
$Compiler = (Resolve-Path -LiteralPath $Compiler).Path
$lock = Get-Content (Join-Path $root 'config/installer-tool.lock.json') -Raw | ConvertFrom-Json
& (Join-Path $PSScriptRoot 'Assert-InstallerTool.ps1') -Compiler $Compiler
$banner = (& $Compiler --version 2>&1 | Out-String).Trim()
if ($banner -ne $lock.version) { throw "Compiler version differs from the locked version: $banner" }
$payload = Join-Path $output 'app'
$project = Join-Path $root 'src/Mani.App/Mani.App.csproj'
& dotnet restore $project --locked-mode -p:Platform=x64
if ($LASTEXITCODE) { throw 'Locked restore failed.' }
# The same project also builds MSIX and diagnostics: rebuild to avoid stale XAML/PRI.
& dotnet clean $project -c Release -p:Platform=x64 -v:quiet
if ($LASTEXITCODE) { throw 'Clean failed.' }
& dotnet publish $project --no-restore -c Release -p:Platform=x64 "-p:PublishProfileFullPath=$(Join-Path $PSScriptRoot 'Desktop.Publish.props')" "-p:Version=$Version" "-p:IsolatedFileActivationTest=$($TestInstall.IsPresent.ToString().ToLowerInvariant())" -p:EnableDiagnostics=false -p:UseSharedCompilation=false -m:1 -o $payload
if ($LASTEXITCODE) { throw 'Desktop publish failed.' }
foreach ($required in @('Mani.App.exe','Mani.App.dll','Mani.App.pri','libmpv-2.dll','Assets/AppIcon.ico','Microsoft.UI.Xaml.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Required published file missing: $required" }
}
$assembly = [IO.File]::ReadAllBytes((Join-Path $payload 'Mani.App.dll'))
$isolated = [Text.Encoding]::Unicode.GetString($assembly).Contains('Mani.IsolatedFileActivationTests') -or [Text.Encoding]::Unicode.GetString($assembly,1,$assembly.Length-1).Contains('Mani.IsolatedFileActivationTests')
if ($isolated -ne $TestInstall.IsPresent) { throw 'Unexpected app data isolation mode.' }
if (Test-Path (Join-Path $payload 'Assets/baseline-media.json')) { throw 'Diagnostics fixtures must not be shipped.' }
Copy-Item -LiteralPath (Join-Path (Split-Path $Compiler) 'License.txt') -Destination (Join-Path $payload 'licenses/Inno-Setup.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'desktop/INSTALL.txt'),(Join-Path $PSScriptRoot 'desktop/INSTALL.zh-CN.txt') -Destination $output
[xml]$manifest = Get-Content (Join-Path $root 'src/Mani.App/Package.appxmanifest') -Raw
$extensions = @($manifest.Package.Applications.Application.Extensions.Extension.FileTypeAssociation.SupportedFileTypes.FileType)
if (!$extensions.Count) { throw 'No video associations in source manifest.' }
$include = Join-Path $output 'associations.iss'
$lines = foreach ($extension in $extensions) {
    if ($extension -notmatch '^\.[a-z0-9]+$') { throw 'Invalid file extension.' }
    'Root: HKA; Subkey: "Software\Classes\' + $extension + '\OpenWithProgids"; ValueType: none; ValueName: "{#ProductId}.Video"; Flags: uninsdeletevalue'
    'Root: HKA; Subkey: "Software\{#ProductId}\Capabilities\FileAssociations"; ValueType: string; ValueName: "' + $extension + '"; ValueData: "{#ProductId}.Video"'
}
$lines | Set-Content -LiteralPath $include -Encoding utf8
$name = if ($TestInstall) { 'Mani-Directory-Test-Setup' } elseif ($UnsignedForTesting) { 'Mani-Local-Unsigned-Setup' } else { 'Mani-Beta-0.1-Windows-x64-Setup' }
$arguments = @('--quiet','--no-ide-signtools',"/DAppVersion=$Version","/DPayload=$payload","/DInstallerOutput=$output","/DInstallerName=$name","/DAssociationInclude=$include",'/DSignUninstaller=no')
if ($TestInstall) { $arguments += '/DTestInstall=1' }
$oldKey = $env:MANI_DESKTOP_SIGNING_KEY
try {
    if ($SigningKey) {
        $env:MANI_DESKTOP_SIGNING_KEY = (Resolve-Path -LiteralPath $SigningKey).Path
        & (Join-Path $PSScriptRoot 'desktop/Sign-File.ps1') -Path (Join-Path $payload 'Mani.App.exe')
        $arguments = @($arguments | Where-Object {$_ -notin @('/DSignUninstaller=no','--quiet')}) + @('/DSignUninstaller=yes','/DSignCommand=1')
        $signScript = Join-Path $PSScriptRoot 'desktop/Sign-File.ps1'
        # Use this host: Windows PowerShell inherits an incompatible PSModulePath
        # through ISCC and cannot import its signing module.
        $signHost = Join-Path $PSHOME 'pwsh.exe'
        $arguments += '/Smani=$q' + $signHost + '$q -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $q' + $signScript + '$q -Path $f'
    }
    & $Compiler @arguments (Join-Path $PSScriptRoot 'desktop/Mani.iss')
    if ($LASTEXITCODE) { throw 'Installer compilation failed.' }
} finally { $env:MANI_DESKTOP_SIGNING_KEY = $oldKey }
$files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
    @{path=[IO.Path]::GetRelativePath($payload,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant();bytes=$_.Length}
})
$files | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'installed-files.json') -Encoding utf8
$info = [ordered]@{version=$Version;format='desktop';isolated=$TestInstall.IsPresent;signed=[bool]$SigningKey;compiler=$lock;extensions=$extensions;files=$files.Count;installer="$name.exe";sha256=(Get-FileHash (Join-Path $output "$name.exe")).Hash.ToLowerInvariant();nativeSha256=(Get-FileHash (Join-Path $payload 'libmpv-2.dll')).Hash.ToLowerInvariant()}
$signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $output "$name.exe")
$info.configuration = 'Release'
$info.signatureStatus = $signature.Status.ToString()
$info.signerThumbprint = if ($signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint } else { $null }
$info.timestamped = [bool]$signature.TimeStamperCertificate
$info.sourceCommit = (& git -C $root rev-parse HEAD).Trim()
$info.sourceDirty = [bool](& git -C $root status --porcelain)
if ($SigningKey -and (!$info.timestamped -or $info.signatureStatus -notin @('Valid','NotTrusted','UnknownError'))) { throw 'The final installer signature is invalid or lacks a timestamp.' }
$info | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'setup-info.json') -Encoding utf8
