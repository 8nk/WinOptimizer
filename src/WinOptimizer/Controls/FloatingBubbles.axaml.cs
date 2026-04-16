using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;

namespace WinOptimizer.Controls;

public partial class FloatingBubbles : UserControl
{
    private class Bubble
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double VelocityX { get; set; }
        public double VelocityY { get; set; }
        public double Radius { get; set; }
        public double Opacity { get; set; }
        public Ellipse Ellipse { get; set; } = null!;
    }

    private readonly List<Bubble> _bubbles = new();
    private readonly DispatcherTimer _timer;
    private bool _initialized;

    public FloatingBubbles()
    {
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _timer.Tick += OnTimerTick;
    }

    private void CreateBubbles(double areaWidth, double areaHeight)
    {
        var rng = Random.Shared;
        BubblesCanvas.Children.Clear();
        _bubbles.Clear();

        if (areaWidth <= 0) areaWidth = 800;
        if (areaHeight <= 0) areaHeight = 600;

        // 10 bubbles, smaller so they don't clip heavily at edges
        for (int i = 0; i < 10; i++)
        {
            var radius = rng.NextDouble() * 80 + 40; // 40-120 radius
            var opacity = rng.NextDouble() * 0.06 + 0.02;
            var speed = rng.NextDouble() * 0.15 + 0.05;
            var angle = rng.NextDouble() * Math.PI * 2;

            // Spawn within bounds
            var x = rng.NextDouble() * areaWidth;
            var y = rng.NextDouble() * areaHeight;

            var ellipse = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Fill = Brushes.White,
                Opacity = opacity,
                IsHitTestVisible = false
            };

            _bubbles.Add(new Bubble
            {
                X = x,
                Y = y,
                VelocityX = Math.Cos(angle) * speed,
                VelocityY = Math.Sin(angle) * speed,
                Radius = radius,
                Opacity = opacity,
                Ellipse = ellipse
            });

            BubblesCanvas.Children.Add(ellipse);
            Canvas.SetLeft(ellipse, x - radius);
            Canvas.SetTop(ellipse, y - radius);
        }

        _initialized = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;

        if (width <= 0 || height <= 0)
            return;

        if (!_initialized)
        {
            CreateBubbles(width, height);
            return;
        }

        foreach (var bubble in _bubbles)
        {
            bubble.X += bubble.VelocityX;
            bubble.Y += bubble.VelocityY;

            if (bubble.X - bubble.Radius > width)
                bubble.X = -bubble.Radius;
            else if (bubble.X + bubble.Radius < 0)
                bubble.X = width + bubble.Radius;

            if (bubble.Y - bubble.Radius > height)
                bubble.Y = -bubble.Radius;
            else if (bubble.Y + bubble.Radius < 0)
                bubble.Y = height + bubble.Radius;

            Canvas.SetLeft(bubble.Ellipse, bubble.X - bubble.Radius);
            Canvas.SetTop(bubble.Ellipse, bubble.Y - bubble.Radius);
        }
    }
}
