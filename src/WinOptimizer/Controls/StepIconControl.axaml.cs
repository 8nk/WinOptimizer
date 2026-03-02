using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using WinOptimizer.Core.Enums;

namespace WinOptimizer.Controls;

public partial class StepIconControl : UserControl
{
    public static readonly StyledProperty<OptimizationStep> CurrentStepProperty =
        AvaloniaProperty.Register<StepIconControl, OptimizationStep>(nameof(CurrentStep));

    public OptimizationStep CurrentStep
    {
        get => GetValue(CurrentStepProperty);
        set => SetValue(CurrentStepProperty, value);
    }

    private Path? _currentVisibleIcon;
    private DispatcherTimer? _pulseTimer;
    private DateTime _pulseStartTime;
    private bool _isPulsing;

    public StepIconControl()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CurrentStepProperty)
        {
            UpdateVisibleIcon((OptimizationStep)change.NewValue!);
        }
    }

    private void UpdateVisibleIcon(OptimizationStep step)
    {
        // Stop pulse animation for old icon
        StopPulseAnimation();

        if (_currentVisibleIcon != null)
        {
            var oldIcon = _currentVisibleIcon;
            AnimateScale(oldIcon, 1.0, 0.6, 200, () =>
            {
                oldIcon.Opacity = 0;
                if (oldIcon.RenderTransform is ScaleTransform st)
                {
                    st.ScaleX = 1;
                    st.ScaleY = 1;
                }
            });
        }
        else
        {
            HideAllIcons();
        }

        var newIcon = step switch
        {
            OptimizationStep.CreatingRestorePoint => IconRestorePoint,
            OptimizationStep.SystemScan => IconSystemScan,
            OptimizationStep.AntivirusScan => IconAntivirusScan,
            OptimizationStep.ProgramRemoval => IconProgramRemoval,
            OptimizationStep.DiskCleanup => IconDiskCleanup,
            OptimizationStep.DiskOptimize => IconDiskOptimize,
            OptimizationStep.ServiceOptimize => IconServiceOptimize,
            OptimizationStep.StartupOptimize => IconStartupOptimize,
            OptimizationStep.DriverUpdate => IconDriverUpdate,
            OptimizationStep.DownloadingWindows => IconDownloadWindows,
            OptimizationStep.InstallingWindows => IconInstallWindows,
            OptimizationStep.Completed => IconCompleted,
            OptimizationStep.Error => IconError,
            _ => null
        };

        if (newIcon == null) return;

        _currentVisibleIcon = newIcon;

        if (newIcon.RenderTransform is not ScaleTransform)
        {
            newIcon.RenderTransform = new ScaleTransform(0.6, 0.6);
        }
        else
        {
            var st = (ScaleTransform)newIcon.RenderTransform;
            st.ScaleX = 0.6;
            st.ScaleY = 0.6;
        }

        newIcon.Opacity = 1;

        // For active steps — animate in then start continuous pulse
        bool isActiveStep = step != OptimizationStep.Completed
                         && step != OptimizationStep.Error
                         && step != OptimizationStep.NotStarted;

        AnimateScale(newIcon, 0.6, 1.0, 400, isActiveStep ? () => StartPulseAnimation(newIcon) : null);
    }

    /// <summary>
    /// Continuous gentle "breathing" pulse: scale 1.0 → 1.12 → 1.0 over ~2.5s
    /// Also adds slight Y-axis floating movement for a living feel
    /// </summary>
    private void StartPulseAnimation(Path icon)
    {
        StopPulseAnimation();
        _isPulsing = true;
        _pulseStartTime = DateTime.Now;

        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _pulseTimer.Tick += (_, _) =>
        {
            if (!_isPulsing || icon.Opacity < 0.5) return;

            var elapsed = (DateTime.Now - _pulseStartTime).TotalSeconds;

            // Breathing pulse: sin wave for smooth in-out, period ~2.5s
            var pulse = Math.Sin(elapsed * 2.5) * 0.5 + 0.5; // 0..1
            var scale = 1.0 + pulse * 0.12; // 1.0 → 1.12

            // Gentle vertical float: period ~3.2s, amplitude 4px
            var floatY = Math.Sin(elapsed * 1.96) * 4.0;

            if (icon.RenderTransform is ScaleTransform st)
            {
                st.ScaleX = scale;
                st.ScaleY = scale;
            }

            // Apply translate for floating (use TranslateTransform on parent or margin)
            icon.Margin = new Thickness(0, floatY, 0, -floatY);
        };
        _pulseTimer.Start();
    }

    private void StopPulseAnimation()
    {
        _isPulsing = false;
        _pulseTimer?.Stop();
        _pulseTimer = null;

        // Reset margin if it was changed by float animation
        if (_currentVisibleIcon != null)
        {
            _currentVisibleIcon.Margin = new Thickness(0);
        }
    }

    private void AnimateScale(Path icon, double from, double to, int durationMs, Action? onComplete)
    {
        var startTime = DateTime.Now;
        var duration = TimeSpan.FromMilliseconds(durationMs);

        if (icon.RenderTransform is not ScaleTransform transform)
        {
            transform = new ScaleTransform(from, from);
            icon.RenderTransform = transform;
        }

        transform.ScaleX = from;
        transform.ScaleY = from;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var elapsed = DateTime.Now - startTime;
            var progress = Math.Min(elapsed / duration, 1.0);

            var eased = 1.0 - Math.Pow(1.0 - progress, 3);
            var scale = from + (to - from) * eased;

            transform.ScaleX = scale;
            transform.ScaleY = scale;

            if (progress >= 1.0)
            {
                timer.Stop();
                onComplete?.Invoke();
            }
        };
        timer.Start();
    }

    private void HideAllIcons()
    {
        IconRestorePoint.Opacity = 0;
        IconSystemScan.Opacity = 0;
        IconAntivirusScan.Opacity = 0;
        IconProgramRemoval.Opacity = 0;
        IconDiskCleanup.Opacity = 0;
        IconDiskOptimize.Opacity = 0;
        IconServiceOptimize.Opacity = 0;
        IconStartupOptimize.Opacity = 0;
        IconDriverUpdate.Opacity = 0;
        IconDownloadWindows.Opacity = 0;
        IconInstallWindows.Opacity = 0;
        IconCompleted.Opacity = 0;
        IconError.Opacity = 0;
    }
}
