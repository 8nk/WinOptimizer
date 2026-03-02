using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace WinOptimizer.Controls;

public partial class ShimmerText : UserControl
{
    public static readonly StyledProperty<string> DisplayTextProperty =
        AvaloniaProperty.Register<ShimmerText, string>(nameof(DisplayText), "WinOptimizer");

    public static readonly StyledProperty<double> TextSizeProperty =
        AvaloniaProperty.Register<ShimmerText, double>(nameof(TextSize), 20);

    public static readonly StyledProperty<string> BaseColorProperty =
        AvaloniaProperty.Register<ShimmerText, string>(nameof(BaseColor), "#80FFFFFF");

    public static readonly StyledProperty<string> ShimmerColorProperty =
        AvaloniaProperty.Register<ShimmerText, string>(nameof(ShimmerColor), "#FFFFFF");

    public string DisplayText
    {
        get => GetValue(DisplayTextProperty);
        set => SetValue(DisplayTextProperty, value);
    }

    public double TextSize
    {
        get => GetValue(TextSizeProperty);
        set => SetValue(TextSizeProperty, value);
    }

    public string BaseColor
    {
        get => GetValue(BaseColorProperty);
        set => SetValue(BaseColorProperty, value);
    }

    public string ShimmerColor
    {
        get => GetValue(ShimmerColorProperty);
        set => SetValue(ShimmerColorProperty, value);
    }

    private readonly DispatcherTimer _timer;
    private double _phase;
    private TextBlock? _textBlock;

    public ShimmerText()
    {
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _timer.Tick += OnTimerTick;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        CreateTextBlock();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DisplayTextProperty ||
            change.Property == TextSizeProperty ||
            change.Property == BaseColorProperty ||
            change.Property == ShimmerColorProperty)
        {
            CreateTextBlock();
        }
    }

    private void CreateTextBlock()
    {
        TextCanvas.Children.Clear();

        _textBlock = new TextBlock
        {
            Text = DisplayText,
            FontSize = TextSize,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        TextCanvas.Children.Add(_textBlock);
        UpdateShimmer();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _phase += 0.025;
        if (_phase >= Math.PI * 2)
            _phase -= Math.PI * 2;

        UpdateShimmer();
    }

    private void UpdateShimmer()
    {
        if (_textBlock == null) return;

        var baseCol = Color.Parse(BaseColor);
        var shimmerCol = Color.Parse(ShimmerColor);

        var t = (Math.Sin(_phase) + 1) / 2;

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
        };

        var highlightCenter = t;
        var highlightWidth = 0.25;

        var left = Math.Max(0, highlightCenter - highlightWidth);
        var right = Math.Min(1, highlightCenter + highlightWidth);

        brush.GradientStops.Add(new GradientStop(baseCol, 0));

        if (left > 0.01)
            brush.GradientStops.Add(new GradientStop(baseCol, left));

        brush.GradientStops.Add(new GradientStop(shimmerCol, highlightCenter));

        if (right < 0.99)
            brush.GradientStops.Add(new GradientStop(baseCol, right));

        brush.GradientStops.Add(new GradientStop(baseCol, 1));

        _textBlock.Foreground = brush;
    }
}
