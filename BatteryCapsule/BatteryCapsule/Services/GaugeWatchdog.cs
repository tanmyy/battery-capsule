using System.IO;
using System.Text.Json;

namespace BatteryCapsule.Services;

/// <summary>
/// Gauge reliability watchdog. On machines whose firmware only exposes the
/// reported percentage (no voltage, current, or capacities), no app can compute
/// an independent "real" percentage. What CAN be done is watch the gauge's
/// behavior over time and catch it lying:
///   - impossible drops between polls (faster than physically possible),
///   - upward recalibration jumps while on battery (it was wrong before),
///   - abrupt power loss at a high reported charge (it died at "90%").
///
/// All state is a small local JSON file. Nothing leaves the machine.
/// </summary>
public sealed class GaugeWatchdog
{
    public sealed class SuspectDeath
    {
        public int ReportedPercent { get; set; }
        public DateTime Utc { get; set; }
    }

    private sealed class WatchdogState
    {
        public bool CleanExit { get; set; } = true;
        public int? LastReportedPercent { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public List<SuspectDeath> SuspectDeaths { get; set; } = new();
        public int SuddenDropsSeen { get; set; }
    }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BatteryCapsule", "watchdog.json");

    private WatchdogState _state = new();
    private int? _lastPollPercent;
    private DateTime _lastPollUtc;
    private int _pollsSinceSave;

    /// <summary>True when the most recent sample looked impossible.</summary>
    public bool LastSampleSuspicious { get; private set; }
    public string? LastSuspicionReason { get; private set; }

    /// <summary>True if a suspect death was recorded during this startup.</summary>
    public bool DeathRecordedThisStartup { get; private set; }
    public SuspectDeath? LatestDeath => _state.SuspectDeaths.Count == 0 ? null : _state.SuspectDeaths[^1];
    public IReadOnlyList<SuspectDeath> SuspectDeaths => _state.SuspectDeaths;
    public int SuddenDropsSeen => _state.SuddenDropsSeen;

    public void OnStartup()
    {
        Load();
        DeathRecordedThisStartup = false;

        // An unclean previous session plus a healthy last-reported charge means
        // the machine lost power while the gauge claimed plenty: the gauge lied.
        // Ignore stale state (older than 7 days) and low readings (a worn battery
        // dying at 15% is normal wear, not a lying gauge).
        bool stateFresh = (DateTime.UtcNow - _state.LastSeenUtc) < TimeSpan.FromDays(7);
        if (!_state.CleanExit && stateFresh &&
            _state.LastReportedPercent is int lastPct && lastPct >= 20)
        {
            _state.SuspectDeaths.Add(new SuspectDeath { ReportedPercent = lastPct, Utc = _state.LastSeenUtc });
            while (_state.SuspectDeaths.Count > 20) _state.SuspectDeaths.RemoveAt(0);
            DeathRecordedThisStartup = true;
        }

        _state.CleanExit = false;
        Save();
    }

    public void OnCleanExit()
    {
        _state.CleanExit = true;
        Save();
    }

    /// <summary>
    /// Feed each poll's Windows-reported percent. Returns (suspicious, reason).
    /// </summary>
    public (bool suspicious, string? reason) EvaluateSample(int? windowsPercent, bool onAcPower)
    {
        LastSampleSuspicious = false;
        LastSuspicionReason = null;

        if (windowsPercent.HasValue)
        {
            if (_lastPollPercent.HasValue)
            {
                int delta = windowsPercent.Value - _lastPollPercent.Value;
                double seconds = (DateTime.UtcNow - _lastPollUtc).TotalSeconds;

                // A long gap means the machine was asleep/hibernating: don't judge
                // the jump across it.
                if (seconds <= 600)
                {
                    if (delta <= -5)
                    {
                        LastSampleSuspicious = true;
                        LastSuspicionReason =
                            $"Battery % fell {Math.Abs(delta)} pts in {seconds:0}s, faster than physically possible. The gauge is unreliable.";
                        _state.SuddenDropsSeen++;
                    }
                    else if (!onAcPower && delta >= 10)
                    {
                        LastSampleSuspicious = true;
                        LastSuspicionReason =
                            $"Battery % jumped up {delta} pts while on battery. The gauge recalibrated, so it was wrong before.";
                    }
                    else if (onAcPower && delta <= -3)
                    {
                        LastSampleSuspicious = true;
                        LastSuspicionReason =
                            $"Battery % fell {Math.Abs(delta)} pts while plugged in. That should not happen.";
                    }
                }
            }
            _lastPollPercent = windowsPercent.Value;
            _lastPollUtc = DateTime.UtcNow;
            _state.LastReportedPercent = windowsPercent.Value;
            _state.LastSeenUtc = DateTime.UtcNow;
        }

        // Persist regularly and on every suspicious event, so an abrupt shutdown
        // still leaves a fresh last-reported value behind.
        _pollsSinceSave++;
        if (LastSampleSuspicious || _pollsSinceSave >= 6)
        {
            _pollsSinceSave = 0;
            Save();
        }

        return (LastSampleSuspicious, LastSuspicionReason);
    }

    public string Verdict()
    {
        if (_state.SuspectDeaths.Count == 0 && _state.SuddenDropsSeen == 0)
            return "No problems caught yet. Keep it running and it will catch the gauge lying.";

        if (_state.SuspectDeaths.Count > 0)
        {
            int worst = _state.SuspectDeaths.Max(d => d.ReportedPercent);
            return $"Died at a reported {worst}% before. Treat high readings with suspicion.";
        }

        return $"Caught {_state.SuddenDropsSeen} impossible drop(s). The gauge has proven jumpy.";
    }

    public void Reset()
    {
        _state.SuspectDeaths.Clear();
        _state.SuddenDropsSeen = 0;
        _lastPollPercent = null;
        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<WatchdogState>(json);
                if (loaded != null) _state = loaded;
            }
        }
        catch
        {
            _state = new WatchdogState();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best-effort; a failed save shouldn't crash a lightweight widget.
        }
    }
}
