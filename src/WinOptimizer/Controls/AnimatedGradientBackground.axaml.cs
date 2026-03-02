using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace WinOptimizer.Controls;

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

    private static readonly Color[] CyanColors =
    {
        Color.Parse("#E0F7FA"), Color.Parse("#B2EBF2"), Color.Parse("#80DEEA"),
        Color.Parse("#4DD0E1"), Color.Parse("#26C6DA"), Color.Parse("#00BCD4"), Color.Parse("#00ACC1")
    };

    private static readonly Color[] BlueColors =
    {
        Color.Parse("#E3F2FD"), Color.Parse("#BBDEFB"), Color.Parse("#90CAF9"),
        Color.Parse("#64B5F6"), Color.Parse("#42A5F5"), Color.Parse("#2196F3"), Color.Parse("#1E88E5")
    };

    private static readonly Color[] GreenColors =
    {
        Color.Parse("#E8F5E9"), Color.Parse("#C8E6C9"), Color.Parse("#A5D6A7"),
        Color.Parse("#81C784"), Color.Parse("#66BB6A"), Color.Parse("#4CAF50"), Color.Parse("#43A047")
    };

    private static readonly Color[] DeepColors =
    {
        Color.Parse("#004D40"), Color.Parse("#00695C"), Color.Parse("#00897B"),
        Color.Parse("#009688"), Color.Parse("#26A69A"), Color.Parse("#4DB6AC"), Color.Parse("#80CBC4")
    };

    private static readonly Color[] AuroraColors =
    {
        Color.Parse("#1A237E"), Color.Parse("#0D47A1"), Color.Parse("#006064"),
        Color.Parse("#004D40"), Color.Parse("#00BCD4"), Color.Parse("#26C6DA"), Color.Parse("#4DD0E1")
    };

    private static readonly Color[] PurpleColors =
    {
        Color.Parse("#E8EAF6"), Color.Parse("#C5CAE9"), Color.Parse("#9FA8DA"),
        Color.Parse("#7986CB"), Color.Parse("#5C6BC0"), Color.Parse("#3F51B5"), Color.Parse("#3949AB")
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
            Interval = TimeSpan.FromMilliseconds(40)
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
        _phase += 0.004; // Slower rotation
        if (_phase >= 2 * Math.PI) _phase -= 2 * Math.PI;

        _colorPhase += 0.0005; // Very slow palette transition
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

        // Smooth gradient rotation
        var startPoint = new RelativePoint(0.5 - cos * 0.4, 0.5 - sin * 0.4, RelativeUnit.Relative);
        var endPoint = new RelativePoint(0.5 + cos * 0.4, 0.5 + sin * 0.4, RelativeUnit.Relative);

        var brush = new LinearGradientBrush { StartPoint = startPoint, EndPoint = endPoint };

        // Fixed evenly-spaced stops, no offset jitter = no visible bands
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

        // Smooth easing (ease-in-out)
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
