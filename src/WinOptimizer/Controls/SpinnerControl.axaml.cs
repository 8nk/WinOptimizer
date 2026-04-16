using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace WinOptimizer.Controls;

public partial class SpinnerControl : UserControl
{
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<SpinnerControl, double>(nameof(Size), 80);

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private readonly DispatcherTimer _timer;
    private double _anglePrimary;
    private double _angleSecondary;
    private double _angleTertiary;
    private double _pulsePhase;

    public SpinnerControl()
    {
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += OnTimerTick;

        PropertyChanged += (_, e) =>
        {
            if (e.Property == SizeProperty)
                UpdateAllArcs();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateAllArcs();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _anglePrimary += 4.0;
        _angleSecondary += 2.5;
        _angleTertiary -= 1.5;

        if (_anglePrimary >= 360) _anglePrimary -= 360;
        if (_angleSecondary >= 360) _angleSecondary -= 360;
        if (_angleTertiary <= -360) _angleTertiary += 360;

        _pulsePhase += 0.06;
        if (_pulsePhase >= Math.PI * 2)
            _pulsePhase -= Math.PI * 2;

        if (ArcPrimary.RenderTransform is RotateTransform rt1)
            rt1.Angle = _anglePrimary;
        if (ArcSecondary.RenderTransform is RotateTransform rt2)
            rt2.Angle = _angleSecondary;
        if (ArcTertiary.RenderTransform is RotateTransform rt3)
            rt3.Angle = _angleTertiary;

        UpdateAllArcs();
    }

    private void UpdateAllArcs()
    {
        var size = Size;
        var strokePrimary = 5.0;
        var strokeSecondary = 3.0;
        var strokeTertiary = 2.0;

        var radiusPrimary = (size - strokePrimary) / 2;
        var radiusSecondary = (size - strokeSecondary) / 2 - 1;
        var radiusTertiary = (size - strokeTertiary) / 2 - 3;

        var center = size / 2;

        var primarySweep = 200.0 + 50.0 * Math.Sin(_pulsePhase);
        ArcPrimary.Data = CreateArcGeometry(center, radiusPrimary, -90, primarySweep);
        ArcSecondary.Data = CreateArcGeometry(center, radiusSecondary, -90, 120);
        ArcTertiary.Data = CreateArcGeometry(center, radiusTertiary, -90, 60);
    }

    private static StreamGeometry CreateArcGeometry(double center, double radius, double startAngle, double sweepAngle)
    {
        var startRad = startAngle * Math.PI / 180;
        var endRad = (startAngle + sweepAngle) * Math.PI / 180;

        var startX = center + radius * Math.Cos(startRad);
        var startY = center + radius * Math.Sin(startRad);
        var endX = center + radius * Math.Cos(endRad);
        var endY = center + radius * Math.Sin(endRad);

        var isLargeArc = Math.Abs(sweepAngle) > 180;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(startX, startY), false);
            ctx.ArcTo(
                new Point(endX, endY),
                new Size(radius, radius),
                0,
                isLargeArc,
                sweepAngle >= 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
            ctx.EndFigure(false);
        }

        return geometry;
    }
}
