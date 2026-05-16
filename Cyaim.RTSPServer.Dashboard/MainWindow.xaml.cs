using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cyaim.RTSPServer.Config;
using Cyaim.RTSPServer.Dashboard.Services;
using Microsoft.Extensions.Logging;

namespace Cyaim.RTSPServer.Dashboard;

public partial class MainWindow : Window
{
    private readonly RtspServerService _serverService;
    private readonly DispatcherTimer _refreshTimer;
    private readonly ObservableCollection<StreamViewModel> _streams = new();
    private readonly ObservableCollection<LogEntry> _logs = new();

    public MainWindow()
    {
        InitializeComponent();

        // 获取服务
        _serverService = App.GetService<RtspServerService>() 
            ?? throw new InvalidOperationException("Server service not available");

        // 绑定数据
        StreamsGrid.ItemsSource = _streams;
        StreamList.ItemsSource = _streams;

        // 订阅事件
        _serverService.StatusChanged += OnServerStatusChanged;
        _serverService.LogReceived += OnLogReceived;

        // 初始化流列表
        foreach (var stream in _serverService.Streams)
        {
            _streams.Add(stream);
        }

        // 设置刷新定时器
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();

        Log("Dashboard initialized");
    }

    #region 服务器控制

    private async void OnStartStopClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_serverService.IsRunning)
            {
                await _serverService.StopAsync();
            }
            else
            {
                await _serverService.StartAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Server Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnRestartClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _serverService.RestartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Server Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnServerStatusChanged(object? sender, ServerStatusEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (e.IsRunning)
            {
                StatusText.Text = "● Running";
                StatusText.Foreground = Brushes.LightGreen;
                StartStopButton.Content = "⏹ Stop";
            }
            else
            {
                StatusText.Text = "● Stopped";
                StatusText.Foreground = Brushes.Red;
                StartStopButton.Content = "▶ Start";
            }
        });
    }

    #endregion

    #region 流管理

    private async void OnAddStream(object sender, RoutedEventArgs e)
    {
        var dialog = new AddStreamDialog();
        if (dialog.ShowDialog() == true)
        {
            var config = new StreamConfig
            {
                Path = dialog.StreamPath,
                Name = dialog.StreamName,
                Description = dialog.Description,
                SourceType = dialog.SourceType,
                VideoCodec = dialog.VideoCodec,
                Width = dialog.Width,
                Height = dialog.Height,
                Framerate = dialog.Framerate,
                EnableAudio = dialog.EnableAudio,
                AudioCodec = dialog.AudioCodec
            };

            if (await _serverService.AddStreamAsync(config))
            {
                RefreshStreams();
            }
        }
    }

    private async void OnRemoveStream(object sender, RoutedEventArgs e)
    {
        if (StreamList.SelectedItem is StreamViewModel selected)
        {
            var result = MessageBox.Show(
                $"Remove stream '{selected.Name}' ({selected.Path})?", 
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                await _serverService.RemoveStreamAsync(selected.Path);
                RefreshStreams();
            }
        }
    }

    private void OnStreamSelected(object sender, SelectionChangedEventArgs e)
    {
        if (StreamList.SelectedItem is StreamViewModel selected)
        {
            ShowStreamDetails(selected);
        }
    }

    private void ShowStreamDetails(StreamViewModel stream)
    {
        StreamDetailsPanel.Children.Clear();

        var header = new TextBlock
        {
            Text = stream.Name,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 16)
        };
        StreamDetailsPanel.Children.Add(header);

        AddDetailRow("Path", stream.Path);
        AddDetailRow("Description", stream.Description);
        AddDetailRow("Source Type", stream.SourceType);
        AddDetailRow("Video Codec", stream.VideoCodec);
        AddDetailRow("Resolution", stream.Resolution);
        AddDetailRow("Framerate", $"{stream.Framerate} fps");
        AddDetailRow("Status", stream.Status);
        AddDetailRow("Active Clients", stream.ActiveClients.ToString());
    }

    private void AddDetailRow(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var labelText = new TextBlock
        {
            Text = label + ":",
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.Gray
        };
        Grid.SetColumn(labelText, 0);

        var valueText = new TextBlock { Text = value };
        Grid.SetColumn(valueText, 1);

        grid.Children.Add(labelText);
        grid.Children.Add(valueText);
        StreamDetailsPanel.Children.Add(grid);
    }

    private void RefreshStreams()
    {
        _streams.Clear();
        foreach (var stream in _serverService.Streams)
        {
            _streams.Add(stream);
        }
    }

    #endregion

    #region 监控

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        var stats = _serverService.GetStatistics();

        // 更新统计卡片
        ActiveStreamsCount.Text = stats.ActiveStreams.ToString();
        ConnectedClientsCount.Text = stats.ActiveConnections.ToString();
        BandwidthText.Text = $"{stats.BandwidthMbps:F1} Mbps";

        var uptime = stats.Uptime;
        UptimeText.Text = $"{(int)uptime.TotalHours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";

        // 更新流列表
        RefreshStreams();
    }

    #endregion

    #region 日志

    private void OnLogReceived(object? sender, LogEntry log)
    {
        Dispatcher.Invoke(() =>
        {
            _logs.Add(log);

            // 限制日志数量
            while (_logs.Count > 1000)
                _logs.RemoveAt(0);

            // 更新日志显示
            var timestamp = log.Timestamp.ToString("HH:mm:ss");
            var level = log.Level.ToString().ToUpper();
            LogsTextBox.AppendText($"[{timestamp}] [{level}] {log.Message}\n");
            LogsTextBox.ScrollToEnd();
        });
    }

    private void OnClearLogs(object sender, RoutedEventArgs e)
    {
        _logs.Clear();
        LogsTextBox.Clear();
    }

    private void Log(string message)
    {
        OnLogReceived(this, new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = LogLevel.Information,
            Message = message
        });
    }

    #endregion

    #region 设置

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        // TODO: 保存设置到配置文件
        MessageBox.Show("Settings saved. Restart server to apply changes.", 
            "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        Log("Settings saved");
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        MainTabControl.SelectedIndex = 3; // Settings tab
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _serverService.StatusChanged -= OnServerStatusChanged;
        _serverService.LogReceived -= OnLogReceived;
        base.OnClosed(e);
    }
}
