// IINA 1.5.1/build 180 PlayerCore / SidebarPlaylistPane / SidebarChaptersPane port.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace Mani_App;

public sealed partial class MainPage
{
    private readonly List<Action> _navigationText = [];
    private readonly Grid _playlistPane = new() { Margin = new(8), RowSpacing = 8, Visibility = Visibility.Collapsed };
    private readonly Grid _chaptersPane = new() { Margin = new(8), RowSpacing = 8, Visibility = Visibility.Collapsed };
    private readonly ListView _playlistList = new() { SelectionMode = ListViewSelectionMode.Extended, CanDragItems = true, CanReorderItems = true, AllowDrop = true };
    private readonly ListView _chapterList = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ComboBox _editionBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _chapterEmpty = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private Button _addFilesButton = null!;
    private PlaylistSnapshot _playlist = new([], false);
    private MediaInfo? _navigationInfo;
    private string _navigationSignature = "", _chapterSignature = "", _editionPath = "";
    private long _observedEdition = -1;
    private bool _navigationUpdating;
    private long _nextNavigationRead;
    private long _nextPlaylistRead, _observedPlaylistRevision = -1;
    private int _sourcesRevision;
    private bool _playlistDragging;
    private readonly string _playlistDragOwner = Guid.NewGuid().ToString("N");

    private void InitializeNavigation()
    {
        void Identify(FrameworkElement element, string key, string? id = null)
        {
            AutomationProperties.SetAutomationId(element, id ?? key);
            _navigationText.Add(() => AutomationProperties.SetName(element, UiText.Get(key)));
        }
        Button Button(Panel parent, string key, string glyph, RoutedEventHandler action)
        {
            var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 16 }, Width = 36, Height = 36, Style = (Style)Application.Current.Resources["GlassIconButton"] };
            Identify(button, key); _navigationText.Add(() => ToolTipService.SetToolTip(button, UiText.Get(key)));
            button.Click += action; parent.Children.Add(button); return button;
        }
        MenuFlyoutItem Menu(MenuFlyoutSubItem parent, string key, RoutedEventHandler action, string shortcut = "")
        {
            var item = new MenuFlyoutItem { KeyboardAcceleratorTextOverride = shortcut };
            Identify(item, key, key + "Menu"); _navigationText.Add(() => item.Text = UiText.Get(key));
            item.Click += action; parent.Items.Add(item); return item;
        }
        var folder = Menu(FileMenu, "OpenFolder", OpenFolder_Click);
        FileMenu.Items.Remove(folder); FileMenu.Items.Insert(1, folder);
        var savePlaylist = Menu(FileMenu, "SavePlaylist", SavePlaylist_Click);
        MainMenu.Opened += async (_, _) =>
        {
            try { savePlaylist.IsEnabled = _session is not null && (await _session.GetPlaylistAsync()).Entries.Count > 0; }
            catch (OperationCanceledException) { savePlaylist.IsEnabled = false; }
        };
        Menu(PlaybackMenu, "Playlist", (_, _) => ShowNavigationPane(QuickPane.Playlist), "Ctrl+Shift+P");
        Menu(PlaybackMenu, "PreviousItem", async (_, _) => await NavigatePlaylistAsync(false), "Ctrl+Left");
        Menu(PlaybackMenu, "NextItem", async (_, _) => await NavigatePlaylistAsync(true), "Ctrl+Right");
        Menu(PlaybackMenu, "Chapters", (_, _) => ShowNavigationPane(QuickPane.Chapters), "Ctrl+Shift+C");
        Menu(PlaybackMenu, "PreviousChapter", async (_, _) => await ChangeChapterAsync(-1), "Ctrl+Shift+<");
        Menu(PlaybackMenu, "NextChapter", async (_, _) => await ChangeChapterAsync(1), "Ctrl+Shift+>");
        Menu(PlaybackMenu, "Edition", async (_, _) => await CycleEditionAsync(), "Shift+E");
        Menu(LoopMenu, "LoopPlaylist", async (_, _) => await Execute(s => s.SetLoopAsync(PlaybackLoop.Playlist)));
        foreach (var (key, glyph) in new[] { ("Playlist", "\uE8FD"), ("Chapters", "\uE8A5") })
        {
            var tab = new ListBoxItem { Content = new FontIcon { Glyph = glyph, FontSize = 14 } };
            Identify(tab, key, "Tab" + key); _navigationText.Add(() => ToolTipService.SetToolTip(tab, UiText.Get(key)));
            QuickPage.Items.Add(tab);
        }
        foreach (var pane in new[] { _playlistPane, _chaptersPane })
        {
            pane.RowDefinitions.Add(new() { Height = GridLength.Auto });
            pane.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
            Grid.SetRow(pane, 1); SidebarPageGrid.Children.Add(pane);
        }
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        _addFilesButton = Button(toolbar, "AddFiles", "\uE710", (_, _) => { });
        var addMenu = new MenuFlyout(); _addFilesButton.Flyout = addMenu;
        foreach (var (key, id, action) in new (string, string, RoutedEventHandler)[]
        {
            ("AddFiles", "AddPlaylistFiles", (_, _) => Open_Click(_addFilesButton, new())),
            ("AddFolder", "AddPlaylistFolder", async (_, _) => await PickFolderAsync(append: true)),
            ("AddUrl", "AddPlaylistUrl", async (_, _) => await PromptUrlAsync(append: true))
        })
        {
            var item = new MenuFlyoutItem(); Identify(item, key, id);
            _navigationText.Add(() => item.Text = UiText.Get(key)); item.Click += action; addMenu.Items.Add(item);
        }
        _navigationText.Add(() => { AutomationProperties.SetName(_addFilesButton, UiText.Get("AddToPlaylist")); ToolTipService.SetToolTip(_addFilesButton, UiText.Get("AddToPlaylist")); });
        Button(toolbar, "Remove", "\uE738", async (_, _) => await Execute(s => s.RemovePlaylistEntriesAsync(SelectedPlaylistIds())));
        Button(toolbar, "Shuffle", "\uE8B1", async (_, _) => await Execute(s => s.TogglePlaylistShuffleAsync()));
        var sort = Button(toolbar, "SortName", "\uE8CB", (_, _) => { });
        var sortMenu = new MenuFlyout(); sort.Flyout = sortMenu;
        foreach (var (key, path, descending) in new[] { ("SortName", false, false), ("SortPath", true, false), ("SortDescending", false, true), ("SortPathDescending", true, true) })
        {
            var item = new MenuFlyoutItem(); Identify(item, key, key + "Menu");
            _navigationText.Add(() => item.Text = UiText.Get(key));
            item.Click += async (_, _) => await Execute(s => s.SortPlaylistAsync(path, descending)); sortMenu.Items.Add(item);
        }
        Button(toolbar, "MoveUp", "\uE74A", async (_, _) => await MoveSelectedAsync(-1));
        Button(toolbar, "MoveDown", "\uE74B", async (_, _) => await MoveSelectedAsync(1));
        Button(toolbar, "ClearPlaylist", "\uE74D", async (_, _) => await Execute(s => s.ClearPlaylistAsync()));
        _playlistPane.Children.Add(toolbar); Grid.SetRow(_playlistList, 1); _playlistPane.Children.Add(_playlistList);
        Identify(_playlistList, "Playlist", "PlaylistList"); Identify(_chapterList, "Chapters", "ChapterList"); Identify(_editionBox, "Edition");
        _navigationText.Add(() => { _editionBox.Header = UiText.Get("Edition"); _chapterEmpty.Text = UiText.Get("NoChapters"); });
        _chaptersPane.Children.Add(_editionBox); Grid.SetRow(_chapterList, 1); _chaptersPane.Children.Add(_chapterList);
        Grid.SetRow(_chapterEmpty, 1); _chaptersPane.Children.Add(_chapterEmpty);
        _playlistList.DoubleTapped += async (_, e) => { e.Handled = true; await PlaySelectedAsync(); };
        _playlistList.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await PlaySelectedAsync(); }
            if (e.Key == Windows.System.VirtualKey.Delete) { e.Handled = true; await Execute(s => s.RemovePlaylistEntriesAsync(SelectedPlaylistIds())); }
        };
        _playlistList.DragItemsStarting += (_, e) =>
        {
            _playlistDragging = true;
            e.Data.Properties["ManiPlaylistOwner"] = _playlistDragOwner;
            // WinUI drag event items can be container content. Read the selected
            // containers in list order, as IINA does with its ordered IndexSet.
            var sources = _playlistList.Items.OfType<ListViewItem>().Where(row => row.IsSelected)
                .Select(row => ((PlaylistEntry)row.Tag).Path).ToArray();
            e.Data.Properties["ManiPlaylistSources"] = string.Join("\n", sources);
            e.Data.SetText(string.Join("\n", sources));
            e.Data.RequestedOperation = DataPackageOperation.Move | DataPackageOperation.Copy;
            Trace("playlist-drag-start", new { count = e.Items.Count, sources = sources.Length });
        };
        _playlistList.DragOver += (_, e) =>
        {
            if (e.DataView.Properties.TryGetValue("ManiPlaylistOwner", out var owner))
            { e.AcceptedOperation = Equals(owner, _playlistDragOwner) ? DataPackageOperation.Move : DataPackageOperation.Copy; e.Handled = !Equals(owner, _playlistDragOwner); return; }
            e.AcceptedOperation = SupportsMediaDrop(e.DataView) ? DataPackageOperation.Copy : DataPackageOperation.None;
            e.Handled = true;
        };
        _playlistList.DragItemsCompleted += async (_, e) =>
        {
            try
            {
                if (e.DropResult == DataPackageOperation.Move)
                {
                    var order = _playlistList.Items.OfType<ListViewItem>().Select(row => ((PlaylistEntry)row.Tag).Id).ToArray();
                    await Execute(session => session.ReorderPlaylistAsync(order));
                }
                Trace("playlist-drag-completed", new { result = e.DropResult.ToString() });
            }
            finally { _playlistDragging = false; _navigationSignature = ""; _nextNavigationRead = 0; }
        };
        _playlistList.Drop += Playlist_Drop;
        _chapterList.DoubleTapped += async (_, e) => { e.Handled = true; await PlaySelectedChapterAsync(); };
        _chapterList.PreviewKeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await PlaySelectedChapterAsync(); } };
        _editionBox.SelectionChanged += async (_, _) =>
        {
            if (!_navigationUpdating && _editionBox.SelectedItem is ComboBoxItem { Tag: int edition } && _navigationInfo is { } info)
                await Execute(s => s.SetMediaPropertyAsync(info.RequestId, info.PlaylistEntryId, "edition", edition.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        };
        foreach (var refresh in _navigationText) refresh();
    }
    private void ShowNavigationPane(QuickPane pane, bool toggle = true)
    {
        if (IsPictureInPicture) Owner?.ExitPictureInPicture();
        if (IsMusicMode) SetMusicMode(false, automatically: false);
        if (!toggle && _sidebar.WantsVisible && _quickPane == pane) return;
        _requestedQuickPane = pane; _pendingLayoutToggle = true;
        if (_menuOpen) MainMenu.Hide(); else ApplyPendingLayoutSidebar();
    }
    private async void SavePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || Owner is null || _closing || _pickerOpen || _dialogOpen) return;
        _pickerOpen = true; MainMenu.Hide(); RefreshOscHolds();
        try
        {
            var picker = new FileSavePicker(Owner.AppWindow.Id) { SuggestedFileName = "Playlist.m3u8" };
            picker.FileTypeChoices.Add("M3U8", [".m3u8"]);
            var file = await picker.PickSaveFileAsync();
            if (file is null || _closing) return;
            // Snapshot at confirmation, matching IINA's save-panel completion.
            await PlaylistFile.SaveAsync(file.Path, await _session.GetPlaylistAsync());
        }
        catch (Exception error) { ShowError(error, "SavePlaylistError"); }
        finally { _pickerOpen = false; RefreshOscHolds(); WakeOsc(); RestorePlayerMenuFocus(); }
    }
    private async Task OpenSourcesAsync(IEnumerable<string> sources, bool append = false, long? beforeId = null)
    {
        int operation = ++_sourcesRevision;
        var files = await Task.Run(() => MediaSources.Expand(sources));
        if (_closing || operation != _sourcesRevision || _session is null) return;
        if (files.Count == 0) { ShowNotice("NoPlayableFiles"); return; }
        if (!append || (await _session.GetPlaylistAsync()).Entries.Count == 0)
        {
            if (!await OpenFileAsync(files[0], operation)) return;
            if (operation != _sourcesRevision || _closing) return;
            await Execute(s => s.AppendPlaylistAsync(files.Skip(1)));
        }
        else await Execute(s => s.AppendPlaylistAsync(files, beforeId: beforeId));
        _nextNavigationRead = 0;
        if (files.Count > 1 || append) ShowNavigationPane(QuickPane.Playlist, toggle: false);
    }
    private async void OpenFolder_Click(object sender, RoutedEventArgs e) => await PickFolderAsync();
    private async Task PickFolderAsync(bool append = false)
    {
        if (_pickerOpen || _dialogOpen || _closing || Owner is null) return;
        _pickerOpen = true; RefreshOscHolds();
        try { var folder = await new FolderPicker(Owner.AppWindow.Id).PickSingleFolderAsync(); if (folder is not null) await OpenSourcesAsync([folder.Path], append); }
        catch (Exception error) { ShowError(error); }
        finally { _pickerOpen = false; RefreshOscHolds(); WakeOsc(); }
    }
    private long[] SelectedPlaylistIds() => _playlistList.SelectedItems.OfType<ListViewItem>().Select(i => ((PlaylistEntry)i.Tag).Id).ToArray();
    private Task PlaySelectedAsync() => _playlistList.SelectedItem is ListViewItem { Tag: PlaylistEntry item } ? PlayEntryAsync(item.Id) : Task.CompletedTask;
    private async Task PlayEntryAsync(long id)
    {
        ++_sourcesRevision; CancelTimelinePreview(); await FlushHistoryAsync();
        await Execute(s => s.PlayPlaylistEntryAsync(id, _settings.PauseWhenOpen));
    }
    private async Task NavigatePlaylistAsync(bool next)
    {
        ++_sourcesRevision; CancelTimelinePreview(); await FlushHistoryAsync();
        await Execute(async session =>
        {
            var playlist = await session.GetPlaylistAsync();
            int index = playlist.Entries.ToList().FindIndex(e => e.Playing || e.Current);
            if (!next && index == 0) { await session.SeekAsync(0); return; }
            int target = index + (next ? 1 : -1);
            if (target >= 0 && target < playlist.Entries.Count) await session.PlayPlaylistEntryAsync(playlist.Entries[target].Id, _settings.PauseWhenOpen);
        });
    }
    private Task MoveSelectedAsync(int direction) => Execute(s => s.MovePlaylistSelectionAsync(SelectedPlaylistIds(), direction));
    private static bool SupportsMediaDrop(DataPackageView data) => data.Contains(StandardDataFormats.StorageItems) ||
        data.Contains(StandardDataFormats.WebLink) || data.Contains(StandardDataFormats.Text);
    private static async Task<IReadOnlyList<string>> ReadMediaDropAsync(DataPackageView data)
    {
        if (data.Properties.TryGetValue("ManiPlaylistSources", out var sources) && sources is string text) return MediaSources.FromDropText(text);
        if (data.Contains(StandardDataFormats.StorageItems)) return (await data.GetStorageItemsAsync()).Select(f => f.Path).ToArray();
        if (data.Contains(StandardDataFormats.WebLink)) return MediaSources.FromDropText((await data.GetWebLinkAsync()).OriginalString);
        return data.Contains(StandardDataFormats.Text) ? MediaSources.FromDropText(await data.GetTextAsync()) : [];
    }
    private async void Playlist_Drop(object sender, DragEventArgs e)
    {
        if (e.DataView.Properties.TryGetValue("ManiPlaylistOwner", out var owner) && Equals(owner, _playlistDragOwner)) return;
        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            if (_closing || _dialogOpen || _pickerOpen) return;
            long? before = null;
            double y = e.GetPosition(_playlistList).Y;
            foreach (var row in _playlistList.Items.OfType<ListViewItem>())
            {
                if (row.ActualHeight <= 0 || row.Tag is not PlaylistEntry entry) continue;
                double middle = row.TransformToVisual(_playlistList).TransformPoint(new()).Y + row.ActualHeight / 2;
                if (y < middle) { before = entry.Id; break; }
            }
            var sources = await ReadMediaDropAsync(e.DataView);
            if (sources.Count > 0) await OpenSourcesAsync(sources, append: true, beforeId: before);
            else ShowNotice("NoPlayableFiles");
        }
        catch (Exception error) { ShowError(error); }
        finally { deferral.Complete(); }
    }
    private async Task ObserveNavigationAsync(PlaybackSnapshot snapshot, int revision)
    {
        if (_session is null || _playlistDragging || Environment.TickCount64 < _nextNavigationRead) return;
        _nextNavigationRead = Environment.TickCount64 + 500;
        var playlist = _playlist;
        long playlistRevision = _session.PlaylistRevision;
        if (playlistRevision != _observedPlaylistRevision || Environment.TickCount64 >= _nextPlaylistRead)
        {
            playlist = await _session.GetPlaylistAsync();
            if (_closing || revision != _playbackRevision) return;
            _observedPlaylistRevision = playlistRevision;
            _nextPlaylistRead = Environment.TickCount64 + 2000;
            _playlist = playlist;
        }
        string signature = UiText.Language + "/" + playlist.Shuffled + "/" + string.Join("|", playlist.Entries.Select(e => $"{e.Id}:{e.Current}:{e.Playing}:{e.Title}"));
        if (signature != _navigationSignature)
        {
            var selected = SelectedPlaylistIds().ToHashSet();
            var rows = _playlistList.Items.OfType<ListViewItem>().ToDictionary(row => ((PlaylistEntry)row.Tag).Id);
            bool hadFocus = Within(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, _playlistList);
            var ids = playlist.Entries.Select(entry => entry.Id).ToHashSet();
            foreach (var row in rows.Values.Where(row => !ids.Contains(((PlaylistEntry)row.Tag).Id)).ToArray()) _playlistList.Items.Remove(row);
            int rowIndex = 0;
            foreach (var entry in playlist.Entries)
            {
                string title = entry.Title.Length > 0 ? entry.Title : Path.GetFileName(entry.Path);
                if (!rows.TryGetValue(entry.Id, out var row)) row = new ListViewItem { Content = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis }, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                row.Tag = entry; ((TextBlock)row.Content).Text = (entry.Playing ? "▶ " : "") + title;
                if (rowIndex >= _playlistList.Items.Count || !ReferenceEquals(_playlistList.Items[rowIndex], row)) { _playlistList.Items.Remove(row); _playlistList.Items.Insert(rowIndex, row); }
                rowIndex++;
                AutomationProperties.SetAutomationId(row, "PlaylistEntry_" + entry.Id);
                AutomationProperties.SetName(row, (entry.Playing ? UiText.Get("PlayingMarker") + ": " : "") + title);
                ToolTipService.SetToolTip(row, entry.Path);
                if (selected.Contains(entry.Id) && !_playlistList.SelectedItems.Contains(row)) _playlistList.SelectedItems.Add(row);
            }
            if (hadFocus && _playlistList.SelectedItem is ListViewItem focused) focused.Focus(FocusState.Programmatic);
            _navigationSignature = signature;
        }
        if (snapshot.Loading || snapshot.Path.Length == 0) { _navigationInfo = null; UpdateChapterMarkers(); return; }
        try
        {
            var info = await PollMediaInfoAsync();
            if (_closing || revision != _playbackRevision || !QuickSettings.Matches(info, snapshot)) return;
            _navigationInfo = info;
            UpdateChapterMarkers();
            if (_editionPath == info.Path && _observedEdition != info.Edition && info.Edition >= 0 && info.Edition < info.Editions.Count)
            {
                var edition = info.Editions[(int)info.Edition];
                ShowOsdMessage(new(OsdKind.Navigation, edition.Title, Value: edition.Index + 1), snapshot); UpdateOsdView();
            }
            _editionPath = info.Path; _observedEdition = info.Edition;
            string chapters = UiText.Language + $"/{info.RequestId}/{info.PlaylistEntryId}/{info.Edition}/" + string.Join("|", info.Chapters);
            if (chapters == _chapterSignature)
            {
                foreach (var row in _chapterList.Items.OfType<ListViewItem>())
                    if (row.Tag is MediaChapter chapter) ((TextBlock)row.Content).Text = ChapterRow(chapter, info.Chapter);
                return;
            }
            _navigationUpdating = true;
            try
            {
                int? selection = (_chapterList.SelectedItem as ListViewItem)?.Tag is MediaChapter selected ? selected.Index : null;
                bool hadFocus = Within(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, _chapterList);
                _chapterList.Items.Clear();
                foreach (var chapter in info.Chapters)
                {
                    var row = new ListViewItem { Tag = chapter, Content = new TextBlock { Text = ChapterRow(chapter, info.Chapter), TextWrapping = TextWrapping.Wrap } };
                    AutomationProperties.SetAutomationId(row, "Chapter_" + chapter.Index); AutomationProperties.SetName(row, ChapterTitle(chapter)); _chapterList.Items.Add(row);
                    if (chapter.Index == selection) { _chapterList.SelectedItem = row; if (hadFocus) row.Focus(FocusState.Programmatic); }
                }
                _chapterEmpty.Visibility = info.Chapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                _editionBox.Items.Clear();
                foreach (var edition in info.Editions) _editionBox.Items.Add(new ComboBoxItem { Tag = edition.Index, Content = edition.Title.Length > 0 ? edition.Title : string.Format(UiText.Get("EditionFormat"), edition.Index + 1) });
                _editionBox.Visibility = info.Editions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                _editionBox.SelectedIndex = (int)info.Edition; _editionBox.IsEnabled = info.Editions.Count > 1;
                _chapterSignature = chapters;
            }
            finally { _navigationUpdating = false; }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
    }
    private static string ChapterTitle(MediaChapter chapter) => chapter.HasTitle ? chapter.Title : string.Format(UiText.Get("ChapterFormat"), chapter.Index);
    private static string ChapterRow(MediaChapter chapter, long current) => $"{(chapter.Index == current ? "▶ " : "")}{Time(chapter.StartTime)}  {ChapterTitle(chapter)}";
    private Task PlaySelectedChapterAsync() => _chapterList.SelectedItem is ListViewItem { Tag: MediaChapter chapter } && _navigationInfo is { } info
        ? Execute(s => s.PlayChapterAsync(info.RequestId, info.PlaylistEntryId, chapter.StartTime)) : Task.CompletedTask;
    private Task ChangeChapterAsync(int direction) => _snapshot is { } snapshot && _navigationInfo is { Chapters.Count: > 0 }
        ? Execute(s => s.ChangeMediaPropertyAsync(snapshot.RequestId, snapshot.PlaylistEntryId, "chapter", direction.ToString(System.Globalization.CultureInfo.InvariantCulture))) : Task.CompletedTask;
    private Task CycleEditionAsync() => _snapshot is { } snapshot && _navigationInfo is { Editions.Count: > 1 }
        ? Execute(s => s.ChangeMediaPropertyAsync(snapshot.RequestId, snapshot.PlaylistEntryId, "edition", "cycle")) : Task.CompletedTask;
}
