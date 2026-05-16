using System.Windows;

namespace Cyaim.RTSPServer.Dashboard;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        // 设置全局异常处理
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show($"Unhandled exception: {args.Exception.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
