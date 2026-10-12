using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Controls;

namespace RemoteSpace.App;

public sealed partial class MainPage
{
    private void DevicePrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is RecyclingContentHost host) host.Prepare(host.DataContext);
    }
    private void DeviceClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is RecyclingContentHost host) host.Recycle();
    }
}
