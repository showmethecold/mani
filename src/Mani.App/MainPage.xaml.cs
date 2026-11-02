using System.Globalization;
using System.Text.Json;
using Mani.Playback;
using Mani_App.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;

namespace Mani_App;

public sealed partial class MainPage : Page
{
    private PlaybackSession? _session;
    private bool _sessionUnavailable;
    private string _lastError = "";
    private long _lastErrorAt;
    private SwapChainPanelBridge? _bridge;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private Task? _startTask;
    private readonly TaskCompletionSource<bool> _startupReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _activationPending;
    private bool _activationHasMedia;
    private Task _pollTask = Task.CompletedTask;
    private Task<MediaInfo>? _pollMediaInfo;
    private Task<MediaInfo> PollMediaInfoAsync() => _pollMediaInfo ??= _session!.GetMediaInfoAsync();
    private long _nextIdlePoll;
    private bool _closing, _updating, _seeking, _pickerOpen, _adjustingVolume, _pausePending;
    private int _playbackRevision;
    private long _lastTrace, _lastUiTick;
    private object? _lastStateTrace;
    private long? _lastDiagnosticsUpdate;
    private (long Position, long Duration)? _displayedTime;
    private double? _pendingSeek;
    private double? _pendingVolume;
    private string _noticeValue = "";
    private Func<string>? _noticeText;
    private string _notice { get => _noticeText?.Invoke() ?? _noticeValue; set { _noticeText = null; _noticeValue = value; } }
    private void ShowNotice(string key) { _noticeText = () => UiText.Get(key); ApplySurface(_snapshot); }
    private PlaybackSnapshot? _snapshot;
    private readonly PlaybackUiCache _uiCache = new();
    private SessionTraceLog? _traceLog;
    private SessionLogDirectory? _logOwner;
    private string _logDirectory = "";
    public MainWindow? Owner { get; set; }

    public MainPage()
    {
        InitializeComponent();
        InitializeOsc();
        InitializePointerInput();
        InitializeOsd();
        InitializeLayoutSidebar();
        InitializeDelaySliders();
        InitializeNavigation();
        InitializeHistory();
        InitializeTimelinePreview();
        InitializeMediaControls();
        SelectQuickPane(QuickPane.Video);
        RefreshLocalizedControls();
        foreach (var refresh in _navigationText) refresh();
        _timer.Tick += (_, _) =>
        {
            if (_closing) return;
            ApplyLanguage();
            long now = Environment.TickCount64;
            // A timer gap is evidence of delayed dispatch, not proof of a UI
            // deadlock (suspend/debugging can also delay the dispatcher).
            if (_lastUiTick != 0 && now - _lastUiTick >= 1000)
                Trace("ui-timer-gap", new { milliseconds = now - _lastUiTick, pollPending = !_pollTask.IsCompleted,
                    pane = _quickPane.ToString(), sidebar = _sidebar.Phase.ToString(), quickWrites = _quickWrites,
                    oscAnimating = _osc.Animating, windowActive = _cursorWindowActive });
            _lastUiTick = now;
            if (_pollTask.IsCompleted) _pollTask = PollAsync();
        };
        foreach (var timeline in new[] { MainTimeline, MusicTimeline })
        {
            timeline.AddHandler(PointerPressedEvent, new PointerEventHandler(Timeline_Pressed), true);
            timeline.AddHandler(PointerReleasedEvent, new PointerEventHandler(Timeline_Released), true);
            timeline.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => CommitSeek()), true);
            timeline.LostFocus += (_, _) => CommitSeek();
        }
        foreach (var volume in new[] { MainVolume, MusicVolume })
        {
            volume.AddHandler(PointerPressedEvent, new PointerEventHandler(Volume_Pressed), true);
            volume.AddHandler(PointerReleasedEvent, new PointerEventHandler(Volume_Released), true);
            volume.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => _adjustingVolume = false), true);
            volume.LostFocus += (_, _) => _adjustingVolume = false;
        }
    }
    private void Page_Loaded(object sender, RoutedEventArgs e) => _startTask ??= StartAsync();
    private async Task StartAsync()
    {
        bool ready = false;
        try
        {
            var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "mpv-runtime.json")));
            using (manifest)
            {
                if (_closing) return;
                var logRoot = Path.Combine(App.DataDirectory, "Logs");
                _logOwner = new SessionLogDirectory(logRoot);
                _logDirectory = _logOwner.Path;
                _ = Task.Run(() => SessionLogDirectory.Prune(logRoot));
                _traceLog = new SessionTraceLog(Path.Combine(_logDirectory, "host-state.jsonl"));
                Trace("window-created", new { hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Owner!).ToInt64(), processId = Environment.ProcessId });
                File.Copy(Path.Combine(AppContext.BaseDirectory, "mpv-runtime.json"), Path.Combine(_logDirectory, "runtime.json"));
                InitializeMediaTimingIfRequested();
                InitializeStartupIfRequested();
                InitializeOverheadIfRequested();
                _session = await PlaybackSession.CreateAsync(new PlaybackOptions(
                    Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"), manifest.RootElement.GetProperty("sha256").GetString()!,
                    Path.Combine(_logDirectory, "mpv.log")));
            }
            if (_closing) return;
            await LoadSettingsAsync();
            if (_closing) return;
            _bridge = new SwapChainPanelBridge(VideoPanel);
            OpenButton.IsEnabled = OpenUrlButton.IsEnabled = Volume.IsEnabled = true;
            LayoutOsc();
            ApplySurface(null);
            await SetBlurEnabledAsync(true, CancellationToken.None);
            if (_closing) return;
            Status.Text = UiText.Get("DropHint");
            _timer.Start();
            WatchDisplay();
            WatchPower();
            ready = true;
            if (!StartFontVerificationIfRequested() && !StartM2VerificationIfRequested() && !StartMediaTimingIfRequested() && !StartStartupIfRequested() && !StartOverheadIfRequested() && !StartLifecycleIfRequested()) StartUnattendedPlaybackIfRequested();
        }
        catch (Exception error)
        {
            try { FailMediaTimingStartup(error); _startupProbe?.Fail(error); }
            catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException) { }
            ShowError(error);
        }
        finally { _startupReady.TrySetResult(ready && !_closing); }
    }
    internal async Task OpenActivatedFilesAsync(IReadOnlyList<string> paths)
    {
        _activationPending = true;
        try
        {
            // Explorer can activate the app before Page.Loaded, engine creation,
            // or preference restoration has finished. Use the regular open path.
            if (await _startupReady.Task && !_closing)
            {
                await OpenSourcesAsync(paths);
                // loadfile acceptance precedes the first UI snapshot. Reserve
                // this window until the engine reports idle, so the next rapid
                // activation cannot replace media behind a stale welcome page.
                _activationHasMedia = true;
            }
        }
        catch (Exception error) { if (!_closing) ShowError(error); }
        finally { _activationPending = false; }
    }
    private async Task PollAsync()
    {
        if (_session is null || _sessionUnavailable || _closing || _hostStress) return;
        if (_overheadGate is not null && !_overheadGate.BeginPoll()) return;
        bool idle = _snapshot is { Idle: true } && !_uiOpening;
        bool minimized = Owner?.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized };
        if ((idle || minimized) && Environment.TickCount64 < _nextIdlePoll) return;
        _nextIdlePoll = Environment.TickCount64 + 1000;
        _pollMediaInfo = null;
        try
        {
            await SyncBlurMaskAsync();
            await SyncDisplayColorAsync();
            var revision = _playbackRevision;
            // WinUI uses DIPs; mpv alone owns ResizeBuffers in physical pixels.
            using var poll = await _session.PollAsync(
                (int)Math.Round(VideoPanel.ActualWidth * VideoPanel.CompositionScaleX),
                (int)Math.Round(VideoPanel.ActualHeight * VideoPanel.CompositionScaleY));
            var snapshot = poll.Snapshot;
            if (_closing) return;
            _bridge?.Attach(poll.SwapChain?.Pointer ?? 0);
            // A click can complete while this poll awaits a swapchain lease.
            if (revision != _playbackRevision || _pausePending || _uiOpening) return;
            if (_overheadGate is not null && !_overheadGate.BeginUiUpdate()) return;
            if (_snapshot is not { } previous || previous.RequestId != snapshot.RequestId || previous.PlaylistEntryId != snapshot.PlaylistEntryId)
            { ResetMediaPointerInput(); CancelTimelinePreview(); }
            await ObserveHistoryAsync(snapshot);
            if (_closing || revision != _playbackRevision || _uiOpening) return;
            _snapshot = snapshot;
            if (snapshot.Idle && !snapshot.Loading) _activationHasMedia = false;
            UpdateChapterMarkers();
            PersistObservedSettings(snapshot);
            ObserveSubtitleMatching(snapshot);
            if (_lastColorOutput != snapshot.ColorOutput)
            {
                _lastColorOutput = snapshot.ColorOutput;
                Trace("color-output-policy", new { snapshot.ColorOutput, snapshot.ColorPrimaries, snapshot.Transfer });
            }
            PublishWebState(snapshot);
            var traceState = new { snapshot.RequestId, snapshot.Paused, snapshot.Idle, snapshot.Loading, snapshot.Volume,
                snapshot.AudioOutput, snapshot.AudioDevice, snapshot.PausedForCache, snapshot.DroppedFrames };
            if (!traceState.Equals(_lastStateTrace) || Environment.TickCount64 - _lastTrace >= (_m1Busy || _overheadGate is not null ? 2000 : 30000))
            {
                _lastTrace = Environment.TickCount64;
                _lastStateTrace = traceState;
                Trace("state", new { snapshot.Position, snapshot.Paused, snapshot.Idle, snapshot.Loading,
                    snapshot.Volume, snapshot.AudioOutput, snapshot.AudioDevice, snapshot.AudioBuffer,
                    snapshot.AvSync, snapshot.PausedForCache, snapshot.DroppedFrames,
                    Width = VideoPanel.ActualWidth, Height = VideoPanel.ActualHeight,
                    ScaleX = VideoPanel.CompositionScaleX, ScaleY = VideoPanel.CompositionScaleY });
            }
            _updating = true;
            try
            {
                var (ui, changes) = _uiCache.Update(snapshot, _notice, !_adjustingVolume && _pendingVolume is null && _inputTask.IsCompleted);
                ApplySurface(snapshot);
                ObserveOsd(snapshot);
                _updating = true;
                UpdateObservedControls(snapshot);
                if ((changes & PlaybackUiChanges.Playback) != 0)
                {
                    PlayButton.IsEnabled = ui.CanPlay && _surface.HasMedia;
                    SetPlayAppearance(ui.PlayLabel);
                }
                if ((changes & PlaybackUiChanges.Seeking) != 0) Timeline.IsEnabled = ui.CanSeek && _surface.HasMedia;
                if (_surface.HasMedia && !_seeking && _pendingSeek is null && _inputTask.IsCompleted)
                {
                    var maximum = Math.Max(1, snapshot.Duration);
                    if (Timeline.Maximum != maximum) Timeline.Maximum = maximum;
                    if (Timeline.Value != snapshot.Position) Timeline.Value = snapshot.Position;
                }
                if ((changes & PlaybackUiChanges.Volume) != 0 && Volume.Value != ui.Volume)
                    Volume.Value = ui.Volume;
                var time = ((long)Math.Max(0, _surface.Position), (long)Math.Max(0, _surface.Duration));
                if (_displayedTime != time)
                {
                    SetText(TimeLabel, Time(_surface.Position));
                    SetText(DurationLabel, Time(_surface.Duration));
                    _displayedTime = time;
                }
                UpdatePlaybackInfo(snapshot);
                if ((changes & PlaybackUiChanges.Status) != 0) Status.Text = UiText.Status(ui.Status);
            }
            finally { _updating = false; }
            await ApplyTrackpadInputAsync();
            await ObserveMusicAsync(snapshot, revision);
            await ObserveWindowSizeAsync(snapshot, revision);
            await ObserveQuickSettingsAsync(snapshot, revision);
            await ObserveAudioOutputAsync();
            await ObserveNavigationAsync(snapshot, revision);
        }
        catch (Exception error) { ShowError(error); }
    }
    private static void SetText(TextBlock target, string value)
    {
        if (target.Text != value) target.Text = value;
    }
    private void UpdatePlaybackInfo(PlaybackSnapshot snapshot, bool force = false)
    {
        if (InfoPanel.Visibility != Visibility.Visible) return;
        var now = Environment.TickCount64;
        if (!force && _lastDiagnosticsUpdate is long last && now - last < 1000) return;
        _lastDiagnosticsUpdate = now;
        SetText(Diagnostics, string.Format(CultureInfo.InvariantCulture, UiText.Get("PlaybackInfoFormat"),
            snapshot.Version, snapshot.VideoOutput, snapshot.HardwareDecoder, snapshot.VideoFormat, snapshot.ColorPrimaries,
            snapshot.Transfer, snapshot.DroppedFrames, snapshot.AudioOutput, snapshot.AudioDevice, snapshot.AudioBuffer,
            snapshot.AvSync, UiText.Get(snapshot.PausedForCache ? "Yes" : "No"), snapshot.Volume, VideoPanel.ActualWidth,
            VideoPanel.ActualHeight, VideoPanel.CompositionScaleX, _logDirectory));
    }
    private static string Time(double seconds) => seconds >= 3600 ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss");
    private void ShowError(Exception error, string? message = null)
    {
        if (_closing) return;
        if (_session is { IsAvailable: false } && !_sessionUnavailable)
        {
            _sessionUnavailable = true;
            _timer.Stop();
            _opening.Cancel();
            _openCancellation?.Cancel();
            ResetMediaPointerInput();
            _pendingSeek = _pendingVolume = null;
        }
        _uiCache.Invalidate();
        _noticeText = () => string.Format(UiText.Get("ErrorFormat"), UiText.Get(message ?? "OperationError"), error.HResult);
        if (_sessionUnavailable) _noticeText = () => UiText.Get("SessionUnavailable");
        Status.Text = _notice;
        ApplySurface(_sessionUnavailable ? null : _snapshot);
        if (_sessionUnavailable)
            OpenButton.IsEnabled = OpenUrlButton.IsEnabled = PlayButton.IsEnabled = Volume.IsEnabled = Timeline.IsEnabled = false;
        string identity = $"{error.GetType().FullName}:{error.HResult}:{error.Message}";
        long now = Environment.TickCount64;
        if (identity == _lastError && now - _lastErrorAt < 10000) return;
        _lastError = identity; _lastErrorAt = now;
        if (!string.IsNullOrEmpty(_logDirectory))
        {
            try { File.AppendAllText(Path.Combine(_logDirectory, "host-errors.txt"), $"{DateTimeOffset.Now:O} {DiagnosticPrivacy.Redact(error.ToString())}\n"); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
        }
    }
    private async Task Execute(Func<PlaybackSession, Task> action)
    {
        if (_session is null || _sessionUnavailable || _closing || _m1Busy) return;
        try { await action(_session); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error); }
    }
    private async Task<bool> OpenFileAsync(string path, int? sourceOperation = null)
    {
        if (_session is null || _sessionUnavailable || _closing || _m1Busy) return false;
        int operation = sourceOperation ?? ++_sourcesRevision;
        ResetMediaPointerInput();
        CancelTimelinePreview();
        await FlushHistoryAsync();
        if (_closing || operation != _sourcesRevision) return false;
        _playbackRevision++;
        _notice = "";
        _pendingSeek = null;
        _seeking = false;
        long opening = _opening.Begin();
        _navigationInfo = null; UpdateChapterMarkers();
        _uiCache.Invalidate();
        ApplySurface(null);
        _openCancellation?.Cancel();
        _openCancellation?.Dispose();
        _openCancellation = new CancellationTokenSource();
        try { await _session.OpenAsync(path, _settings.PauseWhenOpen, _openCancellation.Token); return true; }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) { ShowError(error, "OpenError"); return false; }
        finally { if (_opening.Complete(opening)) { _playbackRevision++; _nextIdlePoll = 0; } }
    }
    private void LogFilePicker(string stage)
    {
        if (string.IsNullOrEmpty(_logDirectory)) return;
        try { File.AppendAllText(Path.Combine(_logDirectory, "file-picker.txt"), $"{DateTimeOffset.Now:O} {stage}\n"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_pickerOpen || _dialogOpen || _closing || _m1Busy || _session is null) return;
        _pickerOpen = true;
        RefreshOscHolds(); UpdateOsc();
        OpenButton.IsEnabled = false;
        _notice = "";
        try
        {
            if (Owner is null) throw new InvalidOperationException("The player window is not ready. Please try again.");
            LogFilePicker("creating Windows App SDK picker");
            // Desktop picker is bound directly to the owning WinUI WindowId.
            // Its default filter includes all files; cancellation returns null.
            var picker = new FileOpenPicker(Owner.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
                CommitButtonText = UiText.Get("Open")
            };
            LogFilePicker("showing picker");
            var files = await picker.PickMultipleFilesAsync();
            LogFilePicker(files.Count == 0 ? "cancelled" : "files selected");
            if (files.Count > 0 && !_closing) await OpenSourcesAsync(files.Select(file => file.Path), append: ReferenceEquals(sender, _addFilesButton));
        }
        catch (Exception error)
        {
            LogFilePicker($"failed: 0x{error.HResult:X8}");
            ShowError(error, "OpenError");
        }
        finally
        {
            _pickerOpen = false;
            RefreshOscHolds(); UpdateOsc();
            if (!_closing && _session is not null) OpenButton.IsEnabled = true;
        }
    }
    private void Trace(string action, object details) => _traceLog?.Append(action, details);
    internal void CloseTrace() { _traceLog?.Dispose(); _logOwner?.Dispose(); }
    private async void Play_Click(object sender, RoutedEventArgs e) => await TogglePauseAsync("button");
    private async void PlayShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (OwnsKeyboardInput()) return;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        // Native controls own activation/navigation keys. Only the video surface uses global Space.
        for (var node = focused; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (node is ButtonBase or Slider or ToggleSwitch or Selector && node != VideoFocus) return;
        e.Handled = true;
        WakeOsc();
        await TogglePauseAsync("keyboard");
    }
    private async Task TogglePauseAsync(string source)
    {
        if (_session is null || _sessionUnavailable || _closing || _m1Busy || _pausePending || _snapshot is not { Idle: false, Loading: false }) return;
        _pausePending = true;
        _uiCache.Invalidate(); // Immediate button/status feedback bypasses the normal rendering path.
        _playbackRevision++;
        // IINA's play action keeps the control available. Disabling a focused
        // WinUI button moves keyboard focus; _pausePending already guards reentry.
        Trace("pause-request", new { source });
        try
        {
            var paused = await _session.TogglePauseAsync();
            Trace("pause-result", new { source, paused });
            if (_closing) return;
            _notice = "";
            SetPlayAppearance(paused ? "Play" : "Pause");
            Status.Text = UiText.Get(paused ? "Paused" : "Playing");
        }
        catch (Exception error) { ShowError(error, "PauseError"); }
        finally
        {
            _pausePending = false;
            _playbackRevision++;
        }
    }
    private void Volume_Pressed(object sender, PointerRoutedEventArgs e)
    { if (e.GetCurrentPoint(Volume).Properties.IsLeftButtonPressed) _adjustingVolume = true; }
    private void Volume_Released(object sender, PointerRoutedEventArgs e)
    { if (!e.GetCurrentPoint(Volume).Properties.IsLeftButtonPressed) _adjustingVolume = false; }
    private void Timeline_Pressed(object sender, PointerRoutedEventArgs e)
    {
        // IINA keeps secondary mouse actions separate from slider tracking.
        // A right-button release must not commit a synthetic seek at the old value.
        if (e.GetCurrentPoint(Timeline).Properties.IsLeftButtonPressed) _seeking = true;
    }
    private void Timeline_Released(object sender, PointerRoutedEventArgs e)
    { if (!e.GetCurrentPoint(Timeline).Properties.IsLeftButtonPressed) CommitSeek(); }
    private void CommitSeek() { if (_seeking) { _seeking = false; _pendingSeek = Timeline.Value; RequestInputDrain(); } }
    private void Timeline_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        // IINA playSliderChanges uses continuous exact seek by default.
        if (!_updating && !_closing && !_sessionUnavailable && _session is not null)
        { _pendingSeek = e.NewValue; RequestInputDrain(); }
    }
    private void Timeline_KeyUp(object sender, KeyRoutedEventArgs e) => CommitSeek();
    private void Volume_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_updating && !_closing && _session is not null)
        {
            _uiCache.InvalidateVolume(); // Reconcile even if the engine rejects or clamps this value.
            _pendingVolume = e.NewValue;
            RequestInputDrain();
        }
    }
    private void Video_SizeChanged(object sender, SizeChangedEventArgs e) { LayoutOsc(); WakeOsc(); }
    private void Video_ScaleChanged(SwapChainPanel sender, object args) { LayoutOsc(); RequestOscMaterial(); }
    private void Fullscreen_Click(object sender, RoutedEventArgs e) { Owner?.ToggleFullscreen(); WakeOsc(); }
    private void Info_Click(object sender, RoutedEventArgs e)
    {
        CloseLayoutSidebar(animate: false);
        InfoPanel.Visibility = InfoPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (_snapshot is { } snapshot) UpdatePlaybackInfo(snapshot, force: true);
    }
    private async void FrameForward_Click(object sender, RoutedEventArgs e) => await Execute(s => s.StepFrameAsync());
    private async void FrameBack_Click(object sender, RoutedEventArgs e) => await Execute(s => s.StepFrameAsync(backward: true));
    private async void Loop_Click(object sender, RoutedEventArgs e) => await Execute(s => s.CycleAbLoopAsync());
    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopPlaybackAsync();
    private async Task StopPlaybackAsync()
    {
        ++_sourcesRevision;
        await FlushHistoryAsync();
        ResetMediaPointerInput();
        _pendingSeek = null;
        _notice = "";
        _opening.Cancel();
        _openCancellation?.Cancel();
        _playbackRevision++;
        await Execute(s => s.StopAsync());
    }
    private async void Screenshot_Click(object sender, RoutedEventArgs e) => await CaptureScreenshotAsync();
    private async Task CaptureScreenshotAsync()
    {
        // IINA PlayerCore.screenshot requires a selected video stream. Read the
        // current engine model, including when the quick sidebar is closed.
        if (!_surface.HasMedia || _uiOpening || _snapshot is not { } media) return;
        int revision = _playbackRevision;
        await Execute(async s =>
        {
            var info = await s.GetMediaInfoAsync();
            if (_closing || _uiOpening || revision != _playbackRevision || !QuickSettings.Matches(info, media)) return;
            if (!long.TryParse(info.VideoTrack, out long video) || video <= 0)
            {
                Trace("screenshot-skipped", new { reason = "no-video", media.RequestId });
                return;
            }
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Mani");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"Mani-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            await s.CommandAsync("screenshot-to-file", path, "subtitles");
            Trace("screenshot-saved", new { path, media.RequestId, media.PlaylistEntryId });
            if (!_closing && !_uiOpening && revision == _playbackRevision && _snapshot is { } current &&
                QuickSettings.Matches(info, current))
                ShowQuickFeedback(new(OsdKind.Screenshot), current);
        });
    }
    private async void Logs_Click(object sender, RoutedEventArgs e)
    {
        try { if (Directory.Exists(_logDirectory)) await Launcher.LaunchFolderPathAsync(_logDirectory); }
        catch (Exception error) { ShowError(error); }
    }
    private void Video_DragOver(object sender, DragEventArgs e)
    {
        if (!_closing && !_dialogOpen && !_pickerOpen && SupportsMediaDrop(e.DataView)) e.AcceptedOperation = DataPackageOperation.Copy;
    }
    private async void Video_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true; var deferral = e.GetDeferral();
        var media = _snapshot;
        bool subtitleDrop = false;
        try
        {
            if (_closing || _m1Busy || _dialogOpen || _pickerOpen) return;
            var sources = await ReadMediaDropAsync(e.DataView);
            if (!_closing && sources.Count > 0 && sources.All(p => SubtitleMatcher.Extensions.Contains(System.IO.Path.GetExtension(p))) &&
                _session is { } session && media is { Loading: false, Idle: false })
            {
                subtitleDrop = true;
                _subtitleManualRevision++; _matchingCancellation?.Cancel();
                foreach (string path in sources)
                    await session.LoadExternalTrackAsync(media.RequestId, media.PlaylistEntryId, path, ExternalTrackKind.PrimarySubtitle);
                if (MediaOperationCurrent(media))
                {
                    _notice = ""; _trackOperationStatusKey = "M5TrackLoaded";
                    ApplySurface(_snapshot); RefreshMediaControls();
                }
                _nextQuickRead = 0;
            }
            else if (!_closing && sources.Count > 0) await OpenSourcesAsync(sources);
            else if (!_closing) ShowNotice("NoPlayableFiles");
        }
        catch (OperationCanceledException) when (subtitleDrop) { }
        catch (Exception error)
        {
            if (!subtitleDrop) ShowError(error);
            else if (media is not null && MediaOperationCurrent(media))
            { _trackOperationStatusKey = "M5TrackFailed"; ShowError(error, "M5TrackFailed"); RefreshMediaControls(); }
        }
        finally { deferral.Complete(); }
    }
    public async Task ShutdownAsync()
    {
        _closing = true;
        _startupReady.TrySetResult(false);
        _matchingCancellation?.Cancel();
        _interfaceDialog?.Hide();
        _historyDialog?.Hide();
        CancelTimelinePreview();
        _fileRevealDialog?.Cancel();
        _musicFade?.Stop(); MusicVolumeFlyout.Hide();
        StopPointerInput();
        StopOsc();
        StopOsd();
        StopLayoutSidebar();
        _openCancellation?.Cancel();
        _timer.Stop();
        Controls.IsHitTestVisible = false;
        OpenButton.IsEnabled = PlayButton.IsEnabled = Volume.IsEnabled = Timeline.IsEnabled = false;
        if (_startTask is not null) await _startTask;
        StopSettingsWatch();
        await FlushHistoryAsync();
        _session?.BeginClose();
        StopPowerWatch();
        await _sleepPauseTask;
        RecordLifecycleStage("shutdown-started");
        try { await ShutdownProbesAsync(); RecordLifecycleStage("probes-stopped"); }
        finally
        {
            await Task.WhenAll(_thumbnailTasks.ToArray());
            await _inputTask;
            await _matchingTask;
            await _mediaPreferencesTask;
            await _pollTask; // Return all cross-thread leases before detaching.
            await _oscMaterialTask;
            if (_settingsStore is not null) await ObserveSettingsWriteAsync(_settingsStore.FlushAsync());
            await _settingsWrite;
            RecordLifecycleStage("poll-drained");
            try { _bridge?.Dispose(); RecordLifecycleStage("panel-detached"); }
            finally
            {
                _bridge = null;
                if (_session is not null) { await _session.DisposeAsync(); _session = null; }
                RecordLifecycleStage("session-disposed");
            }
        }
    }
}
