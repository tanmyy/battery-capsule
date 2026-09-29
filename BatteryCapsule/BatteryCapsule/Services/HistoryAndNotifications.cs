namespace BatteryCapsule.Services;

public readonly record struct HistoryPoint(DateTime TimeUtc, double? PercentEstimated, double? PowerW);

/// <summary>
/// Small ring buffer of recent snapshots for the mini graph. Kept in memory only -
/// this is a "glance at recent trend" feature, not a long-term log.
/// </summary>
public sealed class HistoryStore
{
    private readonly int _capacity;
    private readonly Queue<HistoryPoint> _points = new();

    public HistoryStore(int capacityMinutes = 30, int sampleEverySeconds = 10)
    {
        _capacity = Math.Max(10, (capacityMinutes * 60) / Math.Max(1, sampleEverySeconds));
    }

    public void Add(HistoryPoint p)
    {
        _points.Enqueue(p);
        while (_points.Count > _capacity) _points.Dequeue();
    }

    public IReadOnlyList<HistoryPoint> Snapshot() => _points.ToArray();
}

/// <summary>
/// Thin wrapper around Windows toast-style balloon notifications via the tray icon.
/// Notifications are opt-in and disabled by default (see AppSettings.NotificationsEnabled).
/// Includes simple de-duplication so a single threshold only fires once per session state.
/// </summary>
public sealed class NotificationService
{
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private bool _warned20, _warned10, _warnedLowTime, _warnedMismatch, _warnedLowHealth, _warnedGauge;

    public NotificationService(System.Windows.Forms.NotifyIcon trayIcon)
    {
        _trayIcon = trayIcon;
    }

    public void ResetDischargeCycleFlags()
    {
        _warned20 = _warned10 = _warnedLowTime = false;
    }

    public void Notify(string title, string message)
    {
        _trayIcon.BalloonTipTitle = title;
        _trayIcon.BalloonTipText = message;
        _trayIcon.ShowBalloonTip(6000);
    }

    public void NotifyGaugeWarning(string reason)
    {
        if (_warnedGauge) return; // one alert per suspicious episode
        _warnedGauge = true;
        Notify("Battery gauge looks unreliable", reason);
    }

    public void ResetGaugeWarning() => _warnedGauge = false;

    public void EvaluateAndNotify(Models.BatterySnapshot snap, AppSettings settings)
    {
        if (!settings.NotificationsEnabled || !snap.BatteryPresent) return;

        int? pct = snap.EstimatedPercent ?? snap.CalculatedPercent;

        if (snap.ChargeState == Models.ChargeState.Charging)
        {
            ResetDischargeCycleFlags();
        }

        if (pct.HasValue && snap.ChargeState == Models.ChargeState.Discharging)
        {
            if (settings.NotifyAt20Percent && pct.Value <= 20 && !_warned20)
            {
                _warned20 = true;
                Notify("Battery at 20%", "Consider plugging in soon.");
            }
            if (settings.NotifyAt10Percent && pct.Value <= 10 && !_warned10)
            {
                _warned10 = true;
                Notify("Battery at 10%", "Battery is getting low.");
            }
        }

        if (settings.NotifyLowTime && snap.EstimatedMinutesRemaining is > 0 and <= 10 &&
            snap.ChargeState == Models.ChargeState.Discharging && !_warnedLowTime)
        {
            _warnedLowTime = true;
            Notify("Very little time left", $"About {snap.EstimatedMinutesRemaining} min remaining at current usage.");
        }

        if (settings.NotifyReportedMismatch && snap.ReportedLooksInconsistent && !_warnedMismatch)
        {
            _warnedMismatch = true;
            Notify("Battery reading may be inaccurate", snap.InconsistencyReason ?? "Windows and estimated values differ.");
        }

        if (settings.NotifyLowHealth && snap.BatteryHealthPercent is < 60 && !_warnedLowHealth)
        {
            _warnedLowHealth = true;
            Notify("Battery health is low", $"Estimated health: {snap.BatteryHealthPercent:0}%. Consider a battery service check.");
        }
    }
}
