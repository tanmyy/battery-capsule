using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BatteryCapsule.Models;
using BatteryCapsule.Services;

namespace BatteryCapsule.Views;

public partial class ExpandedPanelWindow : Window
{
    private readonly CapsuleWindow _owner;

    public ExpandedPanelWindow(CapsuleWindow owner)
    {
        InitializeComponent();
        _owner = owner;

        Loaded += (_, _) => PositionNearCapsule();

        var app = (App)System.Windows.Application.Current;
        app.SnapshotUpdated += OnSnapshotUpdated;
        Closed += (_, _) => app.SnapshotUpdated -= OnSnapshotUpdated;

        if (app.LatestSnapshot != null) Render(app.LatestSnapshot);
    }

    private void PositionNearCapsule()
    {
        Left = _owner.Left;
        Top = _owner.Top + _owner.ActualHeight + 8;

        // Keep on-screen: if it would run off the right/bottom edge, shift it back.
        var workArea = SystemParameters.WorkArea;
        if (Left + ActualWidth > workArea.Right) Left = workArea.Right - ActualWidth - 8;
        if (Top + ActualHeight > workArea.Bottom) Top = _owner.Top - ActualHeight - 8;
    }

    private void Window_Deactivated(object sender, EventArgs e) => Close();

    private void OnSnapshotUpdated(BatterySnapshot snap) => Dispatcher.Invoke(() => Render(snap));

    private void Render(BatterySnapshot snap)
    {
        if (!snap.BatteryPresent)
        {
            EstPercentText.Text = "N/A";
            WinPercentText.Text = "No battery detected";
            ConsistencyBanner.Visibility = Visibility.Collapsed;
            MetricsPanel.Children.Clear();
            AddRow("Status", "This device has no battery");
            return;
        }

        EstPercentText.Text = snap.EstimatedPercent.HasValue ? $"{snap.EstimatedPercent.Value}%" : "N/A";
        WinPercentText.Text = snap.WindowsReportedPercent.HasValue ? $"{snap.WindowsReportedPercent.Value}%" : "N/A";

        if (snap.ReportedLooksInconsistent)
        {
            ConsistencyBanner.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0xA6, 0x23));
            ConsistencyText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0x8A));
            ConsistencyText.Text = "⚠️ " + (snap.InconsistencyReason ?? "The reported percentage may be inaccurate");
        }
        else
        {
            ConsistencyBanner.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x2E, 0x7D, 0x32));
            ConsistencyText.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0xF6, 0xCA));
            ConsistencyText.Text = "✓ Battery reading appears normal";
        }

        MetricsPanel.Children.Clear();

        if (snap.WindowsReportedPercent.HasValue && snap.EstimatedPercent.HasValue)
        {
            int diff = snap.EstimatedPercent.Value - snap.WindowsReportedPercent.Value;
            AddRow("Difference", $"{(diff >= 0 ? "+" : "")}{diff} pts");
        }

        string timeLabel = "Estimated time remaining";
        string timeValue = snap.EstimatedMinutesRemaining.HasValue
            ? FormatMinutes(snap.EstimatedMinutesRemaining.Value)
            : "N/A";
        if (snap.EstimatedMinutesRemaining.HasValue)
            timeValue += $"  ({snap.TimeConfidence} confidence)";
        AddRow(timeLabel, timeValue);

        AddRow("Current power consumption", snap.PowerW.HasValue ? $"{snap.PowerW.Value:0.0} W" : "N/A");
        AddRow("Battery health", snap.BatteryHealthPercent.HasValue ? $"{snap.BatteryHealthPercent.Value:0}%" : "N/A");

        string capacity = (snap.FullChargeCapacityWh.HasValue && snap.DesignCapacityWh.HasValue)
            ? $"{snap.FullChargeCapacityWh.Value:0.0} / {snap.DesignCapacityWh.Value:0.0} Wh"
            : "N/A";
        AddRow("Full-charge / design capacity", capacity);

        AddRow("Battery status", snap.ChargeState.ToString());
        AddRow("Voltage", snap.VoltageV.HasValue ? $"{snap.VoltageV.Value:0.00} V" : "N/A");
        AddRow("Current", snap.CurrentA.HasValue ? $"{snap.CurrentA.Value:0.00} A" : "N/A");
        AddRow("Temperature", snap.TemperatureC.HasValue ? $"{snap.TemperatureC.Value:0.0}°C" : "N/A");

        if (snap.BatteryCount > 1)
            AddRow("Batteries detected", snap.BatteryCount.ToString());

        DrawGraph();
    }

    private void AddRow(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        var valueBlock = new TextBlock
        {
            Text = value,
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 12,
            FontFamily = new FontFamily("Segoe UI Semibold"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        Grid.SetColumn(valueBlock, 1);

        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
        MetricsPanel.Children.Add(grid);
    }

    private static string FormatMinutes(int minutes)
    {
        if (minutes < 60) return $"{minutes} min";
        int h = minutes / 60;
        int m = minutes % 60;
        return m == 0 ? $"{h} hr" : $"{h} hr {m} min";
    }

    private void DrawGraph()
    {
        GraphCanvas.Children.Clear();
        var app = (App)System.Windows.Application.Current;
        var points = app.History.Snapshot().Where(p => p.PercentEstimated.HasValue).ToList();
        if (points.Count < 2) return;

        double width = 240, height = 46;
        double minVal = points.Min(p => p.PercentEstimated!.Value);
        double maxVal = points.Max(p => p.PercentEstimated!.Value);
        if (Math.Abs(maxVal - minVal) < 1) { minVal -= 1; maxVal += 1; }

        var polyline = new Polyline
        {
            Stroke = (SolidColorBrush)app.Resources["AccentGreen"],
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round
        };

        for (int i = 0; i < points.Count; i++)
        {
            double x = width * i / (points.Count - 1);
            double normalized = (points[i].PercentEstimated!.Value - minVal) / (maxVal - minVal);
            double y = height - (normalized * (height - 4)) - 2;
            polyline.Points.Add(new Point(x, y));
        }

        GraphCanvas.Width = width;
        GraphCanvas.Height = height;
        GraphCanvas.Children.Add(polyline);
    }
}
