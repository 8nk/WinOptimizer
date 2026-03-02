using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;

namespace WinOptimizer.Controls;

public partial class CelebrationEffect : UserControl
{
    private class Particle
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double VelocityY { get; set; }
        public double SwayPhase { get; set; }
        public double SwaySpeed { get; set; }
        public double SwayAmplitude { get; set; }
        public double Size { get; set; }
        public double Opacity { get; set; }
        public double FadeSpeed { get; set; }
        public double RotationSpeed { get; set; }
        public double Rotation { get; set; }
        public Color Color { get; set; }
        public Ellipse? EllipseElement { get; set; }
        public Path? StarElement { get; set; }
        public bool IsStar { get; set; }
    }

    private readonly List<Particle> _particles = new();
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = Random.Shared;
    private double _spawnAccumulator;

    // Cyan/teal celebration colors
    private static readonly Color[] ParticleColors =
    {
        Color.Parse("#00BCD4"), // cyan
        Color.Parse("#4DD0E1"), // light cyan
        Color.Parse("#FFD700"), // gold
        Color.Parse("#FFC107"), // amber
        Color.Parse("#FFFFFF"), // white
        Color.Parse("#80DEEA"), // pale cyan
        Color.Parse("#E0F7FA"), // very light cyan
        Color.Parse("#26C6DA"), // mid cyan
    };

    private const string StarPathData = "M12,2L15.09,8.26L22,9.27L17,14.14L18.18,21.02L12,17.77L5.82,21.02L7,14.14L2,9.27L8.91,8.26Z";

    public CelebrationEffect()
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
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        _particles.Clear();
        ParticleCanvas.Children.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        _spawnAccumulator += 0.6;
        while (_spawnAccumulator >= 1.0)
        {
            _spawnAccumulator -= 1.0;
            SpawnParticle(width, height);
        }

        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Y += p.VelocityY;
            p.SwayPhase += p.SwaySpeed;
            var swayX = Math.Sin(p.SwayPhase) * p.SwayAmplitude;
            p.Rotation += p.RotationSpeed;
            p.Opacity -= p.FadeSpeed;

            if (p.Y < -p.Size * 2 || p.Opacity <= 0)
            {
                if (p.EllipseElement != null)
                    ParticleCanvas.Children.Remove(p.EllipseElement);
                if (p.StarElement != null)
                    ParticleCanvas.Children.Remove(p.StarElement);
                _particles.RemoveAt(i);
                continue;
            }

            if (p.IsStar && p.StarElement != null)
            {
                Canvas.SetLeft(p.StarElement, p.X + swayX - p.Size / 2);
                Canvas.SetTop(p.StarElement, p.Y - p.Size / 2);
                p.StarElement.Opacity = Math.Max(0, p.Opacity);
                p.StarElement.RenderTransform = new RotateTransform(p.Rotation);
            }
            else if (p.EllipseElement != null)
            {
                Canvas.SetLeft(p.EllipseElement, p.X + swayX - p.Size / 2);
                Canvas.SetTop(p.EllipseElement, p.Y - p.Size / 2);
                p.EllipseElement.Opacity = Math.Max(0, p.Opacity);
            }
        }

        while (_particles.Count > 80)
        {
            var oldest = _particles[0];
            if (oldest.EllipseElement != null)
                ParticleCanvas.Children.Remove(oldest.EllipseElement);
            if (oldest.StarElement != null)
                ParticleCanvas.Children.Remove(oldest.StarElement);
            _particles.RemoveAt(0);
        }
    }

    private void SpawnParticle(double width, double height)
    {
        var isStar = _rng.NextDouble() < 0.3;
        var size = isStar
            ? _rng.NextDouble() * 12 + 8
            : _rng.NextDouble() * 6 + 3;
        var color = ParticleColors[_rng.Next(ParticleColors.Length)];

        var particle = new Particle
        {
            X = _rng.NextDouble() * width,
            Y = height + size,
            VelocityY = -(_rng.NextDouble() * 1.5 + 0.5),
            SwayPhase = _rng.NextDouble() * Math.PI * 2,
            SwaySpeed = _rng.NextDouble() * 0.06 + 0.02,
            SwayAmplitude = _rng.NextDouble() * 30 + 10,
            Size = size,
            Opacity = _rng.NextDouble() * 0.4 + 0.4,
            FadeSpeed = _rng.NextDouble() * 0.003 + 0.001,
            RotationSpeed = isStar ? _rng.NextDouble() * 2 - 1 : 0,
            Color = color,
            IsStar = isStar
        };

        if (isStar)
        {
            var star = new Path
            {
                Data = StreamGeometry.Parse(StarPathData),
                Fill = new SolidColorBrush(color),
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                Opacity = particle.Opacity,
                IsHitTestVisible = false,
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative)
            };
            particle.StarElement = star;
            ParticleCanvas.Children.Add(star);
        }
        else
        {
            var ellipse = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(color),
                Opacity = particle.Opacity,
                IsHitTestVisible = false
            };
            particle.EllipseElement = ellipse;
            ParticleCanvas.Children.Add(ellipse);
        }

        _particles.Add(particle);
    }
}
