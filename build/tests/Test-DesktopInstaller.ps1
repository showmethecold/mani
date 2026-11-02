param([Parameter(Mandatory)][string]$BuildDirectory,[Parameter(Mandatory)][string]$OutputDirectory,[string]$UpgradeBuildDirectory)
$ErrorActionPreference = 'Stop'
$build = (Resolve-Path -LiteralPath $BuildDirectory).Path
$info = Get-Content (Join-Path $build 'setup-info.json') -Raw | ConvertFrom-Json
if (!$info.isolated -or $info.installer -ne 'Mani-Directory-Test-Setup.exe') { throw 'Only a compile-time isolated installer is allowed.' }
$setup = Join-Path $build $info.installer
if ((Get-FileHash $setup).Hash -ne $info.sha256) { throw 'Installer hash mismatch.' }
$originalSetup=$setup
$upgradeInfo=$null
if($UpgradeBuildDirectory) {
    $upgradeRoot=(Resolve-Path -LiteralPath $UpgradeBuildDirectory).Path
    $upgradeInfo=Get-Content (Join-Path $upgradeRoot 'setup-info.json') -Raw | ConvertFrom-Json
    if(!$upgradeInfo.isolated -or $upgradeInfo.installer -ne 'Mani-Directory-Test-Setup.exe' -or [version]$upgradeInfo.version -le [version]$info.version) {throw 'Expected a newer isolated upgrade installer.'}
    $upgradeSetup=Join-Path $upgradeRoot $upgradeInfo.installer
    if((Get-FileHash $upgradeSetup).Hash -ne $upgradeInfo.sha256) {throw 'Upgrade installer hash mismatch.'}
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$output.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)) { throw 'Use a new test directory inside the workspace.' }
$product = 'Mani.Desktop.InstallTest'
$key = "HKCU:\Software\$product"
if ((Test-Path $key) -or (Test-Path "HKLM:\Software\$product")) { throw 'An earlier isolated test install exists; preserve it for inspection.' }
New-Item -ItemType Directory $output | Out-Null
$target = Join-Path $output '自选 目录 # &/Mani'
$exe = Join-Path $target 'Mani.App.exe'
$data = Join-Path $target 'Mani.IsolatedFileActivationTests'
$results = [ordered]@{version=$info.version;installerSha256=$info.sha256}
$userRoots = @((Join-Path $env:LOCALAPPDATA 'Mani'),(Join-Path $env:LOCALAPPDATA 'Packages/Mani.Beta.Test_h6anv01wm286t/LocalCache/Local/Mani'))
function User-Hashes {
    @(foreach($root in $userRoots) {
        if (Test-Path -LiteralPath $root) { Get-ChildItem -LiteralPath $root -File | Get-FileHash | Select-Object Path,Hash }
        if (Test-Path -LiteralPath (Join-Path $root 'watch-later')) { Get-ChildItem -LiteralPath (Join-Path $root 'watch-later') -File | Get-FileHash | Select-Object Path,Hash }
    }) | ConvertTo-Json -Compress -Depth 4
}
function Defaults {
    $values = [ordered]@{}
    foreach($ext in $info.extensions) {
        $choice = Get-Item -LiteralPath "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$ext\UserChoice" -ErrorAction SilentlyContinue
        $values[$ext] = if($choice) { @($choice.GetValue('ProgId'),$choice.GetValue('Hash')) } else {$null}
    }
    $values | ConvertTo-Json -Compress
}
function Run-Setup([string]$name,[string[]]$extra,[bool]$expectSuccess=$true) {
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/LOG="'+(Join-Path $output "$name.log")+'"')) + $extra
    $process = Start-Process $setup -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    $results[$name + 'ExitCode'] = $process.ExitCode
    if (($process.ExitCode -eq 0) -ne $expectSuccess) { throw "Unexpected $name result: $($process.ExitCode)" }
}
function Close-OwnedPlayer {
    $processes = @(Get-Process -Name 'Mani.App' -ErrorAction SilentlyContinue | Where-Object Path -eq $exe)
    foreach($process in $processes) {
        for($i=0;$i -lt 12 -and !$process.HasExited;$i++) {
            $process.Refresh(); $null=$process.CloseMainWindow(); $null=$process.WaitForExit(1500)
        }
        if (!$process.HasExited) { throw 'The isolated player did not close normally; leave evidence intact.' }
    }
}
$userBefore=User-Hashes; $defaultsBefore=Defaults
$packagesBefore=@(Get-AppxPackage -Name 'Mani.Beta.Test' | ForEach-Object PackageFullName) -join ','
$trustBefore=@(Get-ChildItem Cert:/LocalMachine/TrustedPeople | ForEach-Object Thumbprint | Sort-Object) -join ','
$userBefore | Set-Content (Join-Path $output 'user-hashes-before.json')
Add-Type -Path (Join-Path $PSScriptRoot 'FileActivationProbe.cs')
try {
    $occupied = Join-Path $output 'occupied'
    New-Item -ItemType Directory $occupied | Out-Null
    Set-Content (Join-Path $occupied 'keep.txt') 'not installer data'
    Run-Setup 'occupied-rejected' @('/LANG=en',('/DIR="'+$occupied+'"')) $false
    if ((Get-Content (Join-Path $occupied 'keep.txt')) -ne 'not installer data') {throw 'Existing file changed.'}
    Run-Setup 'install' @('/LANG=zh',('/DIR="'+$target+'"'),'/TASKS=desktopicon')
    if ((Get-ItemProperty $key).InstallDir -ne $target) {throw 'The chosen installation path was not retained.'}
    foreach($file in (Get-Content (Join-Path $build 'installed-files.json') -Raw | ConvertFrom-Json)) {
        if((Get-FileHash -LiteralPath (Join-Path $target $file.path)).Hash -ne $file.sha256) {throw "Installed payload differs: $($file.path)"}
    }
    $results.payloadHashesVerified=$true
    $results.handlers=[ordered]@{}
    foreach($ext in $info.extensions) {
        $handlers=@([FileActivationProbe]::Handlers($ext) | Where-Object {$_[2] -like "$target*"})
        if(!$handlers.Count) {throw "Open with is missing: $ext"}
        $results.handlers[$ext]=$handlers
    }
    # WScript.Shell exposes an ANSI target for this Unicode path. Read the actual
    # Unicode Shell link contract used by Explorer instead.
    $shortcut = [FileActivationProbe]::Shortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Mani Installation Test.lnk'))
    $results.startShortcut=@{target=$shortcut[0];icon=$shortcut[1];expected=$exe}
    if ([IO.Path]::GetFullPath($shortcut[0]) -ne [IO.Path]::GetFullPath($exe) -or [IO.Path]::GetFullPath($shortcut[1]) -ne [IO.Path]::GetFullPath($exe)) {throw 'Start shortcut target or icon is incorrect.'}
    $results.startShortcutVerified=$true
    New-Item -ItemType Directory $data | Out-Null
    @{schemaVersion=1;language='zh-CN';volumePercent=25;muted=$true;pauseWhenOpen=$true;controlBarAutoHideTimeout=2.5;recordHistory=$false;resumePosition=$false;enableThumbnailPreview=$false} | ConvertTo-Json | Set-Content (Join-Path $data 'settings.json')
    $sample=Join-Path $workspace 'tests/media/generated/m1/1080p-sdr.mkv'
    $first=Join-Path $output '视频 %20 # &.mp4'
    Copy-Item -LiteralPath $sample -Destination $first
    $results.sampleSha256=(Get-FileHash $sample).Hash
    [FileActivationProbe]::OpenWith($target,@($first))
    Start-Sleep -Seconds 8
    Run-Setup 'running-rejected' @('/LANG=en') $false
    $bad=Join-Path $output '损坏.mp4'; Set-Content $bad 'not a video'
    [FileActivationProbe]::OpenWith($target,@($bad))
    Start-Sleep -Seconds 3
    1..3 | ForEach-Object {
        $path=Join-Path $output "rapid-$_.mkv"; Copy-Item -LiteralPath $sample -Destination $path
        [FileActivationProbe]::OpenWith($target,@($path))
    }
    Start-Sleep -Seconds 8
    $processes=@(Get-Process -Name 'Mani.App' -ErrorAction SilentlyContinue | Where-Object Path -eq $exe)
    if ($processes.Count -ne 1 -or !$processes[0].Responding) {throw 'Expected one responsive player process.'}
    $results.processes=@($processes | Select-Object Id,Responding,MainWindowTitle)
    Close-OwnedPlayer
    $journals=@(Get-ChildItem (Join-Path $data 'Logs') -Recurse -Filter '*.session.jsonl')
    $events=@($journals | ForEach-Object {Get-Content $_.FullName | ConvertFrom-Json})
    $results.mediaRequests=@($events | Where-Object action -eq 'media-requested').Count
    $results.loaded=@($events | Where-Object {$_.action -eq 'engine-event' -and $_.details.details.eventId -eq 8}).Count
    $results.closed=@($events | Where-Object action -eq 'session-closed').Count
    if($results.mediaRequests -ne 5 -or $results.loaded -ne 4 -or $results.closed -ne 5) {throw 'Unexpected playback lifecycle results.'}
    $savedHash=(Get-FileHash (Join-Path $data 'settings.json')).Hash
    Set-Content (Join-Path $target 'keep my file.txt') 'user file'
    if($upgradeInfo) {$setup=$upgradeSetup}
    Run-Setup 'upgrade' @('/LANG=en')
    if ((Get-ItemProperty $key).InstallDir -ne $target -or (Get-FileHash (Join-Path $data 'settings.json')).Hash -ne $savedHash) {throw 'Upgrade changed the path or data.'}
    if($upgradeInfo) {
        $results.upgradeVersion=(Get-ItemProperty $key).Version
        $results.upgradedExecutableVersion=(Get-Item $exe).VersionInfo.FileVersion
        if($results.upgradeVersion -ne $upgradeInfo.version -or $results.upgradedExecutableVersion -ne $upgradeInfo.version) {throw 'Newer application files were not installed.'}
    }
    Run-Setup 'move-rejected' @('/LANG=en',('/DIR="'+(Join-Path $output 'other')+'"')) $false
    # Registry version represents a later installed product, without touching its files.
    if($upgradeInfo) {
        $setup=$originalSetup
        Run-Setup 'downgrade-rejected' @('/LANG=en') $false
        if((Get-Item $exe).VersionInfo.FileVersion -ne $upgradeInfo.version) {throw 'Downgrade touched the new application.'}
    } else {
        Set-ItemProperty $key Version '999.0.0.0'
        try {Run-Setup 'downgrade-rejected' @('/LANG=en') $false} finally {Set-ItemProperty $key Version $info.version}
    }
    $results.upgradePreservedData=$true
    # Reopen the preserved Chinese profile, switch through the shared settings
    # path, then restore it. The probe is compiled out of regular builds.
    $languageProcess=Start-Process $exe -ArgumentList '--desktop-language-test' -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 6
    $languageReport=Get-Content (Join-Path $data 'language-test.json') -Raw | ConvertFrom-Json
    if($languageReport[0].open -ne 'Open' -or $languageReport[1].open -ne '打开' -or $languageReport[2].language -ne 'zh-CN') {throw 'Live interface language switch or restoration failed.'}
    $results.languageSwitch=$languageReport
    Close-OwnedPlayer
} finally {
    if (Test-Path -LiteralPath $exe) {Close-OwnedPlayer}
    $uninstaller=Join-Path $target 'unins000.exe'
    if(Test-Path -LiteralPath $uninstaller) {
        # Absolute target checked against this newly created evidence root.
        if (![IO.Path]::GetFullPath($uninstaller).StartsWith($output + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'Unsafe uninstall path.'}
        $process=Start-Process $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $output 'uninstall.log')+'"')) -WindowStyle Hidden -Wait -PassThru
        if($process.ExitCode) {throw 'Isolated uninstaller failed.'}
        Start-Sleep -Seconds 2
    }
    $results.uninstalled=!(Test-Path -LiteralPath $exe) -and !(Test-Path $key)
    $results.userFileRetained=(Test-Path -LiteralPath (Join-Path $target 'keep my file.txt'))
    $results.isolatedDataRetained=(Test-Path -LiteralPath (Join-Path $data 'settings.json'))
    $results.userDataUnchanged=(User-Hashes) -eq $userBefore
    $results.defaultsUnchanged=(Defaults) -eq $defaultsBefore
    $results.formalPackageUnchanged=(@(Get-AppxPackage -Name 'Mani.Beta.Test' | ForEach-Object PackageFullName) -join ',') -eq $packagesBefore
    $results.trustUnchanged=(@(Get-ChildItem Cert:/LocalMachine/TrustedPeople | ForEach-Object Thumbprint | Sort-Object) -join ',') -eq $trustBefore
    $results | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'verification.json') -Encoding utf8
}
foreach($name in @('uninstalled','userFileRetained','isolatedDataRetained','userDataUnchanged','defaultsUnchanged','formalPackageUnchanged','trustUnchanged')) {
    if(!$results[$name]) {throw "Final check failed: $name"}
}
Write-Output 'Desktop installation, Open with, playback, upgrade protection and uninstall checks passed.'
