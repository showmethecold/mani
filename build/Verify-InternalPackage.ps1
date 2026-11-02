param([string]$OutputDirectory, [string]$PackageName = 'Mani.Internal')
$ErrorActionPreference = 'Stop'
if (!$OutputDirectory) { throw 'Supply a dedicated output directory for this verification run.' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw 'Use a new verification output directory.' }
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$package = Get-AppxPackage -Name $PackageName
if (!$package) { throw 'Install or register the internal package before verification.' }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ManiPackageActivation {
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    public static uint ExitCode(IntPtr process) {
        uint code;
        if (!GetExitCodeProcess(process, out code)) throw new System.ComponentModel.Win32Exception();
        return code;
    }
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivation {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string app,
            [MarshalAs(UnmanagedType.LPWStr)] string args, uint options, out uint processId);
    }
    public static uint Launch(string app, string args) {
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")));
        try {
            uint id;
            Marshal.ThrowExceptionForHR(((IActivation)instance).ActivateApplication(app, args, 0, out id));
            return id;
        } finally { Marshal.ReleaseComObject(instance); }
    }
}
'@
$processId = [ManiPackageActivation]::Launch($package.PackageFamilyName + '!App', '--m2-verify="' + $outputRoot + '"')
$process = [Diagnostics.Process]::GetProcessById($processId)
$processHandle = $process.Handle
if (!$process.WaitForExit(55000)) { throw "Verification timed out; inspect or close Mani process $processId." }
$exitCode = [ManiPackageActivation]::ExitCode($processHandle)
$process.Dispose()
$report = Get-Content -LiteralPath (Join-Path $outputRoot 'window-results.json') -Raw | ConvertFrom-Json
$logName = Split-Path -Leaf $report.sessionLog
$logRoot = Join-Path $env:LOCALAPPDATA ('Packages/' + $package.PackageFamilyName + '/LocalCache/Local/Mani/Logs/' + $logName)
if (!(Test-Path -LiteralPath $logRoot)) { $logRoot = $report.sessionLog }
$evidenceRoot = Join-Path $outputRoot 'session-logs'
New-Item -ItemType Directory -Path $evidenceRoot | Out-Null
foreach ($name in @('mpv.log', 'mpv.log.session.jsonl', 'host-state.jsonl', 'host-errors.txt')) {
    $source = Join-Path $logRoot $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $evidenceRoot $name) }
}
$coreLog = Get-Content -LiteralPath (Join-Path $evidenceRoot 'mpv.log.session.jsonl') | ForEach-Object { $_ | ConvertFrom-Json }
$coreClosed = $coreLog[-1].action -eq 'session-closed'
@{ package = $package.PackageFullName; processId = $processId; exitCode = $exitCode;
    report = $report; coreClosed = $coreClosed; completed = ($report.completed -and $exitCode -eq 0 -and $coreClosed) } |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $outputRoot 'verification.json') -Encoding utf8
if (!$report.completed -or $exitCode -ne 0 -or !$coreClosed) { throw 'Window checks or normal process shutdown failed. Inspect verification.json.' }
Write-Output "M2 window checks and normal shutdown passed: $outputRoot"
