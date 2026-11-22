using System.Windows;
using Velopack;

namespace RemoteDesk;

public partial class App : Application
{
    [STAThread]
    private static void Main()
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
