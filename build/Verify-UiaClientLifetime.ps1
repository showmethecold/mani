#requires -Version 7.0
# UIA clients must be separate processes: their shutdown is the regression trigger.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$AppProcessId,
    [string]$MediaPath = (Join-Path $PSScriptRoot '../tests/media/generated/m1/1080p-sdr.mkv'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot ('../artifacts/uia-lifetime-' + (Get-Date -Format yyyyMMdd-HHmmss))),
    [ValidateRange(1,10)][int]$Cycles = 3,
    [switch]$CloseWindow,
    [ValidateSet('Exercise','Reconnect')][string]$ClientPhase
)
$ErrorActionPreference = 'Stop'
if ($ClientPhase) {
    # Initialize the Windows desktop assemblies before compiling native helpers,
    # so standard controls (including the system picker) expose their UIA patterns.
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName System.Drawing
    Add-Type -AssemblyName System.Windows.Forms
}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class UiaLifetimeNative {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr process, out uint code);
}
'@
$app = Get-Process -Id $AppProcessId
if ($app.ProcessName -ne 'Mani.App') { throw 'The target must be a Mani.App process.' }
$handle = $app.Handle
$hwnd = $app.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { throw 'The target has no main window.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

if ($ClientPhase) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NativeWindowHandleProperty,$hwnd.ToInt32()))
    if(!$root){throw 'Main window is not available to UIA.'}
    function FindControl([string]$Id) {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)
        $deadline = [DateTime]::UtcNow.AddSeconds(12)
        do {
            $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
            if ($element -and !$element.Current.IsOffscreen) { return $element }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        throw "Visible control not found: $Id"
    }
    function Key([byte]$Value,[switch]$Control) {
        if ([UiaLifetimeNative]::GetAncestor([UiaLifetimeNative]::GetForegroundWindow(),2) -ne $hwnd) { throw 'Target lost foreground; input was not sent.' }
        if ($Control) { [UiaLifetimeNative]::keybd_event(17,0,0,[UIntPtr]::Zero) }
        [UiaLifetimeNative]::keybd_event($Value,0,0,[UIntPtr]::Zero)
        [UiaLifetimeNative]::keybd_event($Value,0,2,[UIntPtr]::Zero)
        if ($Control) { [UiaLifetimeNative]::keybd_event(17,0,2,[UIntPtr]::Zero) }
        Start-Sleep -Milliseconds 200
    }
    function InvokeControl([string]$Id) {
        (FindControl $Id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 250
    }
    function WakeControls {
        $rect = (FindControl 'VideoFocus').Current.BoundingRectangle
        # Cross two distinct video points: repeated stationary/native pointer
        # updates intentionally do not reset the player's auto-hide timer.
        [void][UiaLifetimeNative]::SetCursorPos([int]($rect.X+$rect.Width*.8),[int]($rect.Y+$rect.Height*.3))
        [UiaLifetimeNative]::mouse_event(1,1,0,0,[UIntPtr]::Zero)
        Start-Sleep -Milliseconds 100
        [void][UiaLifetimeNative]::SetCursorPos([int]($rect.X+50),[int]($rect.Y+$rect.Height/2))
        [UiaLifetimeNative]::mouse_event(1,1,0,0,[UIntPtr]::Zero)
        Start-Sleep -Milliseconds 250
    }
    $menuFocusChecks = 0
    function VerifyMenuSpace {
        # Physical More click reproduces WinUI restoring focus to the flyout's
        # anchor. UIA Invoke can bypass the pointer focus transition.
        $more = (FindControl 'MoreButton').Current.BoundingRectangle
        $x = [int]($more.X + $more.Width/2); $y = [int]($more.Y + $more.Height/2)
        $hit = [System.Windows.Automation.AutomationElement]::FromPoint([System.Windows.Point]::new($x,$y))
        if ($hit.Current.ProcessId -ne $AppProcessId) { throw 'More is covered; no input was sent.' }
        [void][UiaLifetimeNative]::SetCursorPos($x,$y)
        [UiaLifetimeNative]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
        [UiaLifetimeNative]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
        Start-Sleep -Milliseconds 250
        if ([System.Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -ne 'FileMenu') {
            throw 'More did not open with its native menu focus.'
        }
        Key 27
        Start-Sleep -Milliseconds 250
        if ([System.Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -ne 'VideoFocus') {
            throw 'Closing More did not return focus to the player.'
        }
        $before = (FindControl 'PlayButton').Current.Name
        foreach ($press in 1..2) {
            Key 32; Start-Sleep -Milliseconds 350
            $after = (FindControl 'PlayButton').Current.Name
            if (($press -eq 1 -and $after -eq $before) -or ($press -eq 2 -and $after -ne $before) -or
                [System.Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -ne 'VideoFocus') {
                throw "Space after More failed on press $press."
            }
            $script:menuFocusChecks++
        }
    }
    function SaveTree([string]$Name) {
        # Include hidden elements and native caption providers; filtering them out hides this failure.
        $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
            @{name=$_.Current.Name; id=$_.Current.AutomationId; type=$_.Current.ControlType.ProgrammaticName;
              offscreen=$_.Current.IsOffscreen; enabled=$_.Current.IsEnabled; rect=$_.Current.BoundingRectangle.ToString(); hwnd=$_.Current.NativeWindowHandle}
        } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory "$Name.json")
    }
    [void][UiaLifetimeNative]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 250
    if ([UiaLifetimeNative]::GetAncestor([UiaLifetimeNative]::GetForegroundWindow(),2) -ne $hwnd) {
        $rect=$root.Current.BoundingRectangle; $x=[int]($rect.X+150); $y=[int]($rect.Y+24)
        $hit=[System.Windows.Automation.AutomationElement]::FromPoint([System.Windows.Point]::new($x,$y))
        if ($hit.Current.ProcessId -ne $AppProcessId) { throw 'Target is covered; no input was sent.' }
        [void][UiaLifetimeNative]::SetCursorPos($x,$y)
        [UiaLifetimeNative]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
        [UiaLifetimeNative]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
        Start-Sleep -Milliseconds 250
    }
    if ($ClientPhase -eq 'Exercise') {
        $MediaPath=(Resolve-Path -LiteralPath $MediaPath).Path
        Key 79 -Control
        # Search the process roots: the system picker can be hosted in the owner window.
        $deadline=[DateTime]::UtcNow.AddSeconds(12); $edit=$null
        do {
            $windows=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$AppProcessId))
            foreach($window in $windows) {
                $box=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'1148'))
                if($box -and !$box.Current.IsOffscreen) {
                    $edit=$box.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit))
                    if(!$edit){$edit=$box}
                    $valuePattern=$null
                    if(!$edit.Current.IsOffscreen -and $edit.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern,[ref]$valuePattern)){break}
                    $edit=$null
                }
            }
            if(!$edit){Start-Sleep -Milliseconds 100}
        } while (!$edit -and [DateTime]::UtcNow -lt $deadline)
        if(!$edit){SaveTree 'picker-failure';throw 'File picker was not found.'}
        $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($MediaPath);$edit.SetFocus()
        # Confirm the picker owns the foreground before sending Enter.
        $foreground=[System.Windows.Automation.AutomationElement]::FromHandle([UiaLifetimeNative]::GetForegroundWindow())
        if($foreground.Current.ProcessId -ne $AppProcessId){throw 'Picker lost foreground.'}
        [UiaLifetimeNative]::keybd_event(13,0,0,[UIntPtr]::Zero);[UiaLifetimeNative]::keybd_event(13,0,2,[UIntPtr]::Zero)
        Start-Sleep -Milliseconds 1600;WakeControls
        if((FindControl 'PlayButton').Current.Name -eq 'Pause'){InvokeControl 'PlayButton'}
        foreach($cycle in 1..$Cycles) {
            foreach($size in @(@(2240,1260),@(1680,945),@(1400,1050),@(560,455))) {
                [void][UiaLifetimeNative]::MoveWindow($hwnd,40,40,$size[0],$size[1],$true)
                Start-Sleep -Milliseconds 350;WakeControls
                VerifyMenuSpace
                SaveTree "cycle-$cycle-width-$($size[0])"
            }
            Key 122;Start-Sleep -Milliseconds 600;SaveTree "fullscreen-$cycle"
            Key 27;Start-Sleep -Milliseconds 600;WakeControls;SaveTree "restored-$cycle"
        }
    } else {
        WakeControls
        $before=(FindControl 'PlayButton').Current.Name
        InvokeControl 'PlayButton';Start-Sleep -Milliseconds 350
        if((FindControl 'PlayButton').Current.Name -eq $before){throw 'Reconnected client could not change playback state.'}
        InvokeControl 'PlayButton';Start-Sleep -Milliseconds 350
        if((FindControl 'PlayButton').Current.Name -ne $before){throw 'Playback state was not restored.'}
        SaveTree 'reconnected'
    }
    @{phase=$ClientPhase;clientPid=$PID;appPid=$AppProcessId;menuFocusChecks=$menuFocusChecks;completedAt=[DateTimeOffset]::Now.ToString('O')} |
        ConvertTo-Json | Set-Content (Join-Path $OutputDirectory "$ClientPhase-completed.json")
    exit 0
}

$result=[ordered]@{appPid=$AppProcessId;startedAt=[DateTimeOffset]::Now.ToString('O');passed=$false;
    appHash=(Get-FileHash (Join-Path (Split-Path $app.Path) 'Mani.App.dll')).Hash;clients=@()}
try {
    foreach($phase in 'Exercise','Reconnect') {
        $start=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        $start.UseShellExecute=$false;$start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
        foreach($arg in @('-NoProfile','-STA','-File',$PSCommandPath,'-AppProcessId',"$AppProcessId",'-MediaPath',$MediaPath,
            '-OutputDirectory',$OutputDirectory,'-Cycles',"$Cycles",'-ClientPhase',$phase)){$start.ArgumentList.Add($arg)}
        $client=[Diagnostics.Process]::Start($start)
        $stdout=$client.StandardOutput.ReadToEndAsync();$stderr=$client.StandardError.ReadToEndAsync()
        if(!$client.WaitForExit(180000)){$client.Kill($true);throw "UIA client timed out: $phase"}
        $stdout.GetAwaiter().GetResult()|Set-Content (Join-Path $OutputDirectory "$phase.stdout.txt")
        $stderr.GetAwaiter().GetResult()|Set-Content (Join-Path $OutputDirectory "$phase.stderr.txt")
        $clientExit=$client.ExitCode;$client.Dispose()
        $exitAt=[DateTimeOffset]::Now.ToString('O')
        $appExited=$app.WaitForExit(10000);[uint32]$code=0;[void][UiaLifetimeNative]::GetExitCodeProcess($handle,[ref]$code)
        $result.clients+=@{phase=$phase;clientExit=$clientExit;clientExitedAt=$exitAt;appExited=$appExited;appExit=if($appExited){$code}else{$null}}
        if($clientExit -ne 0 -or $appExited){throw "UIA client lifetime check failed: $phase"}
    }
    if($CloseWindow) {
        [void][UiaLifetimeNative]::PostMessageW($hwnd,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
        if(!$app.WaitForExit(15000)){throw 'Normal window close timed out.'}
        [uint32]$code=0;[void][UiaLifetimeNative]::GetExitCodeProcess($handle,[ref]$code)
        $result.normalClose=$code
        if($code -ne 0){throw "Application exited abnormally: $code"}
    }
    $result.passed=$true
} catch { $result.failure=$_.Exception.Message; throw }
finally {
    $result.finishedAt=[DateTimeOffset]::Now.ToString('O')
    $result|ConvertTo-Json -Depth 6|Set-Content (Join-Path $OutputDirectory 'result.json')
    $app.Dispose()
}
