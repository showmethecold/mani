param([Parameter(Mandatory=$true)][string]$PublishDirectory)
$ErrorActionPreference='Stop'
$experimentRepo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$experiment=Join-Path $experimentRepo 'artifacts/m5/sup-engine-integration'
$runtime=Join-Path $experiment 'runtime-patched'
$destination=Join-Path $experiment 'app'
$name='Mani.M5.SupExperiment'
if((Test-Path -LiteralPath $destination) -or (Get-AppxPackage -Name $name)) { throw 'Existing experiment package preserved' }
$source=(Resolve-Path -LiteralPath $PublishDirectory).Path
$manifest=Get-Content (Join-Path $runtime 'runtime-manifest.json') -Raw | ConvertFrom-Json
foreach($entry in $manifest.files) {
  $file=Join-Path $runtime $entry.file
  if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { throw "Runtime hash mismatch: $($entry.file)" }
}
New-Item -ItemType Directory -Path $destination | Out-Null
Get-ChildItem -LiteralPath $source | Copy-Item -Destination $destination -Recurse
New-Item -ItemType Directory -Force (Join-Path $destination 'Assets') | Out-Null
Get-ChildItem -LiteralPath (Join-Path $experimentRepo 'src/Mani.App/Assets') -File |
  Where-Object Name -Match '^(AppIcon|default-album-art|SplashScreen|Square|StoreLogo|Wide)' |
  Copy-Item -Destination (Join-Path $destination 'Assets')
# Native runtime changes are confined to this newly created package directory.
foreach($entry in $manifest.files) { Copy-Item -LiteralPath (Join-Path $runtime $entry.file) -Destination $destination -Force }
Copy-Item -LiteralPath (Join-Path $runtime 'mpv-runtime.json'),(Join-Path $runtime 'runtime-manifest.json') -Destination $destination -Force
$template=Get-Content (Join-Path $experimentRepo 'artifacts/m5/followup/app/AppxManifest.xml') -Raw
$template=$template.Replace('Mani.M5.Followup',$name).Replace('Mani M5 Test 0.5.2','Mani SUP Experiment 0.5.3').Replace('Version="0.5.2.0"','Version="0.5.3.0"')
Set-Content -LiteralPath (Join-Path $destination 'AppxManifest.xml') -Value $template -Encoding utf8
$licenseSource=Join-Path $experimentRepo 'artifacts/m5/sup-engine-ab/tools/msys64/ucrt64/share/licenses'
Copy-Item -LiteralPath $licenseSource -Destination (Join-Path $destination 'experimental-dependency-licenses') -Recurse
$engineLicenses=Join-Path $destination 'experimental-engine-licenses'
New-Item -ItemType Directory -Path "$engineLicenses/ffmpeg","$engineLicenses/mpv" -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $experimentRepo 'artifacts/m5/sup-engine-ab/src/ffmpeg') -File |
  Where-Object Name -Match '^(COPYING|LICENSE)' | Copy-Item -Destination "$engineLicenses/ffmpeg"
Get-ChildItem -LiteralPath (Join-Path $experiment 'src/mpv') -File |
  Where-Object Name -Match '^(Copyright|LICENSE)' | Copy-Item -Destination "$engineLicenses/mpv"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'EXPERIMENT-NOTICE.txt') -Destination $destination
Add-AppxPackage -Register (Join-Path $destination 'AppxManifest.xml')
$package=Get-AppxPackage -Name $name
if(!$package) { throw 'Isolated package registration failed' }
$data=Join-Path $env:LOCALAPPDATA ('Packages/'+$package.PackageFamilyName+'/LocalCache/Local/Mani')
if(Test-Path -LiteralPath (Join-Path $data 'settings.json')) { throw 'Existing experiment settings preserved' }
New-Item -ItemType Directory -Path $data -Force | Out-Null
@{schemaVersion=1;language='en-US';volumePercent=25;muted=$true;pauseWhenOpen=$true;controlBarAutoHideTimeout=2.5;recordHistory=$false;resumePosition=$false;enableThumbnailPreview=$false;media=@{SubtitleMatching='no'}} |
  ConvertTo-Json -Depth 5 | Set-Content (Join-Path $data 'settings.json') -Encoding utf8
@{family=$package.PackageFamilyName;data=$data;path=$destination;version='0.5.3.0';configuration='Release';diagnostics=$false;experimental=$true} |
  ConvertTo-Json | Set-Content (Join-Path $experiment 'registration.json') -Encoding utf8
$identity=@(Get-FileHash (Join-Path $destination 'Mani.App.dll'),(Join-Path $destination 'Mani.Playback.dll'),(Join-Path $destination 'resources.pri'),(Join-Path $destination 'libmpv-2.dll') | Select-Object Path,Hash)
$identity | ConvertTo-Json | Set-Content (Join-Path $experiment 'build-identity.json') -Encoding utf8
Get-Content (Join-Path $experiment 'registration.json')
