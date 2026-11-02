// IINA AudioDelayView/SubDelayView port; WinUI pointer/keyboard completion adapter.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Mani_App;

public sealed partial class MainPage
{
    private readonly Dictionary<DelaySlider, DelayEdit> _delayEdits = [];
    private readonly Dictionary<string, double> _observedDelays = [];
    private (DelaySlider Slider, NumberBox Number)[] _delayControls = [];
    private DelayMedia CurrentDelayMedia => new(_snapshot?.RequestId ?? -1, _snapshot?.PlaylistEntryId ?? -1, _playbackRevision);
    private bool DelayAvailable(DelaySlider slider) => QuickCurrent && _sidebar.WantsVisible && !IsMusicMode && !IsPictureInPicture &&
        _quickPane == ((string)slider.Tag == "audio-delay" ? QuickPane.Audio : QuickPane.Subtitles);

    private void InitializeDelaySliders()
    {
        _delayControls = [(QuickAudioDelaySlider, QuickAudioDelay), (QuickPrimaryDelaySlider, QuickPrimaryDelay), (QuickSecondaryDelaySlider, QuickSecondaryDelay)];
        foreach (var (slider, number) in _delayControls)
        {
            _delayEdits.Add(slider, new());
            slider.EditStarted += (_, _) =>
            {
                if (!DelayAvailable(slider)) { slider.CancelEdit(); return; }
                _quickRevision++;
                _delayEdits[slider].Begin(CurrentDelayMedia, double.IsFinite(number.Value) ? number.Value : slider.Value, (string)slider.Tag == "audio-delay");
                PreviewDelay(slider, slider.Value);
                Trace("delay-slider-edit", new { phase = "begin", property = (string)slider.Tag });
            };
            slider.EditFinished += async (_, commit) =>
            {
                var edit = _delayEdits[slider];
                if (!edit.Active) return;
                double? value = edit.Finish(CurrentDelayMedia, commit && DelayAvailable(slider));
                Trace("delay-slider-edit", new { phase = value.HasValue ? "commit" : commit ? "unchanged-or-stale" : "cancel", property = (string)slider.Tag, value });
                if (value is { } delay) await SetQuickPropertyAsync((string)slider.Tag, delay.ToString(CultureInfo.InvariantCulture));
                else if (_observedDelays.TryGetValue((string)slider.Tag, out var observed)) SetDelayPreview(slider, number, observed);
                _nextQuickRead = 0;
            };
        }
    }
    private void SetDelayPreview(DelaySlider slider, NumberBox number, double value)
    {
        bool updating = _quickUpdating;
        _quickUpdating = true;
        try
        {
            slider.Value = DelayEdit.Position(value);
            number.Value = value;
        }
        finally { _quickUpdating = updating; }
    }
    private void PreviewDelay(DelaySlider slider, double value)
    {
        var number = _delayControls.First(p => p.Slider == slider).Number;
        SetDelayPreview(slider, number, _delayEdits[slider].Preview(value));
    }
    private async void QuickDelay_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_quickUpdating || sender is not DelaySlider slider || !DelayAvailable(slider) || slider.SuppressInput) return;
        slider.BeginValueChange();
        if (_delayEdits[slider].Active) PreviewDelay(slider, e.NewValue);
        else await SetQuickPropertyAsync((string)slider.Tag, e.NewValue.ToString(CultureInfo.InvariantCulture));
    }
    private bool DelayEditing(string property) => _delayEdits.Any(p => (string)p.Key.Tag == property && p.Value.Active);
    private void ObserveDelaySliders(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (slider, _) in _delayControls)
            if (double.TryParse(values.GetValueOrDefault((string)slider.Tag), CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
            {
                _observedDelays[(string)slider.Tag] = value;
                if (!_delayEdits[slider].Active) slider.Value = DelayEdit.Position(value);
            }
    }
    private void CancelDelayEdits()
    {
        foreach (var slider in _delayEdits.Keys) slider.CancelEdit();
    }
}
