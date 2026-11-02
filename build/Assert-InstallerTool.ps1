param([Parameter(Mandatory)][string]$Compiler)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Resolve-Path -LiteralPath $Compiler).Path
$lock = Get-Content (Join-Path $PSScriptRoot '../config/installer-tool.lock.json') -Raw | ConvertFrom-Json
foreach ($file in $lock.compilerFiles) {
    $path = Join-Path $root $file.path
    if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $file.sha256) { throw "Pinned installer compiler file differs: $($file.path)" }
}
