using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;

namespace Disturn;

/// <summary>A transient WinUI tray menu; it owns no application services or group state.</summary>
public sealed partial class TrayMenuWindow : Window
{
    private readonly AppWindow _popup;
    private readonly IntPtr _handle;
    private readonly Action _open, _settings, _exit;
    private bool _activated, _closing, _showing, _executing;
    private RectInt32 _anchor;
    private readonly bool _animateOpening;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _openingTimer;
    private System.Diagnostics.Stopwatch? _openingClock;
    private PointInt32 _openingTarget;
    private int _openingDistance;
    private bool _openingComplete;

    internal TrayMenuWindow(ElementTheme theme, Action open, Action settings, Action exit)
    {
        try { InitializeComponent(); }
        catch
        {
            // A failed XAML load still creates a Window. Release it before the
            // tray service falls back to its native Open/Settings/Exit menu.
            try { Close(); }
            catch (Exception cleanupError)
            {
                System.Diagnostics.Debug.WriteLine("Could not close the failed tray popup: " + cleanupError);
            }
            throw; // Preserve the initialization error for the existing fallback handler.
        }
        _open = open;
        _settings = settings;
        _exit = exit;
        _animateOpening = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        MenuRoot.RequestedTheme = theme;
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _popup = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_handle));
        // Use the supported menu configuration instead of ordinary window constraints.
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.PreferredMinimumHeight = 1;
        presenter.PreferredMinimumWidth = 1;
        _popup.SetPresenter(presenter);
        _popup.IsShownInSwitchers = false;
        var corners = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(_handle, 33, ref corners, sizeof(int));
        var dark = theme == ElementTheme.Dark ? 1 : 0;
        DwmSetWindowAttribute(_handle, 20, ref dark, sizeof(int));
        // These documented DWM options are best-effort on older Windows releases.
        MenuRoot.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape)
            {
                args.Handled = true;
                CloseMenu();
                return;
            }
            var buttons = new[] { OpenMenuButton, SettingsMenuButton, ExitMenuButton };
            var focused = FocusManager.GetFocusedElement(MenuRoot.XamlRoot) as Button;
            var index = Array.IndexOf(buttons, focused);
            var next = args.Key switch
            {
                Windows.System.VirtualKey.Down => (index + 1) % buttons.Length,
                Windows.System.VirtualKey.Up => (index + buttons.Length - 1) % buttons.Length,
                Windows.System.VirtualKey.Home => 0,
                Windows.System.VirtualKey.End => buttons.Length - 1,
                _ => -1
            };
            if (next >= 0) { args.Handled = true; buttons[next].Focus(FocusState.Keyboard); }
        };
        MenuRoot.Loaded += (_, _) =>
        {
            // Recheck after XAML has loaded fonts/templates on the target monitor.
            if (_closing) return;
            SizeAndPosition();
            StartOpeningAnimation();
            OpenMenuButton.Focus(FocusState.Programmatic);
        };
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                if (!_activated || _showing || _executing) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    // Recheck after the activation transition; ignore transient
                    // deactivation if this same popup has already regained focus.
                    if (!_closing && !_showing && !_executing && GetForegroundWindow() != _handle)
                        CloseMenu();
                });
            }
            else _activated = true;
        };
        Closed += (_, _) =>
        {
            _closing = true;
            StopOpeningAnimation();
        };
    }

    private void StartOpeningAnimation()
    {
        if (_closing || _openingComplete) return;
        if (!_animateOpening || _openingDistance == 0)
        {
            _openingComplete = true;
            _popup.Move(_openingTarget);
            return;
        }
        StopOpeningAnimation();
        _openingClock = System.Diagnostics.Stopwatch.StartNew();
        _openingTimer = DispatcherQueue.CreateTimer();
        _openingTimer.Interval = TimeSpan.FromMilliseconds(16);
        _openingTimer.IsRepeating = true;
        _openingTimer.Tick += (_, _) =>
        {
            if (_closing || _openingClock is null) return;
            var progress = Math.Clamp(_openingClock.Elapsed.TotalMilliseconds / 180.0, 0, 1);
            var remaining = Math.Pow(1 - progress, 3); // Cubic ease-out.
            _popup.Move(new PointInt32(_openingTarget.X,
                _openingTarget.Y + (int)Math.Round(_openingDistance * remaining)));
            if (progress >= 1)
            {
                _openingComplete = true;
                StopOpeningAnimation();
            }
        };
        _openingTimer.Start();
    }

    private void StopOpeningAnimation()
    {
        _openingTimer?.Stop();
        _openingTimer = null;
        _openingClock?.Stop();
        _openingClock = null;
    }
    private void CloseMenu()
    {
        if (_closing) return;
        _closing = true;
        StopOpeningAnimation();
        Close();
    }

    private void Execute(Action command)
    {
        if (_closing || _executing) return;
        _executing = true;
        try { command(); }
        finally { CloseMenu(); }
    }

    private void OpenMenuButton_Click(object sender, RoutedEventArgs e) => Execute(_open);
    private void SettingsMenuButton_Click(object sender, RoutedEventArgs e) => Execute(_settings);
    private void ExitMenuButton_Click(object sender, RoutedEventArgs e) => Execute(_exit);

    internal void ShowAt(RectInt32 icon)
    {
        if (_closing) return;
        if (_popup.IsVisible) { CloseMenu(); return; } // Repeated right-click toggles this instance.
        _anchor = icon;
        _showing = true;
        try
        {
            SizeAndPosition();
            _popup.Show();
            Activate();
            SetForegroundWindow(_handle);
            _activated |= GetForegroundWindow() == _handle;
            if (!_activated)
                throw new InvalidOperationException("The tray popup could not acquire foreground activation.");
        }
        finally { _showing = false; }
    }

    private void SizeAndPosition()
    {
        var icon = _anchor;
        var centerX = icon.X + icon.Width / 2;
        var centerY = icon.Y + icon.Height / 2;
        // Move the still-hidden host onto the target monitor before reading its DPI.
        if (!_popup.IsVisible) _popup.Move(new PointInt32(centerX, centerY));
        var dpi = GetDpiForWindow(_handle);
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        // Measure the inner content rather than the window's stretching root,
        // whose desired height can include unused space from the host window.
        var horizontalInset = MenuRoot.Padding.Left + MenuRoot.Padding.Right +
            MenuRoot.BorderThickness.Left + MenuRoot.BorderThickness.Right;
        var verticalInset = MenuRoot.Padding.Top + MenuRoot.Padding.Bottom +
            MenuRoot.BorderThickness.Top + MenuRoot.BorderThickness.Bottom;
        MenuItemsPanel.Measure(new Size(272 - horizontalInset, double.PositiveInfinity));
        var contentHeight = MenuItemsPanel.DesiredSize.Height + verticalInset;
        if (!double.IsFinite(contentHeight) || contentHeight <= 0)
            throw new InvalidOperationException("The tray menu has no valid measured height.");
        _popup.ResizeClient(new SizeInt32((int)Math.Ceiling(272 * scale),
            (int)Math.Ceiling(contentHeight * scale)));
        var width = _popup.Size.Width;
        var height = _popup.Size.Height;
        var area = DisplayArea.GetFromPoint(new PointInt32(centerX, centerY), DisplayAreaFallback.Nearest).WorkArea;
        var gap = (int)Math.Ceiling(8 * scale);
        var left = centerX - width / 2;
        var top = icon.Y - height - gap;
        if (icon.X < area.X) // Taskbar on the left.
        {
            left = area.X + gap;
            top = centerY - height / 2;
        }
        else if (icon.X + icon.Width > area.X + area.Width) // On the right.
        {
            left = area.X + area.Width - width - gap;
            top = centerY - height / 2;
        }
        else if (icon.Y < area.Y) // On top.
            top = area.Y + gap;
        else if (icon.Y + icon.Height > area.Y + area.Height) // On the bottom.
            top = area.Y + area.Height - height - gap;
        else if (top < area.Y) // Overflow panel or cursor fallback near the top.
            top = icon.Y + icon.Height + gap;
        left = Math.Clamp(left, area.X, Math.Max(area.X, area.X + area.Width - width));
        top = Math.Clamp(top, area.Y, Math.Max(area.Y, area.Y + area.Height - height));
        _openingTarget = new PointInt32(left, top);
        // Keep the whole window within the work area even at its starting position.
        _openingDistance = Math.Max(0, Math.Min((int)Math.Ceiling(14 * scale),
            area.Y + area.Height - height - top));
        _popup.Move(new PointInt32(left, top + (_animateOpening && !_openingComplete ? _openingDistance : 0)));
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref int value, int size);
}
