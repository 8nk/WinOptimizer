using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;

namespace WinOptimizer.Controls;

/// <summary>
/// Circular progress ring with animated percentage (micro-percentages: 15.3%).
/// Uses ArcSegment instead of SVG path to avoid locale issues.
/// </summary>
public partial class CircularProgressControl : UserControl
{
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<CircularProgressControl, double>(nameof(Progress), 0);

    public static readonly StyledProperty<double> RingThicknessProperty =
        AvaloniaProperty.Register<CircularProgressControl, double>(nameof(RingThickness), 8);

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public double RingThickness
    {
        get => GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    private double _displayedProgress = 0;
    private readonly DispatcherTimer _animTimer;

    // Track ellipse (background ring)
    private Ellipse? _trackRing;
    // Progress arc path
    private Path? _arcPath;
    // Glow effect behind arc
    private Path? _glowPath;

    public CircularProgressControl()
    {
        InitializeComponent();

        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _animTimer.Tick += OnAnimTick;
        _animTimer.Start();

        // Track ring (background circle)
        _trackRing = new Ellipse
        {
            Fill = Brushes.Transparent,
            Stroke = new SolidColorBrush(Color.Parse("#30FFFFFF")),
            StrokeThickness = RingThickness
        };

        // Glow behind arc (wider, semi-transparent)
        _glowPath = new Path
        {
            Stroke = new SolidColorBrush(Color.Parse("#4000BCD4")),
            StrokeThickness = RingThickness + 8,
            StrokeLineCap = PenLineCap.Round,
            Fill = Brushes.Transparent
        };

        // Progress arc — solid bright cyan, round caps
        _arcPath = new Path
        {
            Stroke = new SolidColorBrush(Color.Parse("#00E5FF")),
            StrokeThickness = RingThickness,
            StrokeLineCap = PenLineCap.Round,
            Fill = Brushes.Transparent
        };

        ArcCanvas.Children.Add(_trackRing);
        ArcCanvas.Children.Add(_glowPath);
        ArcCanvas.Children.Add(_arcPath);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RingThicknessProperty)
        {
            if (_trackRing != null) _trackRing.StrokeThickness = RingThickness;
            if (_arcPath != null) _arcPath.StrokeThickness = RingThickness;
            if (_glowPath != null) _glowPath.StrokeThickness = RingThickness + 8;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        UpdateLayout(finalSize);
        return result;
    }

    private void UpdateLayout(Size size)
    {
        var dim = Math.Min(size.Width, size.Height);
        if (dim < 10) return;

        // Glow extends beyond the arc stroke — account for it
        var glowExtra = 8.0; // glow StrokeThickness = thick + 8
        var totalThick = RingThickness + glowExtra;

        if (_trackRing != null)
        {
            // Track ring sized to match arc center (with glow padding)
            _trackRing.Width = dim - totalThick;
            _trackRing.Height = dim - totalThick;
            Canvas.SetLeft(_trackRing, (size.Width - dim + totalThick) / 2);
            Canvas.SetTop(_trackRing, (size.Height - dim + totalThick) / 2);
        }

        if (PercentText != null)
        {
            PercentText.FontSize = dim * 0.2;
        }

        UpdateArc(size);
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        var target = Progress;
        var diff = target - _displayedProgress;

        if (Math.Abs(diff) > 0.05)
        {
            _displayedProgress += diff * 0.12;
            UpdateVisuals();
        }
        else if (Math.Abs(diff) > 0.001)
        {
            _displayedProgress = target;
            UpdateVisuals();
        }
    }

    private void UpdateVisuals()
    {
        if (PercentText != null)
        {
            if (_displayedProgress > 0 && _displayedProgress < 100)
            {
                PercentText.Text = $"{_displayedProgress:F1}%";
                PercentText.IsVisible = true;
            }
            else if (_displayedProgress >= 100)
            {
                PercentText.Text = "100%";
                PercentText.IsVisible = true;
            }
            else
            {
                PercentText.IsVisible = false;
            }
        }

        UpdateArc(Bounds.Size);
    }

    private void UpdateArc(Size size)
    {
        if (_arcPath == null || _glowPath == null) return;

        var dim = Math.Min(size.Width, size.Height);
        if (dim < 10) return;

        var progress = Math.Clamp(_displayedProgress, 0, 100);
        if (progress < 0.1)
        {
            _arcPath.Data = null;
            _glowPath.Data = null;
            return;
        }

        // Account for glow thickness so arc stays within bounds
        var glowExtra = 8.0;
        var totalThick = RingThickness + glowExtra;
        var radius = (dim - totalThick) / 2;
        var cx = size.Width / 2;
        var cy = size.Height / 2;

        var sweepAngle = progress / 100.0 * 360.0;
        if (sweepAngle >= 359.99) sweepAngle = 359.99;

        // Build geometry using ArcSegment — NO locale/string issues!
        var geometry = CreateArcGeometry(cx, cy, radius, sweepAngle);
        _arcPath.Data = geometry;
        _glowPath.Data = geometry;
    }

    /// <summary>
    /// Creates arc geometry using PathGeometry + ArcSegment.
    /// Avoids SVG path string parsing — no locale/comma issues.
    /// </summary>
    private static PathGeometry CreateArcGeometry(double cx, double cy, double radius, double sweepAngleDeg)
    {
        var startAngleRad = -Math.PI / 2; // 12 o'clock position
        var endAngleRad = startAngleRad + sweepAngleDeg * Math.PI / 180.0;

        var startX = cx + radius * Math.Cos(startAngleRad);
        var startY = cy + radius * Math.Sin(startAngleRad);
        var endX = cx + radius * Math.Cos(endAngleRad);
        var endY = cy + radius * Math.Sin(endAngleRad);

        var isLargeArc = sweepAngleDeg > 180;

        var figure = new PathFigure
        {
            StartPoint = new Point(startX, startY),
            IsClosed = false,
            IsFilled = false
        };

        figure.Segments!.Add(new ArcSegment
        {
            Point = new Point(endX, endY),
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = isLargeArc,
            RotationAngle = 0
        });

        var geometry = new PathGeometry();
        geometry.Figures!.Add(figure);
        return geometry;
    }
}
