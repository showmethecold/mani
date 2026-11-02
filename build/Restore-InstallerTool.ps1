param([string]$OutputDirectory = 'artifacts/tools/inno')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lock = Get-Content (Join-Path $root 'config/installer-tool.lock.json') -Raw | ConvertFrom-Json
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (!$destination.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'The compiler must be restored inside the workspace.' }
New-Item -ItemType Directory -Force $destination | Out-Null
$archive = Join-Path $destination $lock.file
if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest $lock.url -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive).Hash -ne $lock.sha256) { throw 'Inno Setup SHA-256 mismatch.' }
$signature = Get-AuthenticodeSignature -LiteralPath $archive
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -ne $lock.signer) { throw 'Inno Setup publisher verification failed.' }
$compiler = Join-Path $destination 'compiler'
if (!(Test-Path -LiteralPath (Join-Path $compiler 'ISCC.exe'))) {
    $process = Start-Process $archive -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/PORTABLE=1','/CURRENTUSER','/NOICONS','/TASKS=""',('/DIR="' + $compiler + '"')) -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode) { throw "Compiler extraction failed: $($process.ExitCode)" }
}
& (Join-Path $PSScriptRoot 'Assert-InstallerTool.ps1') -Compiler (Join-Path $compiler 'ISCC.exe')
$banner = (& (Join-Path $compiler 'ISCC.exe') --version 2>&1 | Out-String).Trim()
if ($banner -ne $lock.version) { throw "Unexpected cached compiler version: $banner" }
Write-Output (Join-Path $compiler 'ISCC.exe')
