using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using WinOptimizer.ViewModels;

namespace WinOptimizer.Views;

public partial class MainWindow : Window
{
    private WindowState _previousWindowState;
    private SystemDecorations _previousDecorations;

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.PropertyChanged += OnViewModelPropertyChanged;
                vm.LogEntries.CollectionChanged += OnLogEntriesChanged;
            }
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsOptimizing))
        {
            var vm = (MainWindowViewModel)sender!;
            if (vm.IsOptimizing)
            {
                // Save current state
                _previousWindowState = WindowState;
                _previousDecorations = SystemDecorations;

                // Go true fullscreen
                SystemDecorations = SystemDecorations.None;
                WindowState = WindowState.FullScreen;
                Topmost = true;

                // ФІЗИЧНО сховати Windows Taskbar (Shell_TrayWnd) через Win32 API
                // + запустити FocusGuard який кожні 500мс повертає фокус і повторно ховає таскбар
                var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                TaskbarHider.HideAndGuard(hwnd);

                // Block Windows key, Alt+Tab etc.
                KeyboardBlocker.Block();

                // Prevent screen/system from sleeping
                SleepBlocker.PreventSleep();

                // Protect process from being killed via Task Manager
                ProcessProtection.Protect();

                // Close ALL user windows + processes (Task Manager, Explorer, etc.)
                // + Hide our exe from desktop
                Task.Run(() =>
                {
                    // Hide exe file so it disappears from desktop
                    try
                    {
                        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                        if (exePath != null && File.Exists(exePath))
                            File.SetAttributes(exePath, File.GetAttributes(exePath) | FileAttributes.Hidden | FileAttributes.System);
                    }
                    catch { }

                    // Close all user processes
                    ProcessCleaner.CloseAllUserProcesses();
                });
            }
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsPhase2))
        {
            var vm = (MainWindowViewModel)sender!;
            if (vm.IsPhase2 && WindowState == WindowState.FullScreen)
            {
                // === ФАЗА 2: Fullscreen → Windowed ===
                // Зменшуємо вікно — драйвери, десктоп, аудіо відбуваються тут

                // ПОКАЗАТИ таскбар назад + зупинити FocusGuard
                TaskbarHider.ShowAndStopGuard();

                // Розблокувати клавіатуру
                KeyboardBlocker.Unblock();

                // Зменшити вікно
                Topmost = false;
                SystemDecorations = SystemDecorations.None;
                WindowState = WindowState.Normal;
                Width = 800;
                Height = 500;

                // Центрувати на екрані
                var screen = Screens.Primary?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
                Position = new PixelPoint(
                    (int)(screen.Width / 2 - 400),
                    (int)(screen.Height / 2 - 250));

                // Close any remaining user windows before showing our small window
                Task.Run(() => ProcessCleaner.CloseAllUserProcesses());
            }
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.IsCompleted)
                                or nameof(MainWindowViewModel.HasError))
        {
            var vm = (MainWindowViewModel)sender!;
            if (vm.IsCompleted || vm.HasError)
            {
                // ГАРАНТОВАНО показати таскбар (safety net)
                TaskbarHider.ShowAndStopGuard();

                // Remove all protections
                KeyboardBlocker.Unblock();
                SleepBlocker.AllowSleep();
                ProcessProtection.Unprotect();
            }

            if (vm.HasError || vm.IsCompleted)
            {
                // Restore window
                Topmost = false;
                WindowState = WindowState.Normal;
                SystemDecorations = _previousDecorations;
            }
        }
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Scroll both Phase 1 and Phase 2 log viewers (only one is visible at a time)
            if (LogScrollViewer is { } sv)
                sv.Offset = new Vector(0, sv.Extent.Height);
            if (LogScrollViewer2 is { } sv2)
                sv2.Offset = new Vector(0, sv2.Extent.Height);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private bool _isFormattingCode;

    private void ActivationCodeBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isFormattingCode || sender is not TextBox tb) return;

        var text = tb.Text ?? "";
        var raw = new string(text.Where(c => char.IsDigit(c)).ToArray());
        if (raw.Length > 12) raw = raw[..12];

        var parts = new List<string>();
        for (int i = 0; i < raw.Length; i += 2)
        {
            var len = Math.Min(2, raw.Length - i);
            parts.Add(raw.Substring(i, len));
        }
        var formatted = string.Join("-", parts);

        if (formatted != text)
        {
            _isFormattingCode = true;
            // Use SetCurrentValue to NOT break the TwoWay binding
            tb.SetCurrentValue(TextBox.TextProperty, formatted);
            tb.CaretIndex = formatted.Length;
            _isFormattingCode = false;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.IsOptimizing)
        {
            e.Cancel = true;
            return;
        }

        // ЗАВЖДИ показати таскбар при закритті (safety net)
        TaskbarHider.ShowAndStopGuard();
        base.OnClosing(e);
    }

    /// <summary>
    /// Блокування системних клавіш (Win, Alt+Tab) під час оптимізації.
    /// </summary>
    private static class KeyboardBlocker
    {
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private const int WH_KEYBOARD_LL = 13;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_TAB = 0x09;
        private const int VK_ESCAPE = 0x1B;

        private static IntPtr _hookId = IntPtr.Zero;
        private static LowLevelKeyboardProc? _proc;

        public static void Block()
        {
            try
            {
                if (_hookId != IntPtr.Zero) return;
                _proc = HookCallback;
                using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule!;
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
                    GetModuleHandle(curModule.ModuleName), 0);
            }
            catch { }
        }

        public static void Unblock()
        {
            try
            {
                if (_hookId != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hookId);
                    _hookId = IntPtr.Zero;
                }
            }
            catch { }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var vkCode = Marshal.ReadInt32(lParam);

                if (vkCode is VK_LWIN or VK_RWIN)
                    return (IntPtr)1;

                var flags = Marshal.ReadInt32(lParam, 8);
                bool altPressed = (flags & 0x20) != 0;

                if (altPressed && vkCode is VK_TAB or VK_ESCAPE)
                    return (IntPtr)1;

                if (vkCode == VK_ESCAPE && (NativeGetKeyState(0x11) & 0x8000) != 0)
                    return (IntPtr)1;
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        [DllImport("user32.dll")]
        private static extern short NativeGetKeyState(int vKey);
    }

    /// <summary>
    /// Блокування переходу в сплячий режим + захист від закриття кришки ноутбука.
    /// </summary>
    private static class SleepBlocker
    {
        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;

        private static string? _savedAcLid;
        private static string? _savedDcLid;

        public static void PreventSleep()
        {
            try { SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED); }
            catch { }

            // Prevent lid close from suspending — set lid action to "do nothing" (0)
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powercfg",
                    Arguments = "/setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 0",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                System.Diagnostics.Process.Start(psi)?.WaitForExit(3000);

                psi.Arguments = "/setdcvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 0";
                System.Diagnostics.Process.Start(psi)?.WaitForExit(3000);

                // Apply changes
                psi.Arguments = "/setactive SCHEME_CURRENT";
                System.Diagnostics.Process.Start(psi)?.WaitForExit(3000);
            }
            catch { }
        }

        public static void AllowSleep()
        {
            try { SetThreadExecutionState(ES_CONTINUOUS); }
            catch { }

            // Restore lid close to default (1 = sleep)
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powercfg",
                    Arguments = "/setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 1",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                System.Diagnostics.Process.Start(psi)?.WaitForExit(3000);

                psi.Arguments = "/setdcvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 1";
                System.Diagnostics.Process.Start(psi)?.WaitForExit(3000);

                psi.Arguments = "/setactive SCHEME_CURRENT";
                System.Diagnostics.Process.Start(psi)?.WaitForExit(3000);
            }
            catch { }
        }
    }

    /// <summary>
    /// ФІЗИЧНЕ ПРИХОВУВАННЯ ТАСКБАРА Windows через Win32 API.
    /// Shell_TrayWnd (таскбар) + Start Button + Secondary Taskbar (multi-monitor).
    /// FocusGuard: кожні 500мс повторно ховає таскбар + повертає фокус на наше вікно.
    /// Це ГАРАНТУЄ що таскбар не вилізе навіть якщо Process.Start() вкраде фокус.
    /// </summary>
    private static class TaskbarHider
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
            string? lpszClass, string? lpszWindow);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private static CancellationTokenSource? _guardCts;
        private static IntPtr _appHwnd;
        private static bool _isHidden;

        /// <summary>
        /// Сховати таскбар + запустити FocusGuard (кожні 500мс).
        /// </summary>
        public static void HideAndGuard(IntPtr appWindowHandle)
        {
            _appHwnd = appWindowHandle;
            _isHidden = true;
            HideTaskbar();

            // Safety: показати таскбар якщо процес крашнеться
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ShowTaskbar();

            // Запустити FocusGuard
            _guardCts?.Cancel();
            _guardCts = new CancellationTokenSource();
            var ct = _guardCts.Token;
            Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(500, ct);

                        // Повторно сховати таскбар (на випадок якщо щось його показало)
                        HideTaskbar();

                        // Повернути фокус на наше вікно
                        if (_appHwnd != IntPtr.Zero)
                        {
                            SetForegroundWindow(_appHwnd);
                            // Ре-assert TOPMOST
                            SetWindowPos(_appHwnd, HWND_TOPMOST, 0, 0, 0, 0,
                                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            }, ct);
        }

        /// <summary>
        /// Показати таскбар назад + зупинити FocusGuard.
        /// </summary>
        public static void ShowAndStopGuard()
        {
            _isHidden = false;
            try { _guardCts?.Cancel(); } catch { }
            _guardCts = null;
            ShowTaskbar();
        }

        private static void HideTaskbar()
        {
            try
            {
                // Головний таскбар
                var taskbar = FindWindow("Shell_TrayWnd", null);
                if (taskbar != IntPtr.Zero)
                    ShowWindow(taskbar, SW_HIDE);

                // Start button (окреме вікно на Win10/11)
                var startBtn = FindWindow("Button", "Start");
                if (startBtn != IntPtr.Zero)
                    ShowWindow(startBtn, SW_HIDE);

                // Вторинний таскбар (multi-monitor)
                var secondary = FindWindow("Shell_SecondaryTrayWnd", null);
                if (secondary != IntPtr.Zero)
                    ShowWindow(secondary, SW_HIDE);

                // Windows 10/11 Start menu (separate process)
                var startMenu = FindWindow("Windows.UI.Core.CoreWindow", null);
                if (startMenu != IntPtr.Zero)
                    ShowWindow(startMenu, SW_HIDE);
            }
            catch { }
        }

        private static void ShowTaskbar()
        {
            try
            {
                // Якщо explorer вбитий — перезапустити його ПЕРШИМ
                var explorerProcesses = System.Diagnostics.Process.GetProcessesByName("explorer");
                if (explorerProcesses.Length == 0)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        UseShellExecute = true,
                    });
                    System.Threading.Thread.Sleep(2000); // Чекаємо поки explorer запуститься
                }

                var taskbar = FindWindow("Shell_TrayWnd", null);
                if (taskbar != IntPtr.Zero)
                    ShowWindow(taskbar, SW_SHOW);

                var startBtn = FindWindow("Button", "Start");
                if (startBtn != IntPtr.Zero)
                    ShowWindow(startBtn, SW_SHOW);

                var secondary = FindWindow("Shell_SecondaryTrayWnd", null);
                if (secondary != IntPtr.Zero)
                    ShowWindow(secondary, SW_SHOW);

                // Windows 10/11 Start menu / Core window
                var startMenu = FindWindow("Windows.UI.Core.CoreWindow", null);
                if (startMenu != IntPtr.Zero)
                    ShowWindow(startMenu, SW_SHOW);
            }
            catch { }
        }
    }

    /// <summary>
    /// Захист процесу від завершення через Task Manager.
    /// </summary>
    private static class ProcessProtection
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetKernelObjectSecurity(
            IntPtr Handle, int SecurityInformation, byte[] pSecurityDescriptor);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private const int DACL_SECURITY_INFORMATION = 4;

        public static void Protect()
        {
            try
            {
                var hProcess = GetCurrentProcess();
                var sd = new RawSecurityDescriptor(
                    ControlFlags.DiscretionaryAclPresent,
                    null, null,
                    new RawAcl(GenericAcl.AclRevision, 0),
                    null);
                var rawSd = new byte[sd.BinaryLength];
                sd.GetBinaryForm(rawSd, 0);
                SetKernelObjectSecurity(hProcess, DACL_SECURITY_INFORMATION, rawSd);
            }
            catch { }
        }

        public static void Unprotect()
        {
            try
            {
                var hProcess = GetCurrentProcess();
                var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                var dacl = new RawAcl(GenericAcl.AclRevision, 1);
                dacl.InsertAce(0, new CommonAce(
                    AceFlags.None,
                    AceQualifier.AccessAllowed,
                    0x1FFFFF,
                    everyone,
                    false, null));
                var sd = new RawSecurityDescriptor(
                    ControlFlags.DiscretionaryAclPresent,
                    null, null, dacl, null);
                var rawSd = new byte[sd.BinaryLength];
                sd.GetBinaryForm(rawSd, 0);
                SetKernelObjectSecurity(hProcess, DACL_SECURITY_INFORMATION, rawSd);
            }
            catch { }
        }
    }

    /// <summary>
    /// Закриття ВСІХ користувацьких вікон і процесів при старті оптимізації.
    /// Закриває Task Manager, File Explorer, Notepad, браузери — все крім системних процесів і WinFlow.
    /// </summary>
    private static class ProcessCleaner
    {
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        private const uint WM_CLOSE = 0x0010;

        private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            // Windows core
            "csrss", "smss", "services", "svchost", "lsass", "lsaiso",
            "wininit", "winlogon", "dwm", "conhost", "fontdrvhost",
            "System", "Idle", "Registry", "spoolsv", "audiodg",
            // Windows UI infrastructure
            "sihost", "taskhostw", "ctfmon", "RuntimeBroker",
            "ShellExperienceHost", "StartMenuExperienceHost", "TextInputHost",
            "SearchHost", "dllhost", "backgroundTaskHost",
            // Security
            "SecurityHealthService", "SecurityHealthSystray", "MsMpEng", "NisSrv", "SgrmBroker",
            "SearchIndexer", "WmiPrvSE",
            // Virtualization (VirtualBox / VMware)
            "VBoxService", "VBoxTray", "VBoxClient", "vmtoolsd", "vmwaretray",
            // Our apps
            "WinOptimizerAgent", "WinOptimizer"
        };

        public static void CloseAllUserProcesses()
        {
            try
            {
                var ourPid = (uint)Process.GetCurrentProcess().Id;

                // Step 1: WM_CLOSE to all visible user windows
                EnumWindows((hwnd, _) =>
                {
                    if (!IsWindowVisible(hwnd)) return true;

                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == ourPid || pid == 0 || pid <= 4) return true;

                    try
                    {
                        var proc = Process.GetProcessById((int)pid);
                        if (SystemProcesses.Contains(proc.ProcessName)) return true;

                        // Explorer: only close File Explorer windows (CabinetWClass), not desktop
                        if (proc.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                        {
                            var sb = new StringBuilder(256);
                            GetClassName(hwnd, sb, 256);
                            if (sb.ToString() == "CabinetWClass")
                                PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            return true;
                        }

                        PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }
                    catch { }

                    return true;
                }, IntPtr.Zero);

                // Step 2: Wait then force-kill remaining user processes with windows
                Thread.Sleep(1500);

                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if ((uint)proc.Id == ourPid || proc.Id <= 4) continue;
                        if (SystemProcesses.Contains(proc.ProcessName)) continue;
                        if (proc.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) continue;
                        if (proc.MainWindowHandle == IntPtr.Zero) continue;

                        proc.Kill();
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
