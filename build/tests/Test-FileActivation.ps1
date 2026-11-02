param([Parameter(Mandatory)][string]$Layout, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$layoutRoot = [IO.Path]::GetFullPath($Layout)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$assemblyBytes = [IO.File]::ReadAllBytes((Join-Path $layoutRoot 'Mani.App.dll'))
$isolationName = 'Mani.IsolatedFileActivationTests'
if (![Text.Encoding]::Unicode.GetString($assemblyBytes).Contains($isolationName) -and
    ![Text.Encoding]::Unicode.GetString($assemblyBytes, 1, $assemblyBytes.Length - 1).Contains($isolationName)) {
    throw 'Refusing to launch: publish with -p:IsolatedFileActivationTest=true first.'
}
if (Test-Path -LiteralPath $outputRoot) { throw 'Use a new evidence directory.' }
$manifestPath = Join-Path $layoutRoot 'AppxManifest.xml'
$manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$testName = 'Mani.FileTest.' + [Guid]::NewGuid().ToString('N')
if ($manifest.Package.Dependencies.PackageDependency) { throw 'Publish a self-contained layout first.' }
if (Get-AppxPackage -Name $testName) { throw 'Refusing to replace an existing package.' }
New-Item -ItemType Directory -Path $outputRoot | Out-Null
Add-Type -Path (Join-Path $PSScriptRoot 'FileActivationProbe.cs')
$extensions = @($manifest.Package.Applications.Application.Extensions.Extension.FileTypeAssociation.SupportedFileTypes.FileType)
function Read-Defaults {
    $values = [ordered]@{}
    foreach ($extension in $extensions) {
        $key = Get-Item -LiteralPath ("HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$extension\UserChoice") -ErrorAction SilentlyContinue
        $values[$extension] = if ($key) { @($key.GetValue('ProgId'), $key.GetValue('Hash')) } else { $null }
    }
    return ($values | ConvertTo-Json -Compress)
}
$defaultsBefore = Read-Defaults
$installedBefore = @(Get-AppxPackage -Name 'Mani.Beta.Test' | ForEach-Object PackageFullName)
$trustBefore = @(Get-ChildItem Cert:/LocalMachine/TrustedPeople | ForEach-Object Thumbprint | Sort-Object)
$userRoots = @((Join-Path $env:LOCALAPPDATA 'Mani'), (Join-Path $env:LOCALAPPDATA 'Packages/Mani.Beta.Test_h6anv01wm286t/LocalCache/Local/Mani'))
function User-Hashes {
    return @(foreach ($root in $userRoots) {
        if (Test-Path -LiteralPath $root) {
            Get-ChildItem -LiteralPath $root -File | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash
        }
    })
}
$userBefore = User-Hashes
$userBefore | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'user-hashes-before.json')
$backup = Join-Path $outputRoot 'user-backup'
New-Item -ItemType Directory -Path $backup | Out-Null
for ($i = 0; $i -lt $userBefore.Count; $i++) { Copy-Item -LiteralPath $userBefore[$i].Path -Destination (Join-Path $backup "$i.data") }
$registered = $false
$success = $false
$results = [ordered]@{}
$results.installedBefore = $installedBefore
try {
    $manifest.Package.Identity.Name = $testName
    $manifest.Package.Identity.Version = '0.5.8.0'
    $manifest.Save($manifestPath)
    Add-AppxPackage -Register $manifestPath
    $registered = $true
    $package = Get-AppxPackage -Name $testName
    $aumid = $package.PackageFamilyName + '!App'
    $data = Join-Path $layoutRoot $isolationName
    if (Test-Path -LiteralPath (Join-Path $data 'settings.json')) { throw 'Refusing to replace existing settings.' }
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    @{ schemaVersion=1; language='en-US'; volumePercent=25; muted=$true; pauseWhenOpen=$true;
       controlBarAutoHideTimeout=2.5; recordHistory=$false; resumePosition=$false; enableThumbnailPreview=$false;
       media=@{SubtitleMatching='no'} } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding utf8
    $results.package = $package.PackageFullName
    $results.handlers = @{}
    foreach ($extension in $extensions) {
        $handlers = [FileActivationProbe]::Handlers($extension)
        $own = @($handlers | Where-Object { $_[0] -like "*$($package.PackageFamilyName)*" -or $_[2].Contains($package.PackageFullName) -or $_[2] -like "$layoutRoot*" })
        if ($own.Count -eq 0) { throw "Shell did not enumerate the isolated Mani handler for $extension : $($handlers | ConvertTo-Json -Compress)" }
        $results.handlers[$extension] = $own
    }
    $sample = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../tests/media/generated/m1/1080p-sdr.mkv'))
    $first = Join-Path $outputRoot '第 1 集 %20 # &.mp4'
    $second = Join-Path $outputRoot '第 2 集.mkv'
    Copy-Item -LiteralPath $sample -Destination $first
    Copy-Item -LiteralPath $sample -Destination $second
    $bad = Join-Path $outputRoot '损坏文件.mp4'
    Set-Content -LiteralPath $bad -Value 'Not a media file.' -Encoding utf8
    $results.sampleSha256 = (Get-FileHash -LiteralPath $sample -Algorithm SHA256).Hash
    # Invoke the registered full-trust Open with handler without changing defaults.
    [FileActivationProbe]::OpenWith($package.PackageFullName, @($first))
    Start-Sleep -Seconds 10
    $results.redirectedProcess = [FileActivationProbe]::Activate($aumid, ('"' + $first + '" "' + $second + '"'))
    Start-Sleep -Seconds 4
    [FileActivationProbe]::OpenWith($package.PackageFullName, @($bad))
    Start-Sleep -Seconds 4
    [FileActivationProbe]::OpenWith($package.PackageFullName, @($first))
    1..3 | ForEach-Object {
        $rapidFile = Join-Path $outputRoot "rapid-$_.mkv"
        Copy-Item -LiteralPath $sample -Destination $rapidFile
        [FileActivationProbe]::OpenWith($package.PackageFullName, @($rapidFile))
    }
    Start-Sleep -Seconds 8
    $processes = @(Get-Process -Name 'Mani.App' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $layoutRoot 'Mani.App.exe') })
    $results.runningProcesses = @($processes | Select-Object Id,MainWindowTitle,Responding)
    if ($processes.Count -ne 1 -or !$processes[0].Responding) { throw 'Repeated activation did not leave exactly one responsive process.' }
    Get-ChildItem -LiteralPath $data -Recurse -File |
        Select-Object FullName,Length | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'data-files.json')
    $journals = @(Get-ChildItem -LiteralPath (Join-Path $data 'Logs') -Recurse -File | Where-Object Name -Like '*.session.jsonl')
    $events = @($journals | ForEach-Object { Get-Content -LiteralPath $_.FullName | ForEach-Object { $_ | ConvertFrom-Json } })
    $results.mediaRequests = @($events | Where-Object action -eq 'media-requested').Count
    $results.fileLoaded = @($events | Where-Object { $_.action -eq 'engine-event' -and $_.details.details.eventId -eq 8 }).Count
    $results.loadFailures = @($events | Where-Object { $_.action -eq 'engine-event' -and $_.details.details.code -lt 0 }).Count
    $results.loadedPerWindow = @($journals | ForEach-Object {
        @(Get-Content -LiteralPath $_.FullName | ConvertFrom-Json | Where-Object { $_.action -eq 'engine-event' -and $_.details.details.eventId -eq 8 }).Count
    })
    if (@($results.loadedPerWindow | Where-Object { $_ -gt 1 }).Count) { throw 'A rapid request replaced media in an already occupied window.' }
    if ($results.mediaRequests -ne 7 -or $results.fileLoaded -ne 6 -or $results.loadFailures -lt 1) {
        throw "Unexpected actual engine results: requests=$($results.mediaRequests), loaded=$($results.fileLoaded), failed=$($results.loadFailures)"
    }
    $success = $true
} finally {
    if ($registered) {
        $owned = Get-AppxPackage -Name $testName
        if ($owned -and $owned.Name -eq $testName -and $owned.IsDevelopmentMode) { Remove-AppxPackage -Package $owned.PackageFullName }
        elseif ($owned) { throw 'Unexpected identity: test package was not removed.' }
    }
    [IO.File]::WriteAllBytes($manifestPath, $manifestBytes)
    $results.passed = $success
    $results.defaultsUnchanged = (Read-Defaults) -eq $defaultsBefore
    $results.installedPackageUnchanged = ($installedBefore -join ',') -eq (@(Get-AppxPackage -Name 'Mani.Beta.Test' | ForEach-Object PackageFullName) -join ',')
    $results.installedAfter = @(Get-AppxPackage -Name 'Mani.Beta.Test' | ForEach-Object PackageFullName)
    $results.trustUnchanged = ($trustBefore -join ',') -eq (@(Get-ChildItem Cert:/LocalMachine/TrustedPeople | ForEach-Object Thumbprint | Sort-Object) -join ',')
    $results.testPackageRemoved = !(Get-AppxPackage -Name $testName)
    $results.userDataUnchanged = (ConvertTo-Json -InputObject $userBefore -Compress) -eq (ConvertTo-Json -InputObject @(User-Hashes) -Compress)
    if ($data -and (Test-Path -LiteralPath $data)) {
        $resolvedData = [IO.Path]::GetFullPath($data)
        $savedData = [IO.Path]::GetFullPath((Join-Path $outputRoot 'isolated-data'))
        if (!$resolvedData.StartsWith($layoutRoot + [IO.Path]::DirectorySeparatorChar) -or !$savedData.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar)) { throw 'Data evidence move escaped test roots.' }
        Move-Item -LiteralPath $resolvedData -Destination $savedData
    }
    $results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'verification.json') -Encoding utf8
}
if (!$results.defaultsUnchanged -or !$results.installedPackageUnchanged -or !$results.trustUnchanged -or !$results.testPackageRemoved -or !$results.userDataUnchanged) { throw 'Host preservation or cleanup failed.' }
Write-Output 'PASS: Shell associations, real file activation, repeated launch, damaged-file recovery, and host preservation.'
