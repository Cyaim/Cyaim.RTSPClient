using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Cyaim.RTSPServer.Config;
using Cyaim.RTSPServer.Dashboard.Services;

namespace Cyaim.RTSPServer.Dashboard;

/// <summary>
/// 添加/编辑流对话框
/// </summary>
public class AddStreamDialog : Window
{
    public string StreamPath { get; private set; } = "";
    public string StreamName { get; private set; } = "";
    public string Description { get; private set; } = "";
    public string? SourceUrl { get; private set; }
    public MediaSourceType SourceType { get; private set; } = MediaSourceType.TestPattern;
    public VideoCodecType VideoCodec { get; private set; } = VideoCodecType.H264;
    public int VideoWidth { get; private set; } = 1920;
    public int VideoHeight { get; private set; } = 1080;
    public int Framerate { get; private set; } = 25;
    public bool EnableAudio { get; private set; } = true;
    public AudioCodecType AudioCodec { get; private set; } = AudioCodecType.PCMA;

    private TextBox? _pathBox;
    private TextBox? _nameBox;
    private TextBox? _descBox;
    private TextBox? _sourceUrlBox;
    private ComboBox? _sourceCombo;
    private ComboBox? _codecCombo;
    private TextBox? _widthBox;
    private TextBox? _heightBox;
    private TextBox? _fpsBox;
    private CheckBox? _audioCheck;
    private ComboBox? _audioCodecCombo;

    // Modern UI Colors
    private static readonly SolidColorBrush PrimaryBrush = new(Color.FromRgb(37, 99, 235));
    private static readonly SolidColorBrush TextBrush = new(Color.FromRgb(30, 41, 59));
    private static readonly SolidColorBrush TextSecondaryBrush = new(Color.FromRgb(100, 116, 139));
    private static readonly SolidColorBrush InputBorderBrush = new(Color.FromRgb(226, 232, 240));
    private static readonly SolidColorBrush HoverBrush = new(Color.FromRgb(241, 245, 249));
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(22, 163, 74));
    private static readonly SolidColorBrush DangerBrush = new(Color.FromRgb(220, 38, 38));

    public AddStreamDialog()
    {
        Title = "Add New Stream";
        Width = 520;
        Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));

        var mainBorder = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(16),
            Padding = new Thickness(24),
            Effect = new DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 1,
                Opacity = 0.08,
                Color = Colors.Black
            }
        };

        var mainStack = new StackPanel();

        // Header
        var header = new TextBlock
        {
            Text = "Add New Stream",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush,
            Margin = new Thickness(0, 0, 0, 24)
        };
        mainStack.Children.Add(header);

        // Stream Info Section
        AddSectionHeader(mainStack, "Stream Information");
        
        AddLabel(mainStack, "Stream Path");
        _pathBox = AddTextBox(mainStack, "/live/camera1");
        
        AddLabel(mainStack, "Stream Name");
        _nameBox = AddTextBox(mainStack, "Camera 1");
        
        AddLabel(mainStack, "Description");
        _descBox = AddTextBox(mainStack, "");

        // Source Section
        AddSectionHeader(mainStack, "Source Configuration");
        
        AddLabel(mainStack, "Source Type");
        _sourceCombo = new ComboBox
        {
            ItemsSource = Enum.GetValues(typeof(MediaSourceType)),
            SelectedItem = MediaSourceType.TestPattern,
            Margin = new Thickness(0, 0, 0, 16),
            Padding = new Thickness(10, 8, 10, 8),
            FontSize = 13,
            BorderBrush = InputBorderBrush
        };
        _sourceCombo.SelectionChanged += OnSourceTypeChanged;
        mainStack.Children.Add(_sourceCombo);

        AddLabel(mainStack, "Source URL (for File/RtspPull)");
        _sourceUrlBox = AddTextBox(mainStack, "");
        _sourceUrlBox.IsEnabled = false;

        // Video Section
        AddSectionHeader(mainStack, "Video Settings");
        
        AddLabel(mainStack, "Video Codec");
        _codecCombo = new ComboBox
        {
            ItemsSource = Enum.GetValues(typeof(VideoCodecType)),
            SelectedItem = VideoCodecType.H264,
            Margin = new Thickness(0, 0, 0, 16),
            Padding = new Thickness(10, 8, 10, 8),
            FontSize = 13,
            BorderBrush = InputBorderBrush
        };
        mainStack.Children.Add(_codecCombo);

        // Resolution
        AddLabel(mainStack, "Resolution");
        var resGrid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        resGrid.ColumnDefinitions.Add(new ColumnDefinition());
        resGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        resGrid.ColumnDefinitions.Add(new ColumnDefinition());

        _widthBox = new TextBox { Text = "1920", Padding = new Thickness(10, 8, 10, 8), FontSize = 13, BorderBrush = BorderBrush };
        Grid.SetColumn(_widthBox, 0);
        resGrid.Children.Add(_widthBox);

        var xLabel = new TextBlock
        {
            Text = "×",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            Foreground = TextSecondaryBrush
        };
        Grid.SetColumn(xLabel, 1);
        resGrid.Children.Add(xLabel);

        _heightBox = new TextBox { Text = "1080", Padding = new Thickness(10, 8, 10, 8), FontSize = 13, BorderBrush = BorderBrush };
        Grid.SetColumn(_heightBox, 2);
        resGrid.Children.Add(_heightBox);
        mainStack.Children.Add(resGrid);

        AddLabel(mainStack, "Framerate (FPS)");
        _fpsBox = AddTextBox(mainStack, "25");

        // Audio Section
        AddSectionHeader(mainStack, "Audio Settings");
        
        _audioCheck = new CheckBox
        {
            Content = "Enable Audio",
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 16),
            Foreground = TextBrush,
            FontSize = 13
        };
        mainStack.Children.Add(_audioCheck);

        AddLabel(mainStack, "Audio Codec");
        _audioCodecCombo = new ComboBox
        {
            ItemsSource = Enum.GetValues(typeof(AudioCodecType)),
            SelectedItem = AudioCodecType.PCMA,
            Margin = new Thickness(0, 0, 0, 16),
            Padding = new Thickness(10, 8, 10, 8),
            FontSize = 13,
            BorderBrush = InputBorderBrush
        };
        mainStack.Children.Add(_audioCodecCombo);

        // Buttons
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0)
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(20, 10, 20, 10),
            Margin = new Thickness(0, 0, 12, 0),
            Background = Brushes.White,
            Foreground = TextBrush,
            BorderBrush = InputBorderBrush,
            BorderThickness = new Thickness(1),
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        cancelButton.Click += (s, e) => DialogResult = false;

        var okButton = new Button
        {
            Content = "💾  Save Stream",
            Padding = new Thickness(24, 10, 24, 10),
            Background = PrimaryBrush,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        okButton.Click += OnOkClick;

        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(okButton);
        mainStack.Children.Add(buttonPanel);

        mainBorder.Child = mainStack;
        Content = mainBorder;
    }

    /// <summary>
    /// 初始化为编辑模式
    /// </summary>
    public void SetEditMode(StreamViewModel stream)
    {
        Title = "Edit Stream";
        
        // 安全地更新 header
        if (Content is Border border && border.Child is StackPanel mainStack && mainStack.Children.Count > 0)
        {
            if (mainStack.Children[0] is TextBlock header)
                header.Text = "Edit Stream";
        }

        _pathBox!.Text = stream.Path;
        _nameBox!.Text = stream.Name;
        _descBox!.Text = stream.Description;
        _pathBox.IsEnabled = false; // 路径不可编辑
        
        // Source
        if (Enum.TryParse<MediaSourceType>(stream.SourceType, out var sourceType))
            _sourceCombo!.SelectedItem = sourceType;
        _sourceUrlBox!.Text = stream.SourceUrl ?? "";
        
        // Video
        if (Enum.TryParse<VideoCodecType>(stream.VideoCodec, out var codec))
            _codecCombo!.SelectedItem = codec;
        _widthBox!.Text = stream.VideoWidth > 0 ? stream.VideoWidth.ToString() : "1920";
        _heightBox!.Text = stream.VideoHeight > 0 ? stream.VideoHeight.ToString() : "1080";
        _fpsBox!.Text = stream.Framerate.ToString();
        
        // Audio
        _audioCheck!.IsChecked = stream.EnableAudio;
        if (Enum.TryParse<AudioCodecType>(stream.AudioCodec, out var audioCodec))
            _audioCodecCombo!.SelectedItem = audioCodec;
    }

    private void OnSourceTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sourceUrlBox == null) return;
        
        var sourceType = (MediaSourceType)(_sourceCombo?.SelectedItem ?? MediaSourceType.TestPattern);
        _sourceUrlBox.IsEnabled = sourceType != MediaSourceType.TestPattern;
    }

    private void AddSectionHeader(Panel parent, string text)
    {
        parent.Children.Add(new Border
        {
            Background = HoverBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 16, 0, 12),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush
            }
        });
    }

    private void AddLabel(Panel parent, string text)
    {
        parent.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = TextSecondaryBrush,
            Margin = new Thickness(0, 0, 0, 6)
        });
    }

    private TextBox AddTextBox(Panel parent, string defaultValue)
    {
        var textBox = new TextBox
        {
            Text = defaultValue,
            Margin = new Thickness(0, 0, 0, 16),
            Padding = new Thickness(10, 8, 10, 8),
            FontSize = 13,
            BorderBrush = InputBorderBrush,
            BorderThickness = new Thickness(1)
        };
        parent.Children.Add(textBox);
        return textBox;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        StreamPath = _pathBox?.Text ?? "/live/camera1";
        StreamName = _nameBox?.Text ?? "Camera 1";
        Description = _descBox?.Text ?? "";
        SourceUrl = _sourceUrlBox?.Text;
        SourceType = (MediaSourceType)(_sourceCombo?.SelectedItem ?? MediaSourceType.TestPattern);
        VideoCodec = (VideoCodecType)(_codecCombo?.SelectedItem ?? VideoCodecType.H264);
        VideoWidth = int.TryParse(_widthBox?.Text, out var w) ? w : 1920;
        VideoHeight = int.TryParse(_heightBox?.Text, out var h) ? h : 1080;
        Framerate = int.TryParse(_fpsBox?.Text, out var fps) ? fps : 25;
        EnableAudio = _audioCheck?.IsChecked ?? true;
        AudioCodec = (AudioCodecType)(_audioCodecCombo?.SelectedItem ?? AudioCodecType.PCMA);

        if (string.IsNullOrWhiteSpace(StreamPath))
        {
            MessageBox.Show("Stream path is required.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!StreamPath.StartsWith("/"))
        {
            StreamPath = "/" + StreamPath;
        }

        DialogResult = true;
    }
}
