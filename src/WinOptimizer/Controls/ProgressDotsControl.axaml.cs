using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;

namespace WinOptimizer.Controls;

public partial class ProgressDotsControl : UserControl
{
    public static readonly StyledProperty<int> CurrentStepIndexProperty =
        AvaloniaProperty.Register<ProgressDotsControl, int>(nameof(CurrentStepIndex), 0);

    public int CurrentStepIndex
    {
        get => GetValue(CurrentStepIndexProperty);
        set => SetValue(CurrentStepIndexProperty, value);
    }

    private const int DotCount = 13; // 13 steps for v5.5 (includes langpack + ISO download + install)
    private const double DotSize = 14;
    private const double Spacing = 30; // Трохи менше щоб 13 точок влізли
    private const double LineThickness = 3;

    private readonly Ellipse[] _dots = new Ellipse[DotCount];
    private readonly Rectangle[] _lines = new Rectangle[DotCount - 1];
    private readonly Rectangle[] _lineOverlays = new Rectangle[DotCount - 1];
    private readonly DispatcherTimer _pulseTimer;
    private double _pulsePhase;
    private bool _built;

    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#00BCD4"));
    private static readonly IBrush CompletedBrush = new SolidColorBrush(Color.Parse("#006064"));
    private static readonly IBrush GrayBrush = new SolidColorBrush(Color.Parse("#B2EBF2"));
    private static readonly IBrush LineBgBrush = new SolidColorBrush(Color.Parse("#30FFFFFF"));
    private static readonly IBrush LineActiveBrush = new SolidColorBrush(Color.Parse("#006064"));

    public ProgressDotsControl()
    {
        InitializeComponent();

        _pulseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _pulseTimer.Tick += OnPulseTick;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BuildVisuals();
        UpdateDots(CurrentStepIndex);
        _pulseTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pulseTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CurrentStepIndexProperty)
        {
            UpdateDots((int)change.NewValue!);
        }
    }

    private void BuildVisuals()
    {
        if (_built) return;
        _built = true;

        DotsCanvas.Children.Clear();

        var totalWidth = (DotCount - 1) * Spacing;
        var startX = (DotsCanvas.Width - totalWidth) / 2;
        var centerY = DotsCanvas.Height / 2;

        for (int i = 0; i < DotCount - 1; i++)
        {
            var x1 = startX + i * Spacing + DotSize / 2 + 2;
            var x2 = startX + (i + 1) * Spacing - DotSize / 2 - 2;
            var lineWidth = Math.Max(0, x2 - x1);

            var lineBg = new Rectangle
            {
                Width = lineWidth,
                Height = LineThickness,
                Fill = LineBgBrush,
                RadiusX = LineThickness / 2,
                RadiusY = LineThickness / 2,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(lineBg, x1);
            Canvas.SetTop(lineBg, centerY - LineThickness / 2);
            DotsCanvas.Children.Add(lineBg);
            _lines[i] = lineBg;

            var lineOverlay = new Rectangle
            {
                Width = 0,
                Height = LineThickness,
                Fill = LineActiveBrush,
                RadiusX = LineThickness / 2,
                RadiusY = LineThickness / 2,
                IsHitTestVisible = false,
                Opacity = 0
            };
            Canvas.SetLeft(lineOverlay, x1);
            Canvas.SetTop(lineOverlay, centerY - LineThickness / 2);
            DotsCanvas.Children.Add(lineOverlay);
            _lineOverlays[i] = lineOverlay;
        }

        for (int i = 0; i < DotCount; i++)
        {
            var cx = startX + i * Spacing;

            var dot = new Ellipse
            {
                Width = DotSize,
                Height = DotSize,
                Fill = GrayBrush,
                IsHitTestVisible = false,
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RenderTransform = new ScaleTransform(1, 1)
            };

            Canvas.SetLeft(dot, cx - DotSize / 2);
            Canvas.SetTop(dot, centerY - DotSize / 2);
            DotsCanvas.Children.Add(dot);
            _dots[i] = dot;
        }
    }

    private void OnPulseTick(object? sender, EventArgs e)
    {
        _pulsePhase += 0.08;
        if (_pulsePhase > Math.PI * 2)
            _pulsePhase -= Math.PI * 2;

        var idx = CurrentStepIndex;
        if (idx < 0 || idx >= DotCount || !_built)
            return;

        var t = (Math.Sin(_pulsePhase) + 1) / 2;
        var currentScale = 1.0 + t * 0.25;
        var currentOpacity = 1.0 - t * 0.15;

        var currentDot = _dots[idx];
        if (currentDot.RenderTransform is ScaleTransform st)
        {
            st.ScaleX = currentScale;
            st.ScaleY = currentScale;
        }
        currentDot.Opacity = currentOpacity;

        for (int i = idx + 1; i < DotCount; i++)
        {
            var distance = i - idx;
            var phaseOffset = distance * 0.6;
            var waveT = (Math.Sin(_pulsePhase - phaseOffset) + 1) / 2;
            var waveScale = 1.0 + waveT * 0.12;
            var waveOpacity = 0.5 + waveT * 0.3;

            var futureDot = _dots[i];
            if (futureDot.RenderTransform is ScaleTransform st2)
            {
                st2.ScaleX = waveScale;
                st2.ScaleY = waveScale;
            }
            futureDot.Opacity = waveOpacity;
        }
    }

    private void UpdateDots(int currentIndex)
    {
        if (!_built) return;

        for (int i = 0; i < DotCount; i++)
        {
            var dot = _dots[i];
            if (i < currentIndex)
            {
                dot.Fill = CompletedBrush;
                dot.Opacity = 1;
                if (dot.RenderTransform is ScaleTransform st1)
                {
                    st1.ScaleX = 1;
                    st1.ScaleY = 1;
                }
            }
            else if (i == currentIndex)
            {
                dot.Fill = AccentBrush;
            }
            else
            {
                dot.Fill = GrayBrush;
                dot.Opacity = 0.6;
                if (dot.RenderTransform is ScaleTransform st2)
                {
                    st2.ScaleX = 1;
                    st2.ScaleY = 1;
                }
            }
        }

        for (int i = 0; i < DotCount - 1; i++)
        {
            if (i < currentIndex)
            {
                _lineOverlays[i].Width = _lines[i].Width;
                _lineOverlays[i].Opacity = 1.0;
            }
            else
            {
                _lineOverlays[i].Width = 0;
                _lineOverlays[i].Opacity = 0;
            }
        }
    }
}
