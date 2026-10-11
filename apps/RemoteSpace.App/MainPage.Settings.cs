using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Controls;
using RemoteSpace.Core;
using RemoteSpace.Profiles;
using RemoteSpace.Transport;
using RemoteSpace.Workspace;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;

namespace RemoteSpace.App;

public sealed partial class MainPage
{
    private void BuildSettings()
    {
        SettingsBody.Children.Clear(); var settings = _catalog.Settings;
        SettingsBody.Children.Add(new TextBlock { Text = "Appearance", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 0) });
        var theme = new ComboBox { Header = "App theme", ItemsSource = Enum.GetNames<WorkspaceTheme>(), SelectedIndex = (int)settings.Theme, MinWidth = 240 };
        AutomationProperties.SetName(theme, "App theme");
        theme.SelectionChanged += async (_, _) => { await Mutate(c => c.Configure(c.Settings with { Theme = (WorkspaceTheme)theme.SelectedIndex })); ApplyTheme(_catalog.Settings.Theme); }; SettingsBody.Children.Add(theme);
        SettingsBody.Children.Add(new TextBlock { Text = "Browser connection bridge", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var endpoint = new TextBox { Header = "Bridge endpoint", PlaceholderText = "wss://your-bridge.example/session", Text = settings.BridgeEndpoint, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(endpoint, "Bridge endpoint"); SettingsBody.Children.Add(endpoint);
        SettingsBody.Children.Add(new TextBlock { Text = "The static app cannot open TCP connections. Use the included bridge on a trusted machine and allow only your own RDP targets and this app's origin. Leave this blank on desktop to connect directly. Tokens are requested per connection, never stored in settings or URLs.", TextWrapping = TextWrapping.Wrap, Opacity = .7 });
        var save = new Button { Content = "Save bridge endpoint" }; save.Click += async (_, _) => await Mutate(c => c.Configure(c.Settings with { BridgeEndpoint = endpoint.Text.Trim() })); SettingsBody.Children.Add(save);
        var export = new Button { Content = "Export saved devices" }; export.Click += async (_, _) => { try { await SaveText("RemoteSpace workspace", "RemoteSpace-workspace", ".json", _catalog.Serialize()); } catch (Exception e) { ShowError(e.Message); } }; SettingsBody.Children.Add(export);
        var import = new Button { Content = "Import saved devices" }; import.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json"); var file = await picker.PickSingleFileAsync(); if (file is null) return;
                if ((await file.GetBasicPropertiesAsync()).Size > DeviceCatalog.MaximumDocumentBytes) throw new IOException("Workspace import exceeds the size limit.");
                var devices = DeviceCatalog.Parse(await FileIO.ReadTextAsync(file)).Snapshot().Devices;
                if (await Confirm("Import devices", $"Import {devices.Length} saved PCs? Existing IDs will be updated. Connection secrets are not part of this file.", "Import")) await Mutate(c => c.UpsertRange(devices.Select(d => d.Profile)));
            }
            catch (Exception e) { ShowError(e.Message); }
        }; SettingsBody.Children.Add(import);
        SettingsBody.Children.Add(new TextBlock { Text = "Diagnostics", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var diagnostics = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 250, Text = _diagnostics.Count == 0 ? "No session diagnostics yet." : string.Join("\n", _diagnostics) }; SettingsBody.Children.Add(diagnostics);
        SettingsBody.Children.Add(new TextBlock { Text = "Implemented preview: direct RDP/NLA/TLS engine, bitmap updates, input, text clipboard, credential-free device management and multi-session workspace. Not full Windows App parity. Cloud subscriptions, RemoteApp, RD Gateway, modern GFX codecs, audio and device redirection are not implemented.", TextWrapping = TextWrapping.Wrap, Opacity = .7 });
    }
}
