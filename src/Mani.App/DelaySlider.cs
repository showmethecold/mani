// Windows pointer boundary for IINA AudioDelayView/SubDelayView release-to-apply.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Windows.System;
using Windows.UI.Core;

namespace Mani_App;

public sealed class DelaySlider : Slider
{
    internal bool PointerEditing { get; private set; }
    internal bool SuppressInput { get; private set; }
    private int _gesture;
    private bool _pointerInside;
    internal event EventHandler? EditStarted;
    internal event EventHandler<bool>? EditFinished;

    public DelaySlider()
    {
        // Thumb may consume routed events before Slider's virtual handler.
        PointerEntered += (_, _) => _pointerInside = true;
        PointerExited += (_, _) => _pointerInside = false;
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) => Begin(e)), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler((_, e) =>
        { if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) FinishLater(true); }), true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, e) =>
            FinishLater(!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)), true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => CancelEdit()), true);
        Unloaded += (_, _) => CancelEdit();
    }
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        Begin(e); // Track clicks may change Value inside the base handler.
        base.OnPointerPressed(e);
    }
    private void Begin(PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) StartEdit();
    }
    internal void BeginValueChange()
    {
        // A native track click raises ValueChanged before managed PointerPressed.
        // Reuse the current input state to establish its preview transaction first.
        bool leftDown = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftButton) & CoreVirtualKeyStates.Down) != 0;
        if (_pointerInside && leftDown && !SuppressInput) StartEdit();
    }
    private void StartEdit()
    {
        if (PointerEditing || !IsEnabled) return;
        SuppressInput = false;
        PointerEditing = true;
        _gesture++;
        EditStarted?.Invoke(this, EventArgs.Empty);
    }
    private void FinishLater(bool commit)
    {
        int gesture = _gesture;
        // Native Slider releases capture before the final routed release. Let
        // that input finish, then read the final snapped preview exactly once.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (gesture != _gesture) return;
            SuppressInput = false;
            if (!PointerEditing) return;
            PointerEditing = false;
            _gesture++;
            EditFinished?.Invoke(this, commit);
        });
    }
    internal void CancelEdit()
    {
        if (!PointerEditing) return;
        PointerEditing = false;
        SuppressInput = true;
        _gesture++;
        EditFinished?.Invoke(this, false);
    }
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (!PointerEditing) SuppressInput = false;
        base.OnKeyDown(e);
    }
}
