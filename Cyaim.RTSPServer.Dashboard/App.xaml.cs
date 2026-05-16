using System.Windows;
using Cyaim.RTSPServer.Dashboard.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cyaim.RTSPServer.Dashboard;

public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 配置依赖注入
        _host = ServiceConfiguration.CreateHostBuilder(e.Args).Build();

        // 设置全局异常处理
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show($"Unhandled exception: {args.Exception.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 获取服务
    /// </summary>
    public static T? GetService<T>() where T : class
    {
        return ((App)Current)._host?.Services.GetService<T>();
    }
}
