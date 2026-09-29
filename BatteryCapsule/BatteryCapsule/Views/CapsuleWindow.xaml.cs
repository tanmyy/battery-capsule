using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BatteryCapsule.Models;
using Microsoft.Win32;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace BatteryCapsule.Views;

public partial class CapsuleWindow : Window
{
    private Point _dragStartScreen;
    private Point _dragStartWindow;
    private bool _isDragging;
    private bool _mouseDownForDrag;
    private const double DragThreshold = 4.0;

    private ExpandedPanelWindow? _panel;

    public CapsuleWindow()
    {
        InitializeComponent();

        var app = (App)System.Windows.Application.Current;
        var settings = app.Settings.Current;

        Left = settings.CapsuleX;
        Top = settings.CapsuleY;
        Topmost = settings.AlwaysOnTop;
        PillBorder.Opacity = settings.Transparency;
        ApplyScale(settings.CapsuleScale);

        ApplyThemeColors();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
                Dispatcher.Invoke(ApplyThemeColors);
        };

        app.SnapshotUpdated += OnSnapshotUpdated;

        var initial = app.LatestSnapshot;
        if (initial != null) Render(initial);
    }

    public void ApplyLiveSettings()
    {
        var settings = ((App)System.Windows.Application.Current).Settings.Current;
        PillBorder.Opacity = settings.Transparency;
        ApplyScale(settings.CapsuleScale);
    }

    private void ApplyScale(double scale)
    {
        scale = Math.Clamp(scale, 0.8, 1.4);
        MainText.FontSize = 13 * scale;
        IconText.FontSize = 14 * scale;
        CloseButton.FontSize = 10 * scale;
        PillBorder.Padding = new Thickness(12 * scale, 6 * scale, 12 * scale, 6 * scale);
    }

    private bool IsWindowsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 1;
        }
        catch
        {
            return false; // default to dark styling if we can't tell
        }
    }

    private void ApplyThemeColors()
    {
        bool light = IsWindowsLightTheme();
        var app = System.Windows.Application.Current;
        PillBorder.Background = light
            ? (SolidColorBrush)app.Resources["CapsuleBackgroundLight"]
            : (SolidColorBrush)app.Resources["CapsuleBackgroundDark"];

        MainText.Foreground = light
            ? (SolidColorBrush)app.Resources["CapsuleTextLight"]
            : (SolidColorBrush)app.Resources["CapsuleTextDark"];

        CloseButton.Foreground = light
            ? new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66))
            : new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // The Button marks the mouse events handled, so this won't also trigger
        // the pill's click-to-open-panel or drag logic.
        ((App)System.Windows.Application.Current).ExitApp();
    }

    private void OnSnapshotUpdated(BatterySnapshot snap)
    {
        Dispatcher.Invoke(() => Render(snap));
    }

    private void Render(BatterySnapshot snap)
    {
        var settings = ((App)System.Windows.Application.Current).Settings.Current;

        if (!snap.BatteryPresent)
        {
            IconText.Text = "🔌";
            MainText.Text = "No battery";
            return;
        }

        int? pct = settings.ShowEstimatedPercent ? (snap.EstimatedPercent ?? snap.CalculatedPercent)
                                                  : snap.WindowsReportedPercent;
        string pctText = pct.HasValue ? $"{pct.Value}%" : "N/A";

        string timeText = "";
        if (settings.ShowTimeRemaining)
        {
            if (snap.ChargeState == ChargeState.Full)
                timeText = " · Full";
            else if (snap.ChargeState == ChargeState.Charging)
                timeText = snap.EstimatedMinutesRemaining.HasValue
                    ? $" · {FormatMinutes(snap.EstimatedMinutesRemaining.Value)} to full"
                    : " · calculating…";
            else if (snap.ChargeState == ChargeState.Discharging)
                timeText = snap.EstimatedMinutesRemaining.HasValue
                    ? $" · {FormatMinutes(snap.EstimatedMinutesRemaining.Value)}"
                    : " · N/A";
        }

        IconText.Text = snap.ChargeState is ChargeState.Charging or ChargeState.Full ? "⚡" : "🔋";
        MainText.Text = pctText + timeText;
    }

    private static string FormatMinutes(int minutes)
    {
        if (minutes < 60) return $"{minutes}m";
        int h = minutes / 60;
        int m = minutes % 60;
        return m == 0 ? $"{h}h" : $"{h}h {m}m";
    }

    // --- Drag handling: distinguish a click (open panel) from a drag (move capsule) ---

    private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDownForDrag = true;
        _isDragging = false;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _dragStartWindow = new Point(Left, Top);
        PillBorder.CaptureMouse();
    }

    private void Pill_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_mouseDownForDrag || e.LeftButton != MouseButtonState.Pressed) return;

        var current = PointToScreen(e.GetPosition(this));
        var delta = current - _dragStartScreen;

        if (!_isDragging && (Math.Abs(delta.X) > DragThreshold || Math.Abs(delta.Y) > DragThreshold))
            _isDragging = true;

        if (_isDragging)
        {
            Left = _dragStartWindow.X + delta.X;
            Top = _dragStartWindow.Y + delta.Y;
        }
    }

    private void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        PillBorder.ReleaseMouseCapture();
        _mouseDownForDrag = false;

        if (_isDragging)
        {
            _isDragging = false;
            var app = (App)System.Windows.Application.Current;
            app.Settings.Current.CapsuleX = Left;
            app.Settings.Current.CapsuleY = Top;
            app.Settings.Save();
        }
        else
        {
            ToggleExpandedPanel();
        }
    }

    private void ToggleExpandedPanel()
    {
        if (_panel != null && _panel.IsVisible)
        {
            _panel.Close();
            _panel = null;
            return;
        }

        _panel = new ExpandedPanelWindow(this);
        _panel.Show();
    }
}
