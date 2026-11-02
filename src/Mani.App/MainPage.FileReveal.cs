// IINA MainMenuActions current-file menu validation/action and default input.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.UI.Popups;

namespace Mani_App;

public sealed partial class MainPage
{
    private bool _fileRevealPending;
    private IAsyncOperation<IUICommand>? _fileRevealDialog;
    private string? CurrentLocalFile => !_closing && !_uiOpening && !_m1Busy && _surface.HasMedia && _snapshot is { } media
        ? LocalMediaFile.PathFrom(media.Path) : null;

    private async void RevealFile_Click(object sender, RoutedEventArgs e) => await RevealCurrentFileAsync();
    private async Task RevealCurrentFileAsync()
    {
        if (_fileRevealPending || _pickerOpen || _dialogOpen || CurrentLocalFile is not { } path || _snapshot is not { } media) return;
        _fileRevealPending = true;
        MainMenu.Hide();
        UpdateActionMenu();
        Trace("file-reveal-request", new { path, media.RequestId, media.PlaylistEntryId });
        try
        {
            await Interop.FileReveal.ShowAsync(path);
            Trace("file-reveal-completed", new { path, media.RequestId, media.PlaylistEntryId });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException)
        {
            Trace("file-reveal-failed", new { path, media.RequestId, error.HResult });
            if (_closing || _uiOpening || _dialogOpen || _pickerOpen || _snapshot is not { } current ||
                current.RequestId != media.RequestId || current.PlaylistEntryId != media.PlaylistEntryId || current.Path != media.Path) return;
            _dialogOpen = true;
            RefreshOscHolds(); UpdateOsc();
            try
            {
                // An owned system dialog remains readable when the music
                // window contains only its 72-DIP control bar.
                var dialog = new MessageDialog(UiText.Get("RevealFileHint"), UiText.Get("RevealFileError"));
                dialog.Commands.Add(new UICommand(UiText.Get("Done")));
                dialog.DefaultCommandIndex = dialog.CancelCommandIndex = 0;
                WinRT.Interop.InitializeWithWindow.Initialize(dialog, WinRT.Interop.WindowNative.GetWindowHandle(Owner!));
                _fileRevealDialog = dialog.ShowAsync();
                await _fileRevealDialog;
            }
            catch (OperationCanceledException) when (_closing) { }
            finally
            {
                _fileRevealDialog = null; _dialogOpen = false;
                RefreshOscHolds(); UpdateOsc(); RestorePlayerMenuFocus();
            }
        }
        finally { _fileRevealPending = false; if (!_closing) UpdateActionMenu(); }
    }
}
