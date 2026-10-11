using Microsoft.UI.Xaml;
namespace RemoteSpace.App;
public sealed partial class App : Application
{
    public static Window MainWindow { get; private set; } = null!;
    public App() { InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new Window { Title = "RemoteSpace", Content = new MainPage() }; MainWindow.Activate();
    }
}
