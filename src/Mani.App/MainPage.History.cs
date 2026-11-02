// IINA HistoryController, HistoryWindowController and native mpv watch-later integration.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Mani_App;

public sealed partial class MainPage
{
    private PlaybackSnapshot? _historySnapshot;
    private long _nextHistorySave;
    private (long Request, double Position)? _savedResume;
    private Action? _refreshHistory;
    private ContentDialog? _historyDialog;
    private async Task ConfigureResumeAsync()
    {
        await Execute(async session =>
        {
            Directory.CreateDirectory(App.WatchLaterDirectory);
            await session.SetAsync("watch-later-directory", App.WatchLaterDirectory);
            await session.SetAsync("save-position-on-quit", _settings.ResumePosition ? "yes" : "no");
            await session.SetAsync("resume-playback", _settings.ResumePosition ? "yes" : "no");
        });
    }
    private void InitializeHistory()
    {
        var history = new MenuFlyoutItem(); var resume = new MenuFlyoutItem(); var recent = new MenuFlyoutSubItem();
        foreach (var (item, key) in new[] { (history, "History"), (resume, "ResumeLast") })
        { AutomationProperties.SetAutomationId(item, key); _navigationText.Add(() => item.Text = UiText.Get(key)); }
        _navigationText.Add(() => recent.Text = UiText.Get("RecentFiles"));
        AutomationProperties.SetAutomationId(recent, "RecentFiles");
        history.Click += History_Click;
        resume.Click += async (_, _) =>
        {
            try { var entries = await App.Instance.History.ReadAsync(); if (entries.FirstOrDefault() is { } entry) await OpenHistoryAsync(entry); }
            catch (Exception error) { ShowError(error); }
        };
        FileMenu.Items.Insert(3, recent); FileMenu.Items.Insert(4, history); FileMenu.Items.Insert(5, resume);
        MainMenu.Opened += async (_, _) =>
        {
            try
            {
                var entries = await App.Instance.History.ReadAsync(); recent.Items.Clear(); resume.IsEnabled = entries.Count > 0;
                foreach (var entry in entries.Take(10))
                {
                    var item = new MenuFlyoutItem { Text = HistoryTitle(entry) };
                    item.Click += async (_, _) => await OpenHistoryAsync(entry); recent.Items.Add(item);
                }
                recent.IsEnabled = entries.Count > 0;
            }
            catch (Exception error) when (SharedPlayerSettings.IsStorageError(error))
            { recent.Items.Clear(); recent.IsEnabled = resume.IsEnabled = false; ShowError(error); }
        };
    }
    private static string HistoryTitle(PlaybackHistoryEntry entry) => string.IsNullOrWhiteSpace(entry.Title) ? Path.GetFileName(entry.Source) : entry.Title;
    private async Task OpenHistoryAsync(PlaybackHistoryEntry entry)
    {
        int operation = ++_sourcesRevision;
        bool exists = LocalMediaFile.PathFrom(entry.Source) is not { } local || await Task.Run(() => File.Exists(local));
        if (_closing || operation != _sourcesRevision) return;
        if (!exists) { ShowNotice("MissingFile"); return; }
        await OpenFileAsync(entry.Source, operation);
    }
    private async Task ObserveHistoryAsync(PlaybackSnapshot snapshot)
    {
        bool changed = _historySnapshot is not { } old || old.RequestId != snapshot.RequestId || old.PlaylistEntryId != snapshot.PlaylistEntryId;
        if (changed && _historySnapshot is { Path.Length: > 0 }) await FlushHistoryAsync();
        if (snapshot.Loading || snapshot.Path.Length == 0 || snapshot.Idle) { _historySnapshot = null; return; }
        _historySnapshot = snapshot;
        bool save = changed || Environment.TickCount64 >= _nextHistorySave;
        if (!save) return;
        _nextHistorySave = Environment.TickCount64 + 5000;
        try
        {
            if (_settings.RecordHistory) await App.Instance.History.RecordAsync(snapshot.Path, snapshot.Title, snapshot.Position, snapshot.Duration, changed);
        }
        catch (Exception error) when (SharedPlayerSettings.IsStorageError(error)) { Trace("history-save-failed", new { code = error.HResult }); }
        try
        {
            if (_settings.ResumePosition && _session is not null && !snapshot.Ended && _savedResume != (snapshot.RequestId, snapshot.Position))
            {
                await _session.SaveResumeAsync(snapshot.RequestId, snapshot.PlaylistEntryId);
                _savedResume = (snapshot.RequestId, snapshot.Position);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (SharedPlayerSettings.IsStorageError(error)) { Trace("history-save-failed", new { code = error.HResult }); }
    }
    private async Task FlushHistoryAsync()
    {
        try
        {
            if (_historySnapshot is { Path.Length: > 0 } last)
            {
                if (_settings.RecordHistory) await App.Instance.History.RecordAsync(last.Path, last.Title, last.Position, last.Duration, false, true);
            }
            await App.Instance.History.FlushAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (SharedPlayerSettings.IsStorageError(error)) { Trace("history-save-failed", new { code = error.HResult }); }
        try
        {
            if (_historySnapshot is { Path.Length: > 0, Ended: false } last && _settings.ResumePosition && _session is not null)
                await _session.SaveResumeAsync(last.RequestId, last.PlaylistEntryId);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (SharedPlayerSettings.IsStorageError(error)) { Trace("resume-save-failed", new { code = error.HResult }); }
    }
    private async void History_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || _pickerOpen || _closing) return;
        _dialogOpen = true; MainMenu.Hide(); RefreshOscHolds();
        PlaybackHistoryEntry? chosen = null;
        try
        {
            var entries = await App.Instance.History.ReadAsync();
            var search = new TextBox(); AutomationProperties.SetAutomationId(search, "HistorySearch");
            var field = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
            var grouping = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
            AutomationProperties.SetAutomationId(field, "HistorySearchField"); AutomationProperties.SetAutomationId(grouping, "HistoryGrouping");
            foreach (var key in new[] { "HistoryFullPath", "HistoryFilename" }) field.Items.Add(new ComboBoxItem { Tag = key });
            foreach (var key in new[] { "HistoryDate", "HistoryLocation" }) grouping.Items.Add(new ComboBoxItem { Tag = key });
            field.SelectedIndex = grouping.SelectedIndex = 0;
            var list = new ListView { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(list, "HistoryList");
            list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><StackPanel Margin="0,4" Spacing="2">
                <TextBlock Text="{Binding Title}" TextWrapping="Wrap"/><TextBlock Text="{Binding Detail}" TextWrapping="Wrap" FontSize="12" Opacity="0.8"/>
                </StackPanel></DataTemplate>
                """);
            list.GroupStyle.Add(new GroupStyle { HeaderTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><TextBlock Text="{Binding Label}" TextWrapping="Wrap" FontWeight="SemiBold" Margin="0,10,0,4"/></DataTemplate>
                """) });
            list.ContainerContentChanging += (_, args) =>
            {
                if (args.Item is HistoryDisplayRow row)
                { AutomationProperties.SetName(args.ItemContainer, row.Title); AutomationProperties.SetAutomationId(args.ItemContainer, "History_" + row.Entry.Identity); }
            };
            var empty = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            var clear = new Button { HorizontalAlignment = HorizontalAlignment.Stretch }; AutomationProperties.SetAutomationId(clear, "ClearHistory");
            var remove = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false }; AutomationProperties.SetAutomationId(remove, "RemoveHistory");
            var options = new Grid { ColumnSpacing = 8 };
            options.ColumnDefinitions.Add(new()); options.ColumnDefinitions.Add(new());
            options.Children.Add(field); Grid.SetColumn(grouping, 1); options.Children.Add(grouping);
            var actions = new Grid { ColumnSpacing = 8 };
            actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new());
            actions.Children.Add(remove); Grid.SetColumn(clear, 1); actions.Children.Add(clear);
            var root = new Grid { MinWidth = 260, RowSpacing = 8 };
            root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
            root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
            root.Children.Add(search); Grid.SetRow(options, 1); root.Children.Add(options);
            Grid.SetRow(list, 2); root.Children.Add(list); Grid.SetRow(empty, 2); root.Children.Add(empty);
            Grid.SetRow(actions, 3); root.Children.Add(actions);
            root.RowDefinitions.Insert(0, new() { Height = GridLength.Auto });
            foreach (var child in root.Children.Cast<FrameworkElement>()) Grid.SetRow(child, Grid.GetRow(child) + 1);
            var heading = new TextBlock { FontSize = 20, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            root.Children.Add(heading);
            var scroll = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            AutomationProperties.SetAutomationId(scroll, "HistoryScroll");
            bool compact = false;
            bool confirm = false, refreshing = false;
            _historyDialog = new ContentDialog { XamlRoot = XamlRoot, Content = scroll, DefaultButton = ContentDialogButton.None, IsPrimaryButtonEnabled = false };
            AutomationProperties.SetAutomationId(_historyDialog, "HistoryDialog");
            void ArrangeHistory()
            {
                double scale = _systemUi.TextScaleFactor;
                compact = XamlRoot.Size.Height < 400 || scale > 1.4;
                root.Height = compact ? Math.Max(400, 260 * scale) : Math.Max(240, Math.Min(560, XamlRoot.Size.Height - 220));
                scroll.MaxHeight = Math.Max(60, XamlRoot.Size.Height - (compact ? 120 * scale : 220));
                heading.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
                if (_historyDialog is not null) _historyDialog.Title = compact ? null : UiText.Get("History");
            }
            void HistoryRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ArrangeHistory();
            void Fill()
            {
                string? selected = (list.SelectedItem as HistoryDisplayRow)?.Entry.Identity;
                var groups = HistoryBrowser.Query(entries, search.Text, (HistorySearchField)Math.Max(0, field.SelectedIndex),
                    (HistoryGrouping)Math.Max(0, grouping.SelectedIndex), System.Globalization.CultureInfo.CurrentUICulture)
                    .Select(group => new HistoryDisplayGroup(group.Label, group.Entries.Select(entry => new HistoryDisplayRow(entry,
                        HistoryTitle(entry), $"{Time(entry.Position)} / {Time(entry.Duration)}  ·  {entry.PlayedAt.ToLocalTime():g}")))).ToArray();
                var view = new Microsoft.UI.Xaml.Data.CollectionViewSource { IsSourceGrouped = true, Source = groups };
                list.ItemsSource = view.View;
                list.SelectedItem = groups.SelectMany(group => group).FirstOrDefault(row => row.Entry.Identity == selected);
                empty.Visibility = groups.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                empty.Text = UiText.Get(entries.Count == 0 ? "HistoryEmpty" : "HistoryNoResults");
                clear.IsEnabled = entries.Count > 0;
            }
            _refreshHistory = () =>
            {
                refreshing = true;
                try
                {
                    _historyDialog.Title = compact ? null : UiText.Get("History"); heading.Text = UiText.Get("History");
                    AutomationProperties.SetName(_historyDialog, UiText.Get("History"));
                    _historyDialog.CloseButtonText = UiText.Get("Close"); _historyDialog.PrimaryButtonText = UiText.Get("Open");
                    search.PlaceholderText = UiText.Get("HistorySearch"); AutomationProperties.SetName(search, UiText.Get("HistorySearch"));
                    AutomationProperties.SetName(field, UiText.Get("HistorySearchIn")); AutomationProperties.SetName(grouping, UiText.Get("HistoryGroupBy"));
                    // WinUI caches the closed ComboBox selection presenter. Reset
                    // selection around label replacement so other-window language
                    // changes refresh both the dropdown and its selected label.
                    int fieldIndex = field.SelectedIndex, groupingIndex = grouping.SelectedIndex;
                    field.SelectedIndex = grouping.SelectedIndex = -1;
                    foreach (var item in field.Items.Concat(grouping.Items).Cast<ComboBoxItem>()) item.Content = new TextBlock { Text = UiText.Get((string)item.Tag), TextWrapping = TextWrapping.Wrap };
                    field.SelectedIndex = fieldIndex; grouping.SelectedIndex = groupingIndex;
                    clear.Content = new TextBlock { Text = UiText.Get(confirm ? "HistoryClearConfirm" : "ClearHistory"), TextWrapping = TextWrapping.Wrap };
                    remove.Content = new TextBlock { Text = UiText.Get("Remove"), TextWrapping = TextWrapping.Wrap };
                    Fill();
                }
                finally { refreshing = false; }
            };
            search.TextChanged += (_, _) => { if (!refreshing) Fill(); };
            field.SelectionChanged += (_, _) => { if (!refreshing) Fill(); };
            grouping.SelectionChanged += (_, _) => { if (!refreshing) Fill(); };
            list.SelectionChanged += (_, _) => { if (_historyDialog is not null) _historyDialog.IsPrimaryButtonEnabled = remove.IsEnabled = list.SelectedItem is HistoryDisplayRow; };
            list.DoubleTapped += (_, _) => { if (list.SelectedItem is HistoryDisplayRow row) { chosen = row.Entry; _historyDialog?.Hide(); } };
            list.PreviewKeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter && list.SelectedItem is HistoryDisplayRow row) { args.Handled = true; chosen = row.Entry; _historyDialog?.Hide(); } };
            clear.Click += async (_, _) =>
            {
                if (!confirm) { confirm = true; _refreshHistory?.Invoke(); return; }
                try { await App.Instance.History.RemoveAsync(); entries = []; confirm = false; _refreshHistory?.Invoke(); }
                catch (Exception error) { ShowError(error); }
            };
            remove.Click += async (_, _) =>
            {
                if (list.SelectedItem is not HistoryDisplayRow row) return;
                try { await App.Instance.History.RemoveAsync(row.Entry.Identity); entries = await App.Instance.History.ReadAsync(); Fill(); }
                catch (Exception error) { ShowError(error); }
            };
            ArrangeHistory(); _refreshHistory();
            XamlRoot.Changed += HistoryRootChanged;
            try { if (await _historyDialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedItem is HistoryDisplayRow selected) chosen = selected.Entry; }
            finally { XamlRoot.Changed -= HistoryRootChanged; }
        }
        catch (Exception error) { ShowError(error); }
        finally { _historyDialog = null; _refreshHistory = null; _dialogOpen = false; RefreshOscHolds(); WakeOsc(); RestorePlayerMenuFocus(); }
        if (chosen is not null && !_closing) await OpenHistoryAsync(chosen);
    }
}

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class HistoryDisplayRow(PlaybackHistoryEntry entry, string title, string detail)
{
    public PlaybackHistoryEntry Entry { get; } = entry;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
}

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class HistoryDisplayGroup(string label, IEnumerable<HistoryDisplayRow> rows) : List<HistoryDisplayRow>(rows)
{
    public string Label { get; } = label;
}
