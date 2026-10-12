using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Controls;
using RemoteSpace.Core;
using RemoteSpace.Profiles;
using RemoteSpace.Workspace;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace RemoteSpace.App;

public sealed partial class MainPage
{
    private bool _updatingWorkspace, _searchQueued;
    private IReadOnlyList<string>? _shownGroups;
    private string? _group;
    private sealed record GroupChoice(string? Value, string Label);
    private void SearchChanged(object sender, TextChangedEventArgs args)
    {
        if (!_ready || _searchQueued) return;
        _searchQueued = true;
        DispatcherQueue.TryEnqueue(() => { _searchQueued = false; RefreshDevices(); });
    }
    private async void SortChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_ready || _updatingWorkspace || Sort.SelectedIndex < 0) return;
        var sort = (ProfileSort)Sort.SelectedIndex;
        await Mutate(c => c.Configure(c.Settings with { Sort = sort }));
    }
    private async void ReverseSort(object sender, RoutedEventArgs args) => await Mutate(c => c.Configure(c.Settings with { SortDescending = !c.Settings.SortDescending }));
    private async void ShowGrid(object sender, RoutedEventArgs args) => await Mutate(c => c.Configure(c.Settings with { ListView = false }));
    private async void ShowList(object sender, RoutedEventArgs args) => await Mutate(c => c.Configure(c.Settings with { ListView = true }));
    private void GroupChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingWorkspace) return;
        _group = (Groups.SelectedItem as GroupChoice)?.Value; RefreshDevices();
    }
    private void ResetFilters(object sender, RoutedEventArgs args) { _group = null; _shownGroups = null; Search.Text = ""; RefreshDevices(); }
    private void RefreshDevices()
    {
        if (Devices is null || _updatingWorkspace) return;
        _updatingWorkspace = true;
        try
        {
            var settings = _catalog.Settings;
            Sort.SelectedIndex = (int)settings.Sort; ListMode.IsChecked = settings.ListView; GridMode.IsChecked = !settings.ListView;
            var groups = _catalog.Groups;
            if (!ReferenceEquals(_shownGroups, groups))
            {
                if (_group is not null && !groups.Contains(_group)) _group = null;
                var choices = new[] { new GroupChoice(null, "All groups") }.Concat(groups.Select(g => new GroupChoice(g, g.Length == 0 ? "Ungrouped" : g))).ToArray();
                Groups.ItemsSource = choices; Groups.SelectedItem = choices.First(g => g.Value == _group); _shownGroups = groups;
            }
            if (_section is not ("devices" or "favorites")) { Devices.ItemsSource = null; DeviceList.ItemsSource = null; return; }
            var items = _catalog.Query(Search.Text ?? "", _section == "favorites", _group);
            // Only the displayed view owns an ItemsSource. Hidden grids don't keep a
            // second set of realized cards or remeasure on every search.
            Devices.ItemsSource = settings.ListView ? null : items; DeviceList.ItemsSource = settings.ListView ? items : null;
            DeviceGridScroll.Visibility = settings.ListView ? Visibility.Collapsed : Visibility.Visible;
            DeviceListScroll.Visibility = settings.ListView ? Visibility.Visible : Visibility.Collapsed;
            DeviceCount.Text = (_section == "favorites" ? "Favorite devices" : _group ?? "Saved devices") + $" ({items.Count})";
            AutomationProperties.SetName(DeviceCount, DeviceCount.Text);
            Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool filtered = !string.IsNullOrEmpty(Search.Text) || _group is not null;
            bool favorites = _section == "favorites" && !filtered;
            EmptyTitle.Text = filtered ? "No matching devices" : favorites ? "Mark PCs as favorites" : "Add a remote PC";
            EmptyDescription.Text = filtered ? "Try another name, address or group." : favorites ? "Use the star on a device card to keep your PCs within easy reach." : "Connect to your PCs from one place. Add a PC or import an .rdp file to get started.";
            AutomationProperties.SetName(EmptyTitle, EmptyTitle.Text); AutomationProperties.SetName(EmptyDescription, EmptyDescription.Text);
            EmptyIcon.Glyph = favorites ? "\uE734" : "\uE7F4";
            FirstPc.Visibility = !filtered && !favorites ? Visibility.Visible : Visibility.Collapsed;
            ClearFilters.Visibility = filtered ? Visibility.Visible : Visibility.Collapsed;
            GoDevices.Visibility = favorites ? Visibility.Visible : Visibility.Collapsed;
            UpdateTileSize();
        }
        finally { _updatingWorkspace = false; }
    }
    private async Task<bool> Mutate(Action<DeviceCatalog> mutation)
    {
        await _mutations.WaitAsync();
        try
        {
            var candidate = _catalog.Fork(); mutation(candidate);
            if (candidate.Revision != _catalog.Revision) { await _store.SaveAsync(candidate); _catalog = candidate; }
            return true;
        }
        catch (Exception e) { ShowError(e.Message); return false; }
        finally { try { RefreshDevices(); } finally { _mutations.Release(); } }
    }
    private async void ReloadDevices(object sender, RoutedEventArgs args)
    {
        await _mutations.WaitAsync();
        try { _catalog = await _store.LoadAsync(); ApplyTheme(_catalog.Settings.Theme); Status.Text = "Saved devices reloaded. Active sessions were not interrupted."; }
        catch (Exception e) { ShowError(e.Message); }
        finally { try { RefreshDevices(); } finally { _mutations.Release(); } }
    }
    private async void AddPc(object sender, RoutedEventArgs args) => await EditProfile(null);
    private async Task EditProfile(ConnectionProfile? original)
    {
        if (_dialogOpen) return; _dialogOpen = true;
        ConnectionProfile? connectAfter = null;
        try
        {
            var editor = new ProfileEditor(original); var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel { Spacing = 12 }; content.Children.Add(error); content.Children.Add(editor);
            var dialog = new ContentDialog { Title = original is null ? "Add a PC" : "Edit PC", Content = content, PrimaryButtonText = original is null ? "Add" : "Save", SecondaryButtonText = original is null ? "Add & connect" : "", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
            ConnectionProfile? result = null;
            void Validate(ContentDialog sender, ContentDialogButtonClickEventArgs e)
            {
                try { result = editor.ReadProfile(); _ = new DeviceCatalog(new() { Devices = [new(result)] }); }
                catch (Exception ex) { e.Cancel = true; error.Text = ex.Message; }
            }
            dialog.PrimaryButtonClick += Validate; dialog.SecondaryButtonClick += Validate;
            var action = await CommandActivation.ShowDialogAsync(this, dialog);
            if (action != ContentDialogResult.None && result is not null && await Mutate(c => c.Upsert(result)) && action == ContentDialogResult.Secondary) connectAfter = result;
        }
        catch (Exception e) { ShowError(e.Message); }
        finally { _dialogOpen = false; }
        if (connectAfter is not null) await Connect(connectAfter);
    }
    private async void DeviceAction(object sender, DeviceActionEventArgs args)
    {
        var profile = _catalog.Find(args.Profile.Id)?.Profile; if (profile is null) return;
        switch (args.Action)
        {
            case "connect": await Connect(profile); break;
            case "edit": await EditProfile(profile); break;
            case "duplicate": await Mutate(c => c.Duplicate(profile.Id)); break;
            case "details":
                var saved = _catalog.Find(profile.Id);
                await ShowDialog("Device details", new TextBlock { Text = $"{profile.Name}\n\nPC address: {profile.Host}\nPort: {profile.Port}\nGroup: {profile.Group}\nUser name: {profile.UserName}\nDomain: {profile.Domain}\nSecurity: {profile.Security}\nRequested display: {profile.Width} × {profile.Height}, {profile.BitsPerPixel}-bit\nText clipboard: {(profile.RedirectClipboard ? "Allowed" : "Off")}\nLast connected: {saved?.LastConnected?.ToLocalTime().ToString("g") ?? "Never"}\n\nThese are saved connection settings, not live host inventory.", TextWrapping = TextWrapping.Wrap, MaxWidth = 480 }); break;
            case "favorite": await Mutate(c => { if (c.Find(profile.Id)?.Profile is { } current) c.Upsert(current with { Favorite = !current.Favorite }); }); break;
            case "export": await ExportProfile(profile); break;
            case "delete": if (await Confirm("Remove PC", "Remove " + profile.Name + " from saved devices? Active sessions will stay open.", "Remove")) await Mutate(c => c.Remove(profile.Id)); break;
        }
    }
    private async void ImportRdp(object sender, RoutedEventArgs args)
    {
        try
        {
            var file = await _fileAccess.PickAsync(".rdp", RdpFile.MaxBytes); if (file is null) return;
            var imported = RdpFile.Parse(file.Bytes);
            if (await Mutate(c => c.Upsert(imported.Profile)) && imported.Warnings.Count > 0) ShowMessage("RDP import", string.Join("\n", imported.Warnings), InfoBarSeverity.Warning);
        }
        catch (Exception e) { ShowError(e.Message); }
    }
    private async Task ExportProfile(ConnectionProfile profile)
    {
        try { await SaveText("Export PC", profile.Name, ".rdp", RdpFile.Export(profile)); }
        catch (Exception e) { ShowError(e.Message); }
    }
    private async Task SaveText(string title, string name, string extension, string text)
    {
        var bytes = new System.Text.UTF8Encoding(false, true).GetBytes(text);
        if (await _fileAccess.ExportAsync(name + extension, bytes, extension == ".json" ? "application/json" : "application/octet-stream"))
            Status.Text = OperatingSystem.IsBrowser() ? "Download requested. Check your browser's downloads." : "File saved.";
    }
}
