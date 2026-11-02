param([Parameter(Mandatory=$true)][ValidateSet('baseline','patched')][string]$Variant)
$ErrorActionPreference='Stop'
$experimentRepo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$bash=Join-Path $experimentRepo 'artifacts/m5/sup-engine-ab/tools/msys64/usr/bin/bash.exe'
$scriptPath=(Join-Path $PSScriptRoot 'build-full-engine.sh').Replace('\','/')
& $bash --noprofile --norc $scriptPath $Variant
exit $LASTEXITCODE
