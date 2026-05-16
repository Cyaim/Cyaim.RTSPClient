using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Cyaim.RTSPServer.Dashboard;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;
    private DateTime _startTime;

    public MainWindow()
    {
        InitializeComponent();
        
        _startTime = DateTime.Now;
        
        // 设置刷新定时器
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();
        
        // 初始化日志
        Log("Server dashboard initialized");
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        // 更新运行时间
        var uptime = DateTime.Now - _startTime;
        UptimeText.Text = $"{uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";
        
        // TODO: 从服务器获取真实数据
        // ActiveStreamsCount.Text = server.GetStatus().StreamCount.ToString();
        // ConnectedClientsCount.Text = server.GetStatus().ActiveConnections.ToString();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        // 切换到 Settings 标签
        var tabControl = (TabControl)((Grid)Content).Children[1];
        tabControl.SelectedIndex = 3;
    }

    private void OnStartStopClick(object sender, RoutedEventArgs e)
    {
        // TODO: 启动/停止服务器
        if (StatusText.Text.Contains("Running"))
        {
            StatusText.Text = "● Stopped";
            StatusText.Foreground = System.Windows.Media.Brushes.Red;
            StartStopButton.Content = "▶ Start";
            Log("Server stopped");
        }
        else
        {
            StatusText.Text = "● Running";
            StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            StartStopButton.Content = "⏹ Stop";
            _startTime = DateTime.Now;
            Log("Server started");
        }
    }

    private void OnAddStream(object sender, RoutedEventArgs e)
    {
        // TODO: 显示添加流对话框
        var dialog = new AddStreamDialog();
        if (dialog.ShowDialog() == true)
        {
            Log($"Stream added: {dialog.StreamPath}");
        }
    }

    private void OnStreamSelected(object sender, SelectionChangedEventArgs e)
    {
        // TODO: 显示流详情
    }

    private void OnClearLogs(object sender, RoutedEventArgs e)
    {
        LogsTextBox.Clear();
    }

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        // TODO: 保存设置
        Log("Settings saved");
        MessageBox.Show("Settings saved successfully. Restart server to apply changes.", 
            "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogsTextBox.AppendText($"[{timestamp}] {message}\n");
        LogsTextBox.ScrollToEnd();
    }
}

/// <summary>
/// 添加流对话框
/// </summary>
public class AddStreamDialog : Window
{
    public string StreamPath { get; private set; } = "";
    public string StreamName { get; private set; } = "";

    public AddStreamDialog()
    {
        Title = "Add New Stream";
        Width = 400;
        Height = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        
        var stack = new StackPanel { Margin = new Thickness(16) };
        
        stack.Children.Add(new TextBlock { Text = "Stream Path:", Margin = new Thickness(0,0,0,4) });
        var pathBox = new TextBox { Text = "/live/camera1" };
        stack.Children.Add(pathBox);
        
        stack.Children.Add(new TextBlock { Text = "Stream Name:", Margin = new Thickness(0,12,0,4) });
        var nameBox = new TextBox { Text = "Camera 1" };
        stack.Children.Add(nameBox);
        
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,16,0,0) };
        var okButton = new Button { Content = "OK", Width = 80, Margin = new Thickness(0,0,8,0) };
        var cancelButton = new Button { Content = "Cancel", Width = 80 };
        
        okButton.Click += (s, e) =>
        {
            StreamPath = pathBox.Text;
            StreamName = nameBox.Text;
            DialogResult = true;
        };
        
        cancelButton.Click += (s, e) => DialogResult = false;
        
        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        stack.Children.Add(buttonPanel);
        
        Content = stack;
    }
}
