using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace WinOptimizer.Controls;

/// <summary>
/// Premium animated gradient background with deep color palettes.
/// Slow, smooth transitions — no visible shimmer or banding.
/// </summary>
public partial class AnimatedGradientBackground : UserControl
{
    public static readonly StyledProperty<string> PaletteProperty =
        AvaloniaProperty.Register<AnimatedGradientBackground, string>(nameof(Palette), "cyan");

    public string Palette
    {
        get => GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    private readonly DispatcherTimer _timer;
    private double _phase;
    private double _colorPhase;

    // === DEEP, PREMIUM palettes (dark backgrounds — no pastel/light colors) ===

    // Deep ocean — dark teal/cyan
    private static readonly Color[] CyanColors =
    {
        Color.Parse("#0A1628"), Color.Parse("#0D2137"), Color.Parse("#0F2B45"),
        Color.Parse("#0E3651"), Color.Parse("#0B4157"), Color.Parse("#094D5E"),
        Color.Parse("#075A68")
    };

    // Midnight blue — deep navy
    private static readonly Color[] BlueColors =
    {
        Color.Parse("#0A0E1A"), Color.Parse("#0D1529"), Color.Parse("#101D38"),
        Color.Parse("#132647"), Color.Parse("#152F55"), Color.Parse("#173863"),
        Color.Parse("#194170")
    };

    // Dark forest — deep green
    private static readonly Color[] GreenColors =
    {
        Color.Parse("#0A1A12"), Color.Parse("#0D2218"), Color.Parse("#102A1E"),
        Color.Parse("#133224"), Color.Parse("#163A2A"), Color.Parse("#194330"),
        Color.Parse("#1C4C36")
    };

    // Abyss — very dark teal-black
    private static readonly Color[] DeepColors =
    {
        Color.Parse("#060D10"), Color.Parse("#081418"), Color.Parse("#0A1B20"),
        Color.Parse("#0C2228"), Color.Parse("#0E2930"), Color.Parse("#103038"),
        Color.Parse("#123740")
    };

    // Northern lights — dark blue-green-purple
    private static readonly Color[] AuroraColors =
    {
        Color.Parse("#0A0A1E"), Color.Parse("#0D1230"), Color.Parse("#0A1A3A"),
        Color.Parse("#0B2238"), Color.Parse("#0E2A38"), Color.Parse("#0D3240"),
        Color.Parse("#0B3A3E")
    };

    // Dark amethyst — deep purple-indigo
    private static readonly Color[] PurpleColors =
    {
        Color.Parse("#0E0A1E"), Color.Parse("#140E2A"), Color.Parse("#1A1236"),
        Color.Parse("#201642"), Color.Parse("#261A4E"), Color.Parse("#2C1E5A"),
        Color.Parse("#322266")
    };

    private static readonly Color[][] AllPalettes =
    {
        CyanColors, DeepColors, AuroraColors, BlueColors, PurpleColors, GreenColors
    };

    public AnimatedGradientBackground()
    {
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50) // Slower tick = smoother
        };
        _timer.Tick += OnTimerTick;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateGradient();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _phase += 0.002; // Much slower rotation — no visible shimmer
        if (_phase >= 2 * Math.PI) _phase -= 2 * Math.PI;

        _colorPhase += 0.0003; // Very slow palette transition
        if (_colorPhase >= AllPalettes.Length) _colorPhase -= AllPalettes.Length;

        UpdateGradient();
    }

    private void UpdateGradient()
    {
        Color[] colors;

        if (Palette?.ToLowerInvariant() == "auto")
        {
            colors = GetInterpolatedPalette();
        }
        else
        {
            colors = Palette?.ToLowerInvariant() switch
            {
                "blue" => BlueColors,
                "green" => GreenColors,
                "deep" => DeepColors,
                "aurora" => AuroraColors,
                "purple" => PurpleColors,
                _ => CyanColors
            };
        }

        var cos = Math.Cos(_phase);
        var sin = Math.Sin(_phase);

        // Wide gradient sweep for smooth coverage
        var startPoint = new RelativePoint(0.5 - cos * 0.5, 0.5 - sin * 0.5, RelativeUnit.Relative);
        var endPoint = new RelativePoint(0.5 + cos * 0.5, 0.5 + sin * 0.5, RelativeUnit.Relative);

        var brush = new LinearGradientBrush { StartPoint = startPoint, EndPoint = endPoint };

        // Even spacing — no jitter or banding
        for (int i = 0; i < colors.Length; i++)
        {
            double offset = (double)i / (colors.Length - 1);
            brush.GradientStops.Add(new GradientStop(colors[i], offset));
        }

        GradientCanvas.Background = brush;
    }

    private Color[] GetInterpolatedPalette()
    {
        var idx1 = (int)Math.Floor(_colorPhase) % AllPalettes.Length;
        var idx2 = (idx1 + 1) % AllPalettes.Length;
        var t = _colorPhase - Math.Floor(_colorPhase);

        // Smooth easing (ease-in-out cubic)
        t = t * t * (3.0 - 2.0 * t);

        var p1 = AllPalettes[idx1];
        var p2 = AllPalettes[idx2];
        var result = new Color[p1.Length];

        for (int i = 0; i < p1.Length; i++)
            result[i] = Color.FromArgb(255,
                (byte)(p1[i].R + (p2[i].R - p1[i].R) * t),
                (byte)(p1[i].G + (p2[i].G - p1[i].G) * t),
                (byte)(p1[i].B + (p2[i].B - p1[i].B) * t));

        return result;
    }
}
