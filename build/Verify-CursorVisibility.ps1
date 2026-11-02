param(
    [Parameter(Mandatory)][int]$AppProcessId,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$MediaPath,
    [switch]$ClientPhase,
    [string]$Action = 'Rect', [string]$Id, [string]$Name, [string]$Value,
    [string]$DxgiProbe,
    [switch]$PickerViaUia,
    [switch]$ExtendedOnly,
    [switch]$DelayOnly,
    [switch]$DelaySweep,
    [switch]$SkipDelay,
    [switch]$FirstOnly,
    [double[]]$Delays = @(0.5, 1, 2.5, 1, 2.5),
    [switch]$ContinueOnCursorFailure
)
$ErrorActionPreference = 'Stop'
if (!$Delays.Count -or @($Delays | Where-Object { ![double]::IsFinite($_) -or $_ -le 0 -or $_ -gt 30 }).Count) {
    throw 'Actual-window checks require positive hide delays between 0 and 30 seconds.'
}
$app = Get-Process -Id $AppProcessId
if ($app.ProcessName -ne 'Mani.App') { throw 'Only a Mani.App window may be targeted.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory $OutputDirectory -Force | Out-Null
if ($ClientPhase) {
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    $roots = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$AppProcessId))
    $property = if ($Id) { [System.Windows.Automation.AutomationElement]::AutomationIdProperty } else { [System.Windows.Automation.AutomationElement]::NameProperty }
    $wanted = if ($Id) { $Id } else { $Name }
    if ($Action -eq 'FocusInfo') {
        $f=[System.Windows.Automation.AutomationElement]::FocusedElement
        @{id=$f.Current.AutomationId;name=$f.Current.Name;processId=$f.Current.ProcessId}|ConvertTo-Json -Compress
        exit
    }
    $element=$null
    foreach($root in $roots) {
        foreach($candidate in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new($property,$wanted))) {
            if(!$candidate.Current.IsOffscreen){$element=$candidate;break}
        }
        if($element){break}
    }
    if(!$element){throw "Visible control not found: $wanted"}
    switch($Action) {
        {$_ -in 'Rect','EditRect'} { if($Action -eq 'EditRect'){$element=$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit))};$r=$element.Current.BoundingRectangle; @{x=$r.X;y=$r.Y;width=$r.Width;height=$r.Height}|ConvertTo-Json -Compress }
        Invoke { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); '{}' }
        Expand { $element.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); '{}' }
        Select { $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); '{}' }
        Focus { $element.SetFocus(); '{}' }
        Toggle { $p=$element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern);if(($p.Current.ToggleState -eq 'On') -ne ($Value -eq 'true')){$p.Toggle()}; '{}' }
        {$_ -in 'Value','ValueAndFocus'} { $edit=$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit));if(!$edit){$edit=$element};$edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value);if($Action -eq 'ValueAndFocus'){$edit.SetFocus()}; '{}' }
        default { throw "Unknown action $Action" }
    }
    exit
}
# UIA and pointer input deliberately use different processes. A live client
# querying native picker controls can change the observed Windows cursor even
# when XAML has applied its override. Dispose the client before native input.
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Path "$PSScriptRoot/CursorTestNative.cs" -ReferencedAssemblies System.Drawing.Common,System.Drawing.Primitives,System.Runtime.InteropServices,System.Runtime,System.ComponentModel.Primitives,System.Private.Windows.GdiPlus,System.Private.Windows.Core
$script:testHwnd=$app.MainWindowHandle
$script:records=[Collections.Generic.List[object]]::new()
$script:motion=0
function Save { $script:records|ConvertTo-Json -Depth 8|Set-Content "$OutputDirectory/checks.json" }
function Check([bool]$Passed,[string]$Label,$Evidence=$null,[bool]$ContinueFailure=$false) {
    $script:records.Add(@{name=$Label;passed=$Passed;at=[DateTimeOffset]::Now.ToString('O');evidence=$Evidence});Save
    if(!$Passed -and !$ContinueFailure){throw $Label};Write-Host $Label
}
function UI([string]$Action,[string]$Id='', [string]$Name='', [string]$Value='') {
    $result=& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -STA -File $PSCommandPath -AppProcessId $AppProcessId -OutputDirectory $OutputDirectory -ClientPhase -Action $Action -Id $Id -Name $Name -Value $Value
    if($LASTEXITCODE -ne 0){throw "UIA worker failed: $Action $Id $Name"}
    $result|ConvertFrom-Json
}
function Key([ushort]$Code,[ushort[]]$Modifiers=@()) { [CursorTestNative]::KeyPress($Code,$Modifiers,$AppProcessId);Start-Sleep -Milliseconds 180 }
function MovePointer([int]$X,[int]$Y) {
    [CursorTestNative]::MouseMove($X,$Y,$AppProcessId)
    Start-Sleep -Milliseconds 120
}
function MoveVideo {
    $r=[CursorTestNative+Rect]::new();[void][CursorTestNative]::GetWindowRect($script:testHwnd,[ref]$r)
    $script:motion=1-$script:motion
    $x=[int]($r.Left+($r.Right-$r.Left)*.31+$script:motion*7);$y=[int]($r.Top+($r.Bottom-$r.Top)*.29)
    $info=[CursorTestNative+CursorInfo]::new();$info.Size=[Runtime.InteropServices.Marshal]::SizeOf($info)
    if(![CursorTestNative]::GetCursorInfo([ref]$info)){throw 'Cannot read pointer before motion'}
    if([Math]::Abs($info.Position.X-$x) -lt 4 -and [Math]::Abs($info.Position.Y-$y) -lt 4){$x+=14}
    MovePointer $x $y
}
function MoveControl([string]$Id,[string]$Name='') { $r=UI Rect $Id $Name;MovePointer ([int]($r.x+$r.width*.5)) ([int]($r.y+$r.height*.5)) }
function ClickControl([string]$Id,[string]$Name='') { MoveControl $Id $Name;[CursorTestNative]::MouseEvent(2,$AppProcessId);[CursorTestNative]::MouseEvent(4,$AppProcessId);Start-Sleep -Milliseconds 300 }
function Cursor([bool]$Visible,[string]$Label) {
    $info=[CursorTestNative+CursorInfo]::new();$info.Size=[Runtime.InteropServices.Marshal]::SizeOf($info)
    if(![CursorTestNative]::GetCursorInfo([ref]$info)){throw 'GetCursorInfo failed'}
    $foreground=[CursorTestNative]::GetForegroundWindow();[uint32]$foregroundProcess=0;[uint32]$pointerProcess=0
    [void][CursorTestNative]::GetWindowThreadProcessId($foreground,[ref]$foregroundProcess)
    [void][CursorTestNative]::GetWindowThreadProcessId([CursorTestNative]::WindowFromPoint($info.Position),[ref]$pointerProcess)
    Check ($foregroundProcess -eq $AppProcessId -and $pointerProcess -eq $AppProcessId) ($Label+'-unobstructed') @{foregroundProcess=$foregroundProcess;pointerProcess=$pointerProcess}
    $pixels=[CursorTestNative]::HasPixels($info)
    Check ($pixels -eq $Visible) $Label @{pixels=$pixels;flags=$info.Flags;cursor=$info.Cursor.ToInt64();x=$info.Position.X;y=$info.Position.Y} $ContinueOnCursorFailure.IsPresent
}
function DesktopCursorHidden([string]$Label) {
    if (!$DxgiProbe) { return }
    $output=& $DxgiProbe "$OutputDirectory/$Label.bmp" 2> "$OutputDirectory/$Label-window.txt"
    $output|Set-Content "$OutputDirectory/$Label.json"
    $result=$output|ConvertFrom-Json
    Check ($LASTEXITCODE -eq 0 -and $result.shapeBytes -gt 0 -and !$result.pixels) ($Label+'-dxgi-no-pointer-pixels') $result $ContinueOnCursorFailure.IsPresent
}
function Idle([string]$Label) { MoveVideo;Start-Sleep -Milliseconds 3100;Cursor $false $Label }
function Menu { MoveVideo;MoveControl 'MoreButton';ClickControl 'MoreButton' }
function Invoke([string]$Id) {
    if($Id -eq 'LayoutMenu'){Expand 'Video'}
    $null=UI Invoke $Id;Start-Sleep -Milliseconds 350
    # More > Settings now opens General; visibility preferences are on Interface.
    if($Id -eq 'InterfaceMenu'){$null=UI Select '' 'Interface';Start-Sleep -Milliseconds 350}
}
function Expand([string]$Name) {
    if($Name -eq 'Control bar'){Expand 'Video'}
    $null=UI Expand '' $Name;Start-Sleep -Milliseconds 400
}
function NumberInput([string]$Id,[string]$Value) {
    $r=UI EditRect $Id;MovePointer ([int]($r.x+$r.width*.4)) ([int]($r.y+$r.height*.5))
    [CursorTestNative]::MouseEvent(2,$AppProcessId);[CursorTestNative]::MouseEvent(4,$AppProcessId)
    Key 65 @(17);[CursorTestNative]::Text($Value,$AppProcessId);Key 9
}
function KeyboardMenu {
    # Establish video keyboard focus before injecting the accelerator. Do not
    # race back-to-back pointer down/up with the next keyboard input batch.
    $null=UI Invoke 'VideoFocus'
    Key 121 @(16)
}
[void][CursorTestNative]::SetForegroundWindow($script:testHwnd)
[CursorTestNative]::RequireForeground($AppProcessId)
if($DelaySweep) {
    $iteration=0
    foreach($delay in $Delays) {
        $iteration++
        $delayText=$delay.ToString([Globalization.CultureInfo]::InvariantCulture)
        KeyboardMenu;Invoke 'InterfaceMenu';NumberInput 'ControlHideDelay' $delayText;ClickControl '' 'Done';MoveVideo
        Cursor $true ("delay-$iteration-$delayText-first-motion-visible")
        Start-Sleep -Milliseconds ([int]([double]$delay*1000+500))
        Cursor $false ("delay-$iteration-$delayText-hidden")
        DesktopCursorHidden "delay-$iteration-$delayText"
    }
    if(@($script:records|Where-Object {!$_.passed}).Count){exit 1};exit
}
if(!$ExtendedOnly -and !$DelayOnly) {
if($MediaPath) {
    Key 79 @(17);Start-Sleep -Milliseconds 700
    Key 78 @(18);Key 65 @(17)
    if($PickerViaUia) { $null=UI ValueAndFocus '1148' '' ([IO.Path]::GetFullPath($MediaPath)) }
    else { [CursorTestNative]::Text([IO.Path]::GetFullPath($MediaPath),$AppProcessId) }
    Start-Sleep -Milliseconds 200;Key 13;Start-Sleep -Milliseconds 1400
    Check ([CursorTestNative]::GetForegroundWindow() -eq $script:testHwnd) 'native-file-picker-closed'
}
MoveVideo
Cursor $true 'first-entry-visible-before-deadline'
Start-Sleep -Milliseconds 3100
Cursor $false 'first-entry-hidden-without-reentry'
DesktopCursorHidden 'first-pointer'
if($FirstOnly){if(@($script:records|Where-Object {!$_.passed}).Count){exit 1};exit}
$r=[CursorTestNative+Rect]::new();[void][CursorTestNative]::GetWindowRect($script:testHwnd,[ref]$r)
MovePointer ($r.Left+400) ($r.Top+260);Cursor $true 'motion-restores-pointer'
Idle 'second-idle-hides'
# Repeated pause/resume must preserve hidden-until-motion, as in IINA.
foreach($cycle in 1..4) {
    [CursorTestNative]::MouseEvent(8,$AppProcessId);[CursorTestNative]::MouseEvent(16,$AppProcessId);Start-Sleep -Milliseconds 350
    Cursor $false "stationary-right-click-$cycle-preserves-hidden-pointer"
    DesktopCursorHidden "stationary-right-click-$cycle"
}
MoveVideo;Cursor $true 'motion-after-secondary-click-restores-pointer'
[CursorTestNative]::MouseEvent(8,$AppProcessId);[CursorTestNative]::MouseEvent(16,$AppProcessId);Start-Sleep -Milliseconds 350
Cursor $true 'visible-secondary-click-does-not-hide-early'
MovePointer ($r.Left+400) ($r.Top+260)
[CursorTestNative]::MouseEvent(2,$AppProcessId);[CursorTestNative]::MouseEvent(4,$AppProcessId);Start-Sleep -Milliseconds 650
Cursor $false 'single-click-hides-before-timeout'
Key 9;Cursor $true 'tab-recalls-pointer'
MoveVideo;Key 27
MoveControl 'PlayButton';Start-Sleep -Milliseconds 3100;Cursor $true 'control-hover-retains-pointer'
Menu;MoveVideo;Start-Sleep -Milliseconds 3100;Cursor $true 'menu-retains-pointer';Key 27
Key 32;Key 32
Idle 'menu-close-space-does-not-reopen-menu'
Key 122;Start-Sleep -Milliseconds 450;Idle 'fullscreen-idle-hides'
Key 27;Start-Sleep -Milliseconds 450;Idle 'fullscreen-return-hides'
MoveVideo;[CursorTestNative]::MouseEvent(2,$AppProcessId)
try {Start-Sleep -Milliseconds 3100;Cursor $true 'held-left-button-retains-pointer'}
finally {[CursorTestNative]::MouseEvent(4,$AppProcessId)}
Start-Sleep -Milliseconds 650
$r=[CursorTestNative+Rect]::new();[void][CursorTestNative]::GetWindowRect($script:testHwnd,[ref]$r)
MovePointer ($r.Left+250) ($r.Top+25);Cursor $true 'native-titlebar-restores-pointer'
Idle 'titlebar-return-hides'
Key 77 @(17,16);Start-Sleep -Milliseconds 500;MoveVideo;Start-Sleep -Milliseconds 3100;Cursor $true 'music-mode-retains-pointer'
Key 77 @(17,16);Start-Sleep -Milliseconds 500;Idle 'music-return-hides'
Menu;Expand 'Window';Invoke 'PictureInPictureMenu';MoveVideo;Start-Sleep -Milliseconds 3100;Cursor $true 'pip-retains-pointer'
MoveVideo;ClickControl 'PipReturnButton';Start-Sleep -Milliseconds 500;Idle 'pip-return-hides'
Menu;Invoke 'InterfaceMenu';MoveControl 'ControlAutoHide';Start-Sleep -Milliseconds 3100;Cursor $true 'settings-retains-pointer'
$null=UI Toggle 'ControlAutoHide' '' 'false';ClickControl '' 'Done'
Idle 'cursor-hides-with-controls-always-shown'
Menu;Invoke 'InterfaceMenu';$null=UI Toggle 'ControlAutoHide' '' 'true';ClickControl '' 'Done'
Idle 'restored-auto-hide-hides'
}
if($ExtendedOnly -or $DelayOnly) {
    if(!$DelayOnly) {
    foreach($mode in 'Top','Bottom') {
        Menu;Expand 'Control bar';$null=UI Toggle ($mode+'OscOption') '' 'true';Start-Sleep -Milliseconds 350
        Idle ($mode+'-osc-idle-hides')
    }
    Menu;Expand 'Control bar';$null=UI Toggle 'EdgeToEdgeOption' '' 'false';Start-Sleep -Milliseconds 350
    Menu;Expand 'Control bar';$null=UI Toggle 'DockControlsOption' '' 'true';Start-Sleep -Milliseconds 350
    Idle 'docked-controls-still-hide-video-pointer'
    MoveControl 'PlayButton';Start-Sleep -Milliseconds 3100;Cursor $true 'docked-control-hover-retains-pointer'
    Menu;Expand 'Control bar';$null=UI Toggle 'DockControlsOption' '' 'false';Start-Sleep -Milliseconds 350
    Menu;Expand 'Control bar';$null=UI Toggle 'EdgeToEdgeOption' '' 'true';Start-Sleep -Milliseconds 350
    Menu;Expand 'Control bar';$null=UI Toggle 'FloatingOscOption' '' 'true';Start-Sleep -Milliseconds 350
    Menu;Invoke 'LayoutMenu';MoveControl 'SidebarClose';Start-Sleep -Milliseconds 3100;Cursor $true 'sidebar-retains-pointer'
    ClickControl 'SidebarClose';Idle 'sidebar-return-hides'
    }
    if(!$SkipDelay) {
    KeyboardMenu;Invoke 'InterfaceMenu';NumberInput 'ControlHideDelay' '1';ClickControl '' 'Done'
    MoveVideo;Start-Sleep -Milliseconds 1500;Cursor $false 'edited-positive-delay-hides'
    KeyboardMenu;Invoke 'InterfaceMenu'
    NumberInput 'ControlHideDelay' '2.5';ClickControl '' 'Done'
    MoveVideo;Cursor $true 'restored-delay-is-not-immediate';Start-Sleep -Milliseconds 3100;Cursor $false 'restored-delay-hides-after-timeout'
    }
    Key 79 @(17,16);Start-Sleep -Milliseconds 350;MoveControl '' 'HTTP or HTTPS media URL';Start-Sleep -Milliseconds 3100;Cursor $true 'url-input-retains-pointer';Key 27
    Idle 'url-cancel-return-hides'
    Key 79 @(17);Start-Sleep -Milliseconds 600;MoveControl '1148';Start-Sleep -Milliseconds 3100;Cursor $true 'native-file-picker-retains-pointer';Key 27
    Idle 'picker-cancel-return-hides'
}
Check (Get-Process -Id $AppProcessId).Responding 'player-still-responsive'
if(@($script:records|Where-Object {!$_.passed}).Count){exit 1}
