using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;

namespace WinOptimizer.Controls;

/// <summary>
/// Premium circular progress ring with smooth gradient arc, pulsing tip dot,
/// and clean 360° rendering (no overlap at full circle).
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
    private double _pulsePhase = 0;
    private readonly DispatcherTimer _animTimer;

    // Track ellipse (background ring)
    private Ellipse? _trackRing;
    // Progress arc path
    private Path? _arcPath;
    // Subtle glow behind arc
    private Path? _glowPath;
    // Full circle for 100%
    private Ellipse? _fullCircle;
    private Ellipse? _fullGlow;
    // Tip dot at the end of the arc
    private Ellipse? _tipDot;

    // Premium color scheme
    private static readonly Color ArcStartColor = Color.Parse("#00BCD4");   // Teal
    private static readonly Color ArcMidColor = Color.Parse("#00E5FF");     // Bright cyan
    private static readonly Color ArcEndColor = Color.Parse("#76FF03");     // Lime green
    private static readonly Color GlowColor = Color.Parse("#2000E5FF");    // 12% cyan glow
    private static readonly Color TipColor = Color.Parse("#FFFFFF");        // White tip
    private static readonly Color TrackColor = Color.Parse("#18FFFFFF");    // 9% white track

    public CircularProgressControl()
    {
        InitializeComponent();

        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _animTimer.Tick += OnAnimTick;
        _animTimer.Start();

        // Track ring (background circle) — subtle
        _trackRing = new Ellipse
        {
            Fill = Brushes.Transparent,
            Stroke = new SolidColorBrush(TrackColor),
            StrokeThickness = RingThickness
        };

        // Subtle glow behind arc
        _glowPath = new Path
        {
            Stroke = new SolidColorBrush(GlowColor),
            StrokeThickness = RingThickness + 6,
            StrokeLineCap = PenLineCap.Round,
            Fill = Brushes.Transparent
        };

        // Progress arc
        _arcPath = new Path
        {
            StrokeThickness = RingThickness,
            StrokeLineCap = PenLineCap.Round,
            Fill = Brushes.Transparent
        };

        // Full circle (shown at ~100%)
        _fullGlow = new Ellipse
        {
            Fill = Brushes.Transparent,
            StrokeThickness = RingThickness + 6,
            IsVisible = false
        };
        _fullCircle = new Ellipse
        {
            Fill = Brushes.Transparent,
            StrokeThickness = RingThickness,
            IsVisible = false
        };

        // Pulsing tip dot at arc end
        _tipDot = new Ellipse
        {
            Fill = new SolidColorBrush(TipColor),
            Width = RingThickness + 4,
            Height = RingThickness + 4,
            IsVisible = false,
            Opacity = 0.9
        };

        ArcCanvas.Children.Add(_trackRing);
        ArcCanvas.Children.Add(_fullGlow);
        ArcCanvas.Children.Add(_fullCircle);
        ArcCanvas.Children.Add(_glowPath);
        ArcCanvas.Children.Add(_arcPath);
        ArcCanvas.Children.Add(_tipDot);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RingThicknessProperty)
        {
            if (_trackRing != null) _trackRing.StrokeThickness = RingThickness;
            if (_arcPath != null) _arcPath.StrokeThickness = RingThickness;
            if (_glowPath != null) _glowPath.StrokeThickness = RingThickness + 6;
            if (_fullCircle != null) _fullCircle.StrokeThickness = RingThickness;
            if (_fullGlow != null) _fullGlow.StrokeThickness = RingThickness + 6;
            if (_tipDot != null)
            {
                _tipDot.Width = RingThickness + 4;
                _tipDot.Height = RingThickness + 4;
            }
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

        var glowExtra = 6.0;
        var totalThick = RingThickness + glowExtra;

        if (_trackRing != null)
        {
            _trackRing.Width = dim - totalThick;
            _trackRing.Height = dim - totalThick;
            Canvas.SetLeft(_trackRing, (size.Width - dim + totalThick) / 2);
            Canvas.SetTop(_trackRing, (size.Height - dim + totalThick) / 2);
        }

        if (_fullCircle != null)
        {
            _fullCircle.Width = dim - totalThick;
            _fullCircle.Height = dim - totalThick;
            Canvas.SetLeft(_fullCircle, (size.Width - dim + totalThick) / 2);
            Canvas.SetTop(_fullCircle, (size.Height - dim + totalThick) / 2);
        }

        if (_fullGlow != null)
        {
            _fullGlow.Width = dim - totalThick;
            _fullGlow.Height = dim - totalThick;
            Canvas.SetLeft(_fullGlow, (size.Width - dim + totalThick) / 2);
            Canvas.SetTop(_fullGlow, (size.Height - dim + totalThick) / 2);
        }

        if (PercentText != null)
        {
            PercentText.FontSize = dim * 0.18;
        }

        UpdateArc(size);
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        _pulsePhase += 0.08;
        if (_pulsePhase > 2 * Math.PI) _pulsePhase -= 2 * Math.PI;

        var target = Progress;
        var diff = target - _displayedProgress;

        if (Math.Abs(diff) > 0.05)
        {
            _displayedProgress += diff * 0.10; // Slightly slower for smoother feel
            UpdateVisuals();
        }
        else if (Math.Abs(diff) > 0.001)
        {
            _displayedProgress = target;
            UpdateVisuals();
        }
        else
        {
            // Still update tip dot pulse even when progress doesn't change
            UpdateTipPulse();
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

    private void UpdateTipPulse()
    {
        if (_tipDot == null || !_tipDot.IsVisible) return;
        // Gentle pulse: opacity 0.6 - 1.0
        _tipDot.Opacity = 0.8 + 0.2 * Math.Sin(_pulsePhase);
    }

    private void UpdateArc(Size size)
    {
        if (_arcPath == null || _glowPath == null || _fullCircle == null ||
            _fullGlow == null || _tipDot == null) return;

        var dim = Math.Min(size.Width, size.Height);
        if (dim < 10) return;

        var progress = Math.Clamp(_displayedProgress, 0, 100);

        if (progress < 0.1)
        {
            _arcPath.Data = null;
            _glowPath.Data = null;
            _fullCircle.IsVisible = false;
            _fullGlow.IsVisible = false;
            _tipDot.IsVisible = false;
            return;
        }

        var glowExtra = 6.0;
        var totalThick = RingThickness + glowExtra;
        var radius = (dim - totalThick) / 2;
        var cx = size.Width / 2;
        var cy = size.Height / 2;

        // Get the arc color based on progress (gradient from teal → cyan → lime)
        var arcBrush = CreateArcBrush(progress, cx, cy, radius);
        _arcPath.Stroke = arcBrush;

        // Glow color matches arc but much more transparent
        var glowBrush = CreateGlowBrush(progress);
        _glowPath.Stroke = glowBrush;

        // At 99.5%+ → full circle (no arc cap overlap)
        if (progress >= 99.5)
        {
            _arcPath.Data = null;
            _glowPath.Data = null;
            _tipDot.IsVisible = false;

            _fullCircle.IsVisible = true;
            _fullGlow.IsVisible = true;
            _fullCircle.Stroke = arcBrush;
            _fullGlow.Stroke = glowBrush;
            return;
        }

        // Normal arc
        _fullCircle.IsVisible = false;
        _fullGlow.IsVisible = false;

        var sweepAngle = progress / 100.0 * 360.0;
        // Cap at 355° to prevent visual glitch at near-full
        if (sweepAngle >= 355) sweepAngle = 355;

        var geometry = CreateArcGeometry(cx, cy, radius, sweepAngle);
        _arcPath.Data = geometry;
        _glowPath.Data = geometry;

        // Position tip dot at the end of the arc
        var endAngleRad = -Math.PI / 2 + sweepAngle * Math.PI / 180.0;
        var tipX = cx + radius * Math.Cos(endAngleRad);
        var tipY = cy + radius * Math.Sin(endAngleRad);

        var tipSize = RingThickness + 4;
        _tipDot.Width = tipSize;
        _tipDot.Height = tipSize;
        Canvas.SetLeft(_tipDot, tipX - tipSize / 2);
        Canvas.SetTop(_tipDot, tipY - tipSize / 2);
        _tipDot.IsVisible = progress > 2;

        // Tip dot pulse
        _tipDot.Opacity = 0.8 + 0.2 * Math.Sin(_pulsePhase);

        // Tip dot color — bright white with subtle glow
        var tipProgress = progress / 100.0;
        var tipColor = InterpolateColor(ArcMidColor, ArcEndColor, tipProgress);
        _tipDot.Fill = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(Color.FromArgb(200, tipColor.R, tipColor.G, tipColor.B), 0.6),
                new GradientStop(Color.FromArgb(0, tipColor.R, tipColor.G, tipColor.B), 1.0)
            }
        };
    }

    /// <summary>
    /// Creates a gradient brush for the arc that shifts color based on progress.
    /// Low progress = teal, mid = cyan, high = lime green.
    /// </summary>
    private IBrush CreateArcBrush(double progress, double cx, double cy, double radius)
    {
        var t = progress / 100.0;

        // Smooth 3-color gradient based on progress
        Color mainColor;
        if (t < 0.5)
        {
            mainColor = InterpolateColor(ArcStartColor, ArcMidColor, t * 2);
        }
        else
        {
            mainColor = InterpolateColor(ArcMidColor, ArcEndColor, (t - 0.5) * 2);
        }

        // Add slight brightness variation for "living" feel
        var brightness = 1.0 + 0.05 * Math.Sin(_pulsePhase * 0.5);
        var r = (byte)Math.Clamp(mainColor.R * brightness, 0, 255);
        var g = (byte)Math.Clamp(mainColor.G * brightness, 0, 255);
        var b = (byte)Math.Clamp(mainColor.B * brightness, 0, 255);

        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    /// <summary>
    /// Creates a subtle glow brush matching the arc color.
    /// </summary>
    private IBrush CreateGlowBrush(double progress)
    {
        var t = progress / 100.0;

        Color mainColor;
        if (t < 0.5)
        {
            mainColor = InterpolateColor(ArcStartColor, ArcMidColor, t * 2);
        }
        else
        {
            mainColor = InterpolateColor(ArcMidColor, ArcEndColor, (t - 0.5) * 2);
        }

        // 15% opacity glow
        return new SolidColorBrush(Color.FromArgb(38, mainColor.R, mainColor.G, mainColor.B));
    }

    private static Color InterpolateColor(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            255,
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    /// <summary>
    /// Creates arc geometry using PathGeometry + ArcSegment.
    /// No SVG string parsing — no locale/comma issues.
    /// </summary>
    private static PathGeometry CreateArcGeometry(double cx, double cy, double radius, double sweepAngleDeg)
    {
        var startAngleRad = -Math.PI / 2; // 12 o'clock
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
