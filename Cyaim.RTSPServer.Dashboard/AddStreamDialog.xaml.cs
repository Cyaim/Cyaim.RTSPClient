using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cyaim.RTSPServer.Config;

namespace Cyaim.RTSPServer.Dashboard;

/// <summary>
/// 添加流对话框
/// </summary>
public class AddStreamDialog : Window
{
    public string StreamPath { get; private set; } = "";
    public string StreamName { get; private set; } = "";
    public string Description { get; private set; } = "";
    public MediaSourceType SourceType { get; private set; } = MediaSourceType.TestPattern;
    public VideoCodecType VideoCodec { get; private set; } = VideoCodecType.H264;
    public int Width { get; private set; } = 1920;
    public int Height { get; private set; } = 1080;
    public int Framerate { get; private set; } = 25;
    public bool EnableAudio { get; private set; } = true;
    public AudioCodecType AudioCodec { get; private set; } = AudioCodecType.PCMA;

    private TextBox? _pathBox;
    private TextBox? _nameBox;
    private TextBox? _descBox;
    private ComboBox? _sourceCombo;
    private ComboBox? _codecCombo;
    private TextBox? _widthBox;
    private TextBox? _heightBox;
    private TextBox? _fpsBox;
    private CheckBox? _audioCheck;
    private ComboBox? _audioCodecCombo;

    public AddStreamDialog()
    {
        Title = "Add New Stream";
        Width = 450;
        Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var mainStack = new StackPanel { Margin = new Thickness(16) };

        // Path
        AddLabel(mainStack, "Stream Path:");
        _pathBox = AddTextBox(mainStack, "/live/camera1");

        // Name
        AddLabel(mainStack, "Stream Name:");
        _nameBox = AddTextBox(mainStack, "Camera 1");

        // Description
        AddLabel(mainStack, "Description:");
        _descBox = AddTextBox(mainStack, "");

        // Source Type
        AddLabel(mainStack, "Source Type:");
        _sourceCombo = new ComboBox
        {
            ItemsSource = Enum.GetValues(typeof(MediaSourceType)),
            SelectedItem = MediaSourceType.TestPattern,
            Margin = new Thickness(0, 0, 0, 12)
        };
        mainStack.Children.Add(_sourceCombo);

        // Video Codec
        AddLabel(mainStack, "Video Codec:");
        _codecCombo = new ComboBox
        {
            ItemsSource = Enum.GetValues(typeof(VideoCodecType)),
            SelectedItem = VideoCodecType.H264,
            Margin = new Thickness(0, 0, 0, 12)
        };
        mainStack.Children.Add(_codecCombo);

        // Resolution
        var resGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        resGrid.ColumnDefinitions.Add(new ColumnDefinition());
        resGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        resGrid.ColumnDefinitions.Add(new ColumnDefinition());

        var widthLabel = new TextBlock { Text = "Width:", Margin = new Thickness(0, 0, 0, 4) };
        Grid.SetColumn(widthLabel, 0);
        resGrid.Children.Add(widthLabel);

        _widthBox = new TextBox { Text = "1920", Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(_widthBox, 0);
        resGrid.Children.Add(_widthBox);

        var xLabel = new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(xLabel, 1);
        resGrid.Children.Add(xLabel);

        var heightLabel = new TextBlock { Text = "Height:", Margin = new Thickness(8, 0, 0, 4) };
        Grid.SetColumn(heightLabel, 2);
        resGrid.Children.Add(heightLabel);

        _heightBox = new TextBox { Text = "1080", Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(_heightBox, 2);
        resGrid.Children.Add(_heightBox);

        mainStack.Children.Add(resGrid);

        // Framerate
        AddLabel(mainStack, "Framerate:");
        _fpsBox = AddTextBox(mainStack, "25");

        // Audio
        _audioCheck = new CheckBox
        {
            Content = "Enable Audio",
            IsChecked = true,
            Margin = new Thickness(0, 8, 0, 8)
        };
        mainStack.Children.Add(_audioCheck);

        // Audio Codec
        AddLabel(mainStack, "Audio Codec:");
        _audioCodecCombo = new ComboBox
        {
            ItemsSource = Enum.GetValues(typeof(AudioCodecType)),
            SelectedItem = AudioCodecType.PCMA,
            Margin = new Thickness(0, 0, 0, 12)
        };
        mainStack.Children.Add(_audioCodecCombo);

        // Buttons
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var okButton = new Button
        {
            Content = "OK",
            Width = 80,
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(26, 115, 232)),
            Foreground = Brushes.White
        };
        okButton.Click += OnOkClick;

        var cancelButton = new Button { Content = "Cancel", Width = 80 };
        cancelButton.Click += (s, e) => DialogResult = false;

        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        mainStack.Children.Add(buttonPanel);

        Content = mainStack;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        StreamPath = _pathBox?.Text ?? "/live/camera1";
        StreamName = _nameBox?.Text ?? "Camera 1";
        Description = _descBox?.Text ?? "";
        SourceType = (MediaSourceType)(_sourceCombo?.SelectedItem ?? MediaSourceType.TestPattern);
        VideoCodec = (VideoCodecType)(_codecCombo?.SelectedItem ?? VideoCodecType.H264);
        Width = int.TryParse(_widthBox?.Text, out var w) ? w : 1920;
        Height = int.TryParse(_heightBox?.Text, out var h) ? h : 1080;
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

    private void AddLabel(Panel parent, string text)
    {
        parent.Children.Add(new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 0, 0, 4)
        });
    }

    private TextBox AddTextBox(Panel parent, string defaultValue)
    {
        var textBox = new TextBox
        {
            Text = defaultValue,
            Margin = new Thickness(0, 0, 0, 12)
        };
        parent.Children.Add(textBox);
        return textBox;
    }
}
