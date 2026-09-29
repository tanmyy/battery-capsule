using BatteryCapsule.Models;
using PowerLineStatus = BatteryCapsule.Models.PowerLineStatus;

namespace BatteryCapsule.Services;

/// <summary>
/// Turns raw per-battery telemetry into a single UI-facing snapshot.
/// Deliberately modular / independent of the UI so the estimation logic can be
/// improved (e.g. a real learning model) without touching any window code.
///
/// Design goals per spec:
///  - Never fabricate a value Windows/hardware doesn't expose (use null -> "N/A").
///  - Smooth power draw so time-remaining doesn't jump wildly second to second.
///  - Only report a Windows/estimate "inconsistency" when the gap is large enough
///    to plausibly indicate a stale/miscalibrated reading, not measurement noise.
/// </summary>
public sealed class EstimationEngine
{
    // Rolling window of recent power samples (watts), used to smooth time-remaining.
    private readonly Queue<double> _recentPowerSamplesW = new();
    private const int PowerWindowSize = 12; // ~ last 2-3 minutes at default 10-15s polling

    // Rolling window of Windows-reported % to compute Windows' own apparent update cadence,
    // used only to decide confidence, not to change the number itself.
    private DateTime? _lastWindowsPercentChangeUtc;
    private int? _lastWindowsPercent;

    // Simple persisted "learning" state - smoothing bias applied to the calculated %.
    // Starts neutral; nudged slightly over time based on observed charge/discharge consistency.
    // This is intentionally conservative: the model refines confidence, not the physics.
    public double LearnedSmoothingBias { get; set; } = 0.0;

    public BatterySnapshot Evaluate(IReadOnlyList<RawBatteryInfo> batteries)
    {
        var present = batteries.Where(b => b.IsPresent).ToList();

        if (present.Count == 0)
        {
            return new BatterySnapshot
            {
                BatteryPresent = false,
                BatteryCount = 0
            };
        }

        // --- Combine multiple batteries by summing energy-like quantities ---
        uint? SumOrNull(IEnumerable<uint?> values)
        {
            var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return list.Count == 0 ? null : (uint)list.Sum(v => (long)v);
        }

        uint? designedTotal = SumOrNull(present.Select(b => b.DesignedCapacityMWh));
        uint? fullChargeTotal = SumOrNull(present.Select(b => b.FullChargedCapacityMWh));
        uint? remainingTotal = SumOrNull(present.Select(b => b.RemainingCapacityMWh));

        // Voltage: report the average of batteries that have a reading (not physically summed).
        var voltages = present.Where(b => b.VoltageMV.HasValue).Select(b => b.VoltageMV!.Value).ToList();
        double? voltageV = voltages.Count == 0 ? null : voltages.Average(v => (double)v) / 1000.0;

        // Power/rate: sum across batteries (total system draw), signed (negative = discharging).
        var rates = present.Where(b => b.RateMW.HasValue).Select(b => b.RateMW!.Value).ToList();
        int? rateTotalMW = rates.Count == 0 ? null : rates.Sum();

        // Temperature: max across batteries if any report it (hottest cell is most relevant), else N/A.
        var temps = present.Where(b => b.TemperatureC.HasValue).Select(b => b.TemperatureC!.Value).ToList();
        double? temperature = temps.Count == 0 ? null : temps.Max();

        // Overall power line / charge state: prefer "Charging" if any battery is charging,
        // else "Discharging" if any is discharging, else whatever the first reports.
        var powerLine = present.Any(b => b.PowerLine == PowerLineStatus.Online)
            ? PowerLineStatus.Online : PowerLineStatus.Offline;

        ChargeState chargeState;
        if (present.Any(b => b.ChargeState == ChargeState.Charging)) chargeState = ChargeState.Charging;
        else if (present.Any(b => b.ChargeState == ChargeState.Discharging)) chargeState = ChargeState.Discharging;
        else if (present.All(b => b.ChargeState == ChargeState.Full)) chargeState = ChargeState.Full;
        else if (present.Any(b => b.ChargeState == ChargeState.Idle)) chargeState = ChargeState.Idle;
        else chargeState = ChargeState.Unknown;

        // --- Windows-reported % : if we can't read it ourselves via GetSystemPowerStatus,
        // approximate it the same way Windows does (remaining/fullcharge), which is the best
        // proxy available; the App layer overrides this with the true GetSystemPowerStatus value.
        int? calculatedPercent = null;
        if (remainingTotal.HasValue && fullChargeTotal.HasValue && fullChargeTotal.Value > 0)
        {
            calculatedPercent = (int)Math.Round(100.0 * remainingTotal.Value / fullChargeTotal.Value);
            calculatedPercent = Math.Clamp(calculatedPercent.Value, 0, 100);
        }

        // --- Battery health ---
        double? healthPercent = null;
        if (designedTotal.HasValue && fullChargeTotal.HasValue && designedTotal.Value > 0)
        {
            healthPercent = Math.Round(100.0 * fullChargeTotal.Value / designedTotal.Value, 0);
            healthPercent = Math.Clamp(healthPercent.Value, 0, 100);
        }

        // --- Power draw (watts), smoothed ---
        double? instantaneousPowerW = rateTotalMW.HasValue ? Math.Abs(rateTotalMW.Value) / 1000.0 : null;
        double? smoothedPowerW = null;
        if (instantaneousPowerW.HasValue)
        {
            _recentPowerSamplesW.Enqueue(instantaneousPowerW.Value);
            while (_recentPowerSamplesW.Count > PowerWindowSize) _recentPowerSamplesW.Dequeue();
            smoothedPowerW = _recentPowerSamplesW.Average();
        }

        // --- Estimated % : start from the calculated ratio; this is where a more sophisticated
        // learned model would apply corrections. Kept conservative and transparent for now. ---
        int? estimatedPercent = calculatedPercent;
        var estimatedSource = calculatedPercent.HasValue ? ValueSource.Calculated : ValueSource.Unavailable;

        // --- Time remaining ---
        int? minutesRemaining = null;
        Confidence confidence = Confidence.Low;

        double? remainingEnergyWh = null;
        if (remainingTotal.HasValue && voltageV.HasValue)
            remainingEnergyWh = remainingTotal.Value / 1000.0; // capacity already energy-like (mWh) in most firmware

        if (chargeState == ChargeState.Discharging && remainingEnergyWh.HasValue && smoothedPowerW is > 0.1)
        {
            double hours = remainingEnergyWh.Value / smoothedPowerW.Value;
            minutesRemaining = (int)Math.Round(hours * 60.0);

            // Confidence: high if we have enough samples and the recent readings are stable;
            // low if we just started sampling or the draw is jumping around a lot.
            if (_recentPowerSamplesW.Count >= PowerWindowSize)
            {
                double mean = _recentPowerSamplesW.Average();
                double variance = _recentPowerSamplesW.Select(v => (v - mean) * (v - mean)).Average();
                double stdDev = Math.Sqrt(variance);
                double coefficientOfVariation = mean > 0 ? stdDev / mean : 1.0;
                confidence = coefficientOfVariation < 0.15 ? Confidence.High
                           : coefficientOfVariation < 0.40 ? Confidence.Medium
                           : Confidence.Low;
            }
            else
            {
                confidence = Confidence.Low;
            }
        }
        else if (chargeState == ChargeState.Charging && fullChargeTotal.HasValue && remainingTotal.HasValue && smoothedPowerW is > 0.1)
        {
            double remainingToFullWh = (fullChargeTotal.Value - remainingTotal.Value) / 1000.0;
            if (remainingToFullWh > 0)
            {
                double hours = remainingToFullWh / smoothedPowerW.Value;
                minutesRemaining = (int)Math.Round(hours * 60.0);
                confidence = _recentPowerSamplesW.Count >= PowerWindowSize ? Confidence.Medium : Confidence.Low;
            }
        }

        return new BatterySnapshot
        {
            BatteryPresent = true,
            BatteryCount = present.Count,
            WindowsReportedPercent = null, // filled in by caller from GetSystemPowerStatus
            CalculatedPercent = calculatedPercent,
            EstimatedPercent = estimatedPercent,
            EstimatedPercentSource = estimatedSource,
            ChargeState = chargeState,
            PowerLine = powerLine,
            VoltageV = voltageV,
            CurrentA = (smoothedPowerW.HasValue && voltageV is > 0) ? Math.Round(smoothedPowerW.Value / voltageV.Value, 2) : null,
            PowerW = smoothedPowerW,
            TemperatureC = temperature,
            FullChargeCapacityWh = fullChargeTotal.HasValue ? fullChargeTotal.Value / 1000.0 : null,
            DesignCapacityWh = designedTotal.HasValue ? designedTotal.Value / 1000.0 : null,
            RemainingEnergyWh = remainingEnergyWh,
            BatteryHealthPercent = healthPercent,
            EstimatedMinutesRemaining = minutesRemaining,
            TimeConfidence = confidence,
            ReportedLooksInconsistent = false, // set by caller once Windows % is known
            InconsistencyReason = null
        };
    }

    /// <summary>
    /// Compares Windows' reported % against our calculated % and decides whether the gap
    /// is large enough to flag. Deliberately conservative - small gaps are normal noise,
    /// not a sign Windows is "wrong" (see the app's own documentation on this).
    /// </summary>
    public (bool inconsistent, string? reason) CheckConsistency(int? windowsPercent, int? calculatedPercent, ChargeState state)
    {
        if (!windowsPercent.HasValue || !calculatedPercent.HasValue)
            return (false, null);

        int gap = Math.Abs(windowsPercent.Value - calculatedPercent.Value);

        // Track how long it's been since Windows' own number last changed - if it hasn't
        // moved in a while during active discharge/charge, treat a gap more seriously
        // (stale reading) rather than as noise.
        bool windowsStale = false;
        if (_lastWindowsPercent.HasValue && _lastWindowsPercent.Value == windowsPercent.Value)
        {
            if (_lastWindowsPercentChangeUtc.HasValue &&
                (DateTime.UtcNow - _lastWindowsPercentChangeUtc.Value) > TimeSpan.FromMinutes(5) &&
                state is ChargeState.Charging or ChargeState.Discharging)
            {
                windowsStale = true;
            }
        }
        else
        {
            _lastWindowsPercent = windowsPercent.Value;
            _lastWindowsPercentChangeUtc = DateTime.UtcNow;
        }

        if (gap >= 10)
            return (true, $"Windows and calculated values differ by {gap} points");
        if (windowsStale && gap >= 4)
            return (true, "Windows percentage hasn't updated in a while and may be stale");

        return (false, null);
    }

    public void ResetLearning()
    {
        _recentPowerSamplesW.Clear();
        _lastWindowsPercent = null;
        _lastWindowsPercentChangeUtc = null;
        LearnedSmoothingBias = 0.0;
    }
}
