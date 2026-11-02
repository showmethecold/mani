# Embedded in Mani Setup. Windows owns package deployment, upgrades and removal.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
try {
    $mode = $env:MANI_SETUP_MODE
    if ($mode -notin @('inspect', 'validate', 'install')) { throw 'Unknown installer operation.' }
    $name = $env:MANI_SETUP_NAME
    $publisher = $env:MANI_SETUP_PUBLISHER
    $version = [version]$env:MANI_SETUP_VERSION
    $installed = @(Get-AppxPackage -Name $name | Where-Object { $_.Name -eq $name -and $_.Publisher -eq $publisher } | Sort-Object Version -Descending)
    $current = if ($installed.Count) { $installed[0] } else { $null }
    if ($mode -eq 'inspect') {
        $state = 'missing'
        if ($current -and [version]$current.Version -gt $version) {
            $state = 'newer'
        } elseif ($current -and [version]$current.Version -eq $version -and $current.Status -eq 'Ok') {
            $state = 'installed'
        } elseif ($current) { $state = 'upgrade' }
        @{ ok=$true; state=$state; version=if ($current) { $current.Version.ToString() } else { '' }; family=if ($current) { $current.PackageFamilyName } else { '' } } | ConvertTo-Json -Compress
        exit 0
    }
    $package = $env:MANI_SETUP_PACKAGE
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $env:MANI_SETUP_SHA256) { throw 'Package checksum mismatch.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entry = $archive.GetEntry('AppxManifest.xml')
        if (!$entry) { throw 'Missing package manifest.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $identity = $manifest.Package.Identity
        if ($identity.Name -ne $name -or $identity.Publisher -ne $publisher -or
            [version]$identity.Version -ne $version -or $identity.ProcessorArchitecture -ne 'x64') { throw 'Package identity mismatch.' }
    } finally { $archive.Dispose() }
    $signature = Get-AuthenticodeSignature -LiteralPath $package
    if (!$signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $env:MANI_SETUP_THUMBPRINT -or
        $signature.Status -eq 'HashMismatch' -or $signature.Status -eq 'NotSigned') { throw 'Package signature mismatch.' }
    if ($mode -eq 'validate') {
        @{ ok=$true; state='validated'; version=$version.ToString(); message=$signature.Status.ToString() } | ConvertTo-Json -Compress
        exit 0
    }
    # Re-read immediately before deployment. Never downgrade or uninstall existing data.
    $current = Get-AppxPackage -Name $name | Where-Object { $_.Name -eq $name -and $_.Publisher -eq $publisher } | Sort-Object Version -Descending | Select-Object -First 1
    if ($current -and [version]$current.Version -gt $version) {
        @{ ok=$true; state='newer'; version=$current.Version.ToString(); family=$current.PackageFamilyName } | ConvertTo-Json -Compress
        exit 0
    }
    if ($signature.Status -ne 'Valid') { throw ('Windows does not trust this package signature: ' + $signature.StatusMessage) }
    # No elevation, ForceApplicationShutdown, ForceUpdateFromAnyVersion, or uninstall.
    Add-AppxPackage -Path $package -ErrorAction Stop
    $verified = Get-AppxPackage -Name $name | Where-Object { $_.Name -eq $name -and $_.Publisher -eq $publisher } | Sort-Object Version -Descending | Select-Object -First 1
    if (!$verified -or [version]$verified.Version -lt $version -or $verified.Status -ne 'Ok') { throw 'Windows did not confirm a healthy installation.' }
    @{ ok=$true; state='installed'; version=$verified.Version.ToString(); family=$verified.PackageFamilyName } | ConvertTo-Json -Compress
    exit 0
} catch {
    @{ ok=$false; state='failed'; code=('0x{0:X8}' -f $_.Exception.HResult); message=$_.Exception.Message } | ConvertTo-Json -Compress
    exit 1
}
