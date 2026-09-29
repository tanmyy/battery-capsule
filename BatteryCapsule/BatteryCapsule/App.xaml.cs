using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using BatteryCapsule.Models;
using BatteryCapsule.Services;
using BatteryCapsule.Views;
using Application = System.Windows.Application;

namespace BatteryCapsule;

public partial class App : Application
{
    // --- Win32 GetSystemPowerStatus: the same call Windows' own taskbar battery icon
    // uses, so "WindowsReportedPercent" in the UI is genuinely what Windows itself shows. ---
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;   // seconds, -1 if unknown
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    private System.Threading.Mutex? _singleInstanceMutex;
    private DispatcherTimer? _pollTimer;
    private BatteryReader? _reader;
    private EstimationEngine? _engine;
    private SettingsService? _settingsService;
    private HistoryStore? _history;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private NotificationService? _notifications;

    public CapsuleWindow? Capsule { get; private set; }
    public BatterySnapshot? LatestSnapshot { get; private set; }
    public HistoryStore History => _history!;
    public SettingsService Settings => _settingsService!;
    public EstimationEngine Engine => _engine!;

    public event Action<BatterySnapshot>? SnapshotUpdated;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool createdNew;
        _singleInstanceMutex = new System.Threading.Mutex(true, "BatteryCapsule_SingleInstance_Mutex", out createdNew);
        if (!createdNew)
        {
            // Already running - just exit quietly.
            Shutdown();
            return;
        }

        _settingsService = new SettingsService();
        _settingsService.Load();

        _reader = new BatteryReader();
        _engine = new EstimationEngine();
        _history = new HistoryStore(sampleEverySeconds: _settingsService.Current.UpdateFrequencySeconds);

        SetupTrayIcon();
        _notifications = new NotificationService(_trayIcon!);

        Capsule = new CapsuleWindow();
        Capsule.Show();

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(2, _settingsService.Current.UpdateFrequencySeconds))
        };
        _pollTimer.Tick += (_, _) => Poll();
        _pollTimer.Start();

        Poll(); // immediate first read so the capsule isn't blank on launch
    }

    public void ApplyUpdateFrequency(int seconds)
    {
        if (_pollTimer != null)
            _pollTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, seconds));
    }

    private void Poll()
    {
        try
        {
            var raw = _reader!.ReadAll();
            var snap = _engine!.Evaluate(raw);

            int? windowsPercent = null;
            if (GetSystemPowerStatus(out var status) && status.BatteryFlag != 128 /* 128 = no system battery */)
            {
                windowsPercent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : (int?)null;
            }

            bool inconsistent = false;
            string? reason = null;
            if (snap.BatteryPresent)
            {
                (inconsistent, reason) = _engine.CheckConsistency(windowsPercent, snap.CalculatedPercent, snap.ChargeState);
            }

            snap = snap with
            {
                WindowsReportedPercent = windowsPercent,
                ReportedLooksInconsistent = inconsistent,
                InconsistencyReason = reason
            };

            LatestSnapshot = snap;

            _history!.Add(new HistoryPoint(DateTime.UtcNow, snap.EstimatedPercent, snap.PowerW));

            _notifications?.EvaluateAndNotify(snap, _settingsService!.Current);

            SnapshotUpdated?.Invoke(snap);
        }
        catch
        {
            // A single failed poll should never crash a lightweight always-on widget;
            // just skip this cycle and try again next tick.
        }
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "Battery Capsule"
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add("Reset battery learning data", null, (_, _) => { _engine?.ResetLearning(); });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _trayIcon.ContextMenuStrip = menu;

        _trayIcon.DoubleClick += (_, _) => Capsule?.Activate();
    }

    public void OpenSettings()
    {
        var win = new SettingsWindow();
        win.Show();
        win.Activate();
    }

    public void ExitApp()
    {
        _settingsService?.Save();
        if (_trayIcon != null) _trayIcon.Visible = false;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pollTimer?.Stop();
        _trayIcon?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
