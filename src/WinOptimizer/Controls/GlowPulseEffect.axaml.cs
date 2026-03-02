using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;

namespace WinOptimizer.Controls;

public partial class GlowPulseEffect : UserControl
{
    public static readonly StyledProperty<string> GlowColorProperty =
        AvaloniaProperty.Register<GlowPulseEffect, string>(nameof(GlowColor), "#00BCD4");

    public string GlowColor
    {
        get => GetValue(GlowColorProperty);
        set => SetValue(GlowColorProperty, value);
    }

    private readonly DispatcherTimer _timer;
    private double _phase;
    private readonly Ellipse[] _rings = new Ellipse[3];

    public GlowPulseEffect()
    {
        InitializeComponent();

        for (int i = 0; i < 3; i++)
        {
            _rings[i] = new Ellipse
            {
                IsHitTestVisible = false,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
        }

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _timer.Tick += OnTimerTick;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        GlowCanvas.Children.Clear();
        foreach (var ring in _rings)
            GlowCanvas.Children.Add(ring);

        UpdateGlowRings();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _phase += 0.04;
        if (_phase >= Math.PI * 2)
            _phase -= Math.PI * 2;

        UpdateGlowRings();
    }

    private void UpdateGlowRings()
    {
        var w = Bounds.Width > 0 ? Bounds.Width : 140;
        var h = Bounds.Height > 0 ? Bounds.Height : 140;
        var centerX = w / 2;
        var centerY = h / 2;

        var color = Color.Parse(GlowColor);

        double[] baseRadii = { 0.55, 0.75, 1.0 };
        double[] phaseOffsets = { 0, 0.8, 1.6 };
        double[] baseOpacities = { 0.18, 0.10, 0.05 };

        var maxDim = Math.Min(w, h);

        for (int i = 0; i < 3; i++)
        {
            var pulse = (Math.Sin(_phase + phaseOffsets[i]) + 1) / 2;
            var scale = 0.85 + pulse * 0.3;
            var opacity = baseOpacities[i] * (0.5 + pulse * 0.5);

            var diameter = maxDim * baseRadii[i] * scale;
            _rings[i].Width = diameter;
            _rings[i].Height = diameter;

            var gradientBrush = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative)
            };

            gradientBrush.GradientStops.Add(new GradientStop(
                Color.FromArgb((byte)(opacity * 255), color.R, color.G, color.B), 0));
            gradientBrush.GradientStops.Add(new GradientStop(
                Color.FromArgb((byte)(opacity * 0.3 * 255), color.R, color.G, color.B), 0.6));
            gradientBrush.GradientStops.Add(new GradientStop(
                Color.FromArgb(0, color.R, color.G, color.B), 1.0));

            _rings[i].Fill = gradientBrush;
            _rings[i].Opacity = 1;

            Canvas.SetLeft(_rings[i], centerX - diameter / 2);
            Canvas.SetTop(_rings[i], centerY - diameter / 2);
        }
    }
}
