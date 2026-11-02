$ErrorActionPreference = 'Stop'
$checked = 0
foreach ($line in Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([0-9a-fA-F]{64})  ([^\\/:]+)$') { throw 'Invalid checksum entry.' }
    $expected = $Matches[1]; $name = $Matches[2]
    $path = Join-Path $PSScriptRoot $name
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected) { throw "Checksum mismatch: $name" }
    $checked++
}
Write-Output "$checked files verified. No installation or certificate changes were performed."
