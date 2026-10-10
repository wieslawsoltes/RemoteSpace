using Uno.UI.Hosting;
namespace RemoteSpace.App;
public class Program
{
    public static async Task Main(string[] args)
    {
        // Accessibility is initialized by the host before it creates App.
        Uno.UI.FeatureConfiguration.AutomationPeer.AutoEnableAccessibility = true;
        var host = UnoPlatformHostBuilder.Create().App(() => new App()).UseWebAssembly().Build();
        await host.RunAsync();
    }
}
