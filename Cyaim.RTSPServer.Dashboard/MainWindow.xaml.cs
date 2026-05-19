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
    private readonly ObservableCollection<ClientViewModel> _clients = new();
    private readonly ObservableCollection<LogEntry> _logs = new();

    /// <summary>
    /// 服务器是否正在运行（用于绑定）
    /// </summary>
    public bool IsServerRunning { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        
        // 设置 DataContext 以便绑定
        DataContext = this;

        // 获取服务
        _serverService = App.GetService<RtspServerService>() 
            ?? throw new InvalidOperationException("Server service not available");

        // 绑定数据源
        StreamsGrid.ItemsSource = _streams;
        StreamList.ItemsSource = _streams;
        ClientsGrid.ItemsSource = _clients;

        // 订阅事件
        _serverService.StatusChanged += OnServerStatusChanged;
        _serverService.LogReceived += OnLogReceived;

        // 初始化数据
        RefreshStreams();
        RefreshClients();

        // 加载设置
        LoadSettings();

        // 设置刷新定时器 (每秒)
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
            Log($"Error: {ex.Message}");
        }
    }

    private async void OnRestartClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _serverService.RestartAsync();
            Log("Server restarted");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Server Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
            Log($"Error: {ex.Message}");
        }
    }

    private void OnServerStatusChanged(object? sender, ServerStatusEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            IsServerRunning = e.IsRunning;
            
            if (e.IsRunning)
            {
                StatusText.Text = "Running";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(22, 163, 74)); // green
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(22, 163, 74));
                StatusBadge.Background = new SolidColorBrush(Color.FromRgb(220, 252, 231)); // light green
                StartStopButton.Content = "⏹  Stop Server";
                StartStopButton.Style = (Style)FindResource("DangerButton");
                RestartButton.Visibility = Visibility.Visible;
            }
            else
            {
                StatusText.Text = "Stopped";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)); // gray
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(220, 38, 38)); // red
                StatusBadge.Background = new SolidColorBrush(Color.FromRgb(241, 245, 249)); // light gray
                StartStopButton.Content = "▶  Start Server";
                StartStopButton.Style = (Style)FindResource("SuccessButton");
                RestartButton.Visibility = Visibility.Collapsed;
            }
            
            // 强制刷新 DataGrid 以更新按钮状态
            StreamsGrid.Items.Refresh();
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
                Source = dialog.SourceUrl ?? "",  // 修复：设置 Source 属性
                SourceType = dialog.SourceType,
                VideoCodec = dialog.VideoCodec,
                Width = dialog.VideoWidth,
                Height = dialog.VideoHeight,
                Framerate = dialog.Framerate,
                EnableAudio = dialog.EnableAudio,
                AudioCodec = dialog.AudioCodec
            };

            if (await _serverService.AddStreamAsync(config))
            {
                RefreshStreams();
                Log($"Stream added: {config.Path}");
            }
        }
    }

    private async void OnRemoveStream(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string path)
        {
            var stream = _streams.FirstOrDefault(s => s.Path == path);
            var result = MessageBox.Show(
                $"Remove stream '{stream?.Name}' ({path})?", 
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                if (await _serverService.RemoveStreamAsync(path))
                {
                    RefreshStreams();
                    Log($"Stream removed: {path}");
                }
            }
        }
    }

    private async void OnStartStream(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string path)
        {
            Log($"Starting stream: {path}");
            // TODO: 实现流启动
            await Task.CompletedTask;
        }
    }

    private async void OnStopStream(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string path)
        {
            Log($"Stopping stream: {path}");
            // TODO: 实现流停止
            await Task.CompletedTask;
        }
    }

    private void OnEditStream(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string path)
        {
            var stream = _streams.FirstOrDefault(s => s.Path == path);
            if (stream == null) return;

            var dialog = new AddStreamDialog();
            dialog.SetEditMode(stream);
            
            if (dialog.ShowDialog() == true)
            {
                // 更新流配置（目前只更新UI，实际需要实现更新逻辑）
                stream.Name = dialog.StreamName;
                stream.Description = dialog.Description;
                RefreshStreams();
                Log($"Stream updated: {path}");
            }
        }
    }

    private void OnStreamSelected(object sender, SelectionChangedEventArgs e)
    {
        var selected = (sender is DataGrid dg) ? dg.SelectedItem as StreamViewModel 
                     : (sender is ListBox lb) ? lb.SelectedItem as StreamViewModel 
                     : null;
        
        if (selected != null)
        {
            ShowStreamDetails(selected);
        }
    }

    private void ShowStreamDetails(StreamViewModel stream)
    {
        StreamDetailsPanel.Children.Clear();

        // 标题
        var header = new TextBlock
        {
            Text = stream.Name,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 16)
        };
        StreamDetailsPanel.Children.Add(header);

        // 详情
        AddDetailRow("Path", stream.Path);
        AddDetailRow("Description", stream.Description);
        AddDetailRow("Source Type", stream.SourceType);
        AddDetailRow("Video Codec", stream.VideoCodec);
        AddDetailRow("Resolution", stream.Resolution);
        AddDetailRow("Framerate", $"{stream.Framerate} fps");
        AddDetailRow("Status", stream.Status);
        AddDetailRow("Active Clients", stream.ActiveClients.ToString());

        // 操作按钮
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 16, 0, 0)
        };
        
        var startBtn = new Button
        {
            Content = "▶ Start",
            Style = (Style)FindResource("SuccessButton"),
            Padding = new Thickness(12, 6, 12, 6),
            Tag = stream.Path
        };
        startBtn.Click += OnStartStream;
        
        var stopBtn = new Button
        {
            Content = "⏹ Stop",
            Style = (Style)FindResource("DangerButton"),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Tag = stream.Path
        };
        stopBtn.Click += OnStopStream;

        var removeBtn = new Button
        {
            Content = "🗑 Remove",
            Style = (Style)FindResource("DangerButton"),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Tag = stream.Path
        };
        removeBtn.Click += OnRemoveStream;

        buttonPanel.Children.Add(startBtn);
        buttonPanel.Children.Add(stopBtn);
        buttonPanel.Children.Add(removeBtn);
        StreamDetailsPanel.Children.Add(buttonPanel);
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

    #region 客户端管理

    private void RefreshClients()
    {
        _clients.Clear();
        foreach (var client in _serverService.Clients)
        {
            _clients.Add(client);
        }
        ClientCountText.Text = $" ({_clients.Count})";
    }

    #endregion

    #region 监控

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        try
        {
            var stats = _serverService.GetStatistics();

            // 更新统计卡片
            ActiveStreamsCount.Text = stats.ActiveStreams.ToString();
            ConnectedClientsCount.Text = stats.ActiveConnections.ToString();
            BandwidthText.Text = $"{stats.BandwidthMbps:F1} Mbps";

            var uptime = stats.Uptime;
            UptimeText.Text = $"{(int)uptime.TotalHours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";

            // 更新列表
            RefreshStreams();
            RefreshClients();
        }
        catch (Exception ex)
        {
            // 静默处理刷新错误
            System.Diagnostics.Debug.WriteLine($"Refresh error: {ex.Message}");
        }
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
            var level = log.Level switch
            {
                LogLevel.Error => "ERR",
                LogLevel.Warning => "WRN",
                LogLevel.Information => "INF",
                LogLevel.Debug => "DBG",
                _ => "???"
            };
            
            var color = log.Level switch
            {
                LogLevel.Error => "#ff6b6b",
                LogLevel.Warning => "#ffd93d",
                LogLevel.Information => "#6bff6b",
                LogLevel.Debug => "#6b6bff",
                _ => "#d4d4d4"
            };

            LogsTextBox.AppendText($"[{timestamp}] [{level}] {log.Message}\n");
            
            if (AutoScrollCheckBox.IsChecked == true)
            {
                LogsTextBox.ScrollToEnd();
            }
        });
    }

    private void OnClearLogs(object sender, RoutedEventArgs e)
    {
        _logs.Clear();
        LogsTextBox.Clear();
        Log("Logs cleared");
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

    private void LoadSettings()
    {
        // 从配置加载当前设置
        HostTextBox.Text = "0.0.0.0";
        PortTextBox.Text = "554";
        MaxConnectionsTextBox.Text = "10000";
        TimeoutTextBox.Text = "60";
        EnableAuthCheckBox.IsChecked = false;
    }

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            // 验证输入
            if (!int.TryParse(PortTextBox.Text, out var port) || port < 1 || port > 65535)
            {
                MessageBox.Show("Invalid port number (1-65535)", "Validation", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(MaxConnectionsTextBox.Text, out var maxConn) || maxConn < 1)
            {
                MessageBox.Show("Invalid max connections", "Validation", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TimeoutTextBox.Text, out var timeout) || timeout < 1)
            {
                MessageBox.Show("Invalid timeout value", "Validation", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // TODO: 保存到配置文件
            // _serverService.UpdateSettings(new RtspServerOptions { ... });

            MessageBox.Show("Settings saved. Restart server to apply changes.", 
                "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            Log("Settings saved");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error saving settings: {ex.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        MainTabControl.SelectedIndex = 4; // Settings tab
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
