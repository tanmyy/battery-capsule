namespace BatteryCapsule.Models;

/// <summary>
/// How a value in the model was obtained. The UI uses this to label things honestly
/// instead of presenting calculated/estimated numbers as if they were direct sensor reads.
/// </summary>
public enum ValueSource
{
    Measured,   // Came directly from a Windows/ACPI battery API call
    Calculated, // Simple deterministic math on measured values (e.g. Rate / Voltage)
    Estimated,  // Produced by our rolling estimation model; inherently uncertain
    Unavailable // Not exposed by this hardware/firmware - never fabricated
}

public enum PowerLineStatus
{
    Unknown,
    Offline,
    Online // plugged into AC
}

public enum ChargeState
{
    Unknown,
    Discharging,
    Charging,
    Idle,       // plugged in, not charging (already full or trickle)
    Full
}

/// <summary>
/// One battery's raw, directly-measured state as read from the Win32 battery IOCTLs.
/// Any field may be null/Unavailable if the firmware doesn't expose it - never invented.
/// </summary>
public sealed class RawBatteryInfo
{
    public string DeviceId { get; init; } = "";

    // --- Static per-battery info (queried once, rarely changes) ---
    public uint? DesignedCapacityMWh { get; set; }      // Measured
    public uint? FullChargedCapacityMWh { get; set; }   // Measured
    public uint CycleCount { get; set; }                // Measured (0 if unsupported)
    public string Chemistry { get; set; } = "";          // Measured

    // --- Dynamic status (polled frequently) ---
    public PowerLineStatus PowerLine { get; set; } = PowerLineStatus.Unknown; // Measured
    public ChargeState ChargeState { get; set; } = ChargeState.Unknown;      // Measured
    public uint? RemainingCapacityMWh { get; set; }     // Measured
    public uint? VoltageMV { get; set; }                // Measured
    public int? RateMW { get; set; }                    // Measured; negative = discharging, positive = charging
    public double? TemperatureC { get; set; }            // Measured where available; usually Unavailable

    public bool IsPresent { get; set; } = true;
}

/// <summary>
/// The combined, UI-facing snapshot: raw values plus everything the estimation
/// engine derives from them, each tagged with its provenance.
/// </summary>
public sealed record BatterySnapshot
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public bool BatteryPresent { get; init; }
    public int BatteryCount { get; init; }

    // Reported (Windows/EC-level) percentage - Measured
    public int? WindowsReportedPercent { get; init; }

    // Our percentage, computed from raw capacity (RemainingCapacity / FullChargedCapacity).
    // Marked Calculated rather than Estimated because it's a direct ratio of measured values,
    // not a probabilistic model output.
    public int? CalculatedPercent { get; init; }

    // Our best estimate of "true" remaining charge, after smoothing/health adjustment.
    // This is the number shown as "Estimated %" - explicitly not guaranteed accurate.
    public int? EstimatedPercent { get; init; }
    public ValueSource EstimatedPercentSource { get; init; } = ValueSource.Unavailable;

    public ChargeState ChargeState { get; init; }
    public PowerLineStatus PowerLine { get; init; }

    public double? VoltageV { get; init; }               // Measured
    public double? CurrentA { get; init; }                // Calculated (Rate / Voltage)
    public double? PowerW { get; init; }                  // Measured (from Rate), absolute value
    public double? TemperatureC { get; init; }             // Measured where available, else Unavailable

    public double? FullChargeCapacityWh { get; init; }    // Measured
    public double? DesignCapacityWh { get; init; }        // Measured
    public double? RemainingEnergyWh { get; init; }        // Measured
    public double? BatteryHealthPercent { get; init; }     // Calculated: FullCharge/Design * 100

    // Time remaining
    public int? EstimatedMinutesRemaining { get; init; }
    public Confidence TimeConfidence { get; init; } = Confidence.Low;

    public bool ReportedLooksInconsistent { get; init; }
    public string? InconsistencyReason { get; init; }
}

public enum Confidence { Low, Medium, High }
