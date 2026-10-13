using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Controls;
using RemoteSpace.Core;
using RemoteSpace.Input;

namespace RemoteSpace.App;

public sealed partial class MainPage
{
    private void AddKeyboardCommands(SessionCommandBar bar, OpenSession opened)
    {
        bar.AddMenu("Keyboard", "\uE765", () =>
        {
            bool enabled = opened.Session.State == SessionState.Active && !opened.Closing &&
                opened.View.IsEnabled && opened.View.IsPresentationActive &&
                (opened.Session is not IRemoteGraphicsOutput output || output.OutputEnabled);
            var menu = new MenuFlyout();
            foreach (var command in RemoteKeyboardCommands.All)
            {
                var selected = command;
                if (command.Command is RemoteKeyboardCommand.StartMenu or RemoteKeyboardCommand.PrintScreen)
                    menu.Items.Add(new MenuFlyoutSeparator());
                var item = bar.MenuCommand(command.Label, () =>
                {
                    // Revalidate against the live view when invoked, not only
                    // when the menu was opened. Do not move local focus remotely
                    // until its launching gesture has finished.
                    opened.View.SendKeyboardCommand(selected.Command);
                    Status.Text = "Sent " + selected.Keys + " to " + opened.Profile.Name + ".";
                    return Task.CompletedTask;
                }, enabled);
                item.KeyboardAcceleratorTextOverride = command.Keys;
                AutomationProperties.SetHelpText(item, "Send " + command.Keys + " to this remote desktop only. The remote operating system determines its action.");
                menu.Items.Add(item);
            }
            return menu;
        });
    }
}
