// IINA menuNewWindow/player registry/window menu, adapted to WinUI windows.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Mani_App;

public sealed partial class MainPage
{
    internal bool CanReuseWindow => !_sessionUnavailable && !_closing && !_activationPending && !_activationHasMedia && !_uiOpening && !_pickerOpen && !_dialogOpen && !_m1Busy &&
        _surface.Surface is PlayerSurface.Welcome or PlayerSurface.Stopped;

    private void UpdateWindowsMenu()
    {
        NewWindowMenu.IsEnabled = !App.Instance.PlayerWindows.IsQuitting && !_closing;
        SwitchWindowMenu.Items.Clear();
        int index = 0;
        foreach (var window in App.Instance.PlayerWindows.Windows.Where(window => !window.IsClosing))
        {
            var item = new ToggleMenuFlyoutItem { Text = $"{++index}. {window.Title}", IsChecked = ReferenceEquals(window, Owner) };
            AutomationProperties.SetAutomationId(item, $"SwitchWindow{index}");
            item.Click += (_, _) => { MainMenu.Hide(); window.ActivatePlayer(); };
            SwitchWindowMenu.Items.Add(item);
        }
    }
    private void NewWindow_Click(object sender, RoutedEventArgs e)
    { MainMenu.Hide(); App.Instance.NewWindow(); }
    private async void CloseWindow_Click(object sender, RoutedEventArgs e)
    { MainMenu.Hide(); if (Owner is { } owner) await owner.CloseSessionAsync(); }
    private async void Exit_Click(object sender, RoutedEventArgs e)
    { MainMenu.Hide(); await App.Instance.QuitAsync(); }
    private void NewWindowShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (_closing || _pickerOpen || OwnsKeyboardInput()) return;
        e.Handled = true;
        App.Instance.NewWindow();
    }
    private async void CloseWindowShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (_closing || _pickerOpen || OwnsKeyboardInput()) return;
        e.Handled = true;
        if (Owner is { } owner) await owner.CloseSessionAsync();
    }
}
