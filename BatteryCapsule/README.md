# Battery Capsule

A tiny always-on-top battery pill widget for Windows, with an expandable detail panel
showing real telemetry (voltage, power draw, health, capacity) read directly from the
Windows battery driver — no internet connection, no telemetry collection, everything local.

## What's in here

```
BatteryCapsule.sln
BatteryCapsule/
  App.xaml / App.xaml.cs        - startup, polling loop, tray icon, single-instance guard
  Models/BatteryTelemetry.cs    - RawBatteryInfo (measured) + BatterySnapshot (measured+calculated+estimated)
  Native/                       - (empty; GetSystemPowerStatus P/Invoke lives inline in App.xaml.cs)
  Services/
    BatteryReader.cs            - reads raw telemetry via the battery class driver IOCTLs (SetupAPI)
    EstimationEngine.cs         - combines multi-battery telemetry, computes health/%/time-remaining, smoothing
    AutoStartService.cs         - "start with Windows" registry Run key
    SettingsService.cs          - local JSON settings persistence
    HistoryAndNotifications.cs  - in-memory history ring buffer + opt-in balloon notifications
  Views/
    CapsuleWindow.xaml(.cs)     - the draggable pill
    ExpandedPanelWindow.xaml(.cs) - click-to-expand detail panel + mini graph
    SettingsWindow.xaml(.cs)    - settings UI
```

## How to build

You'll need **Visual Studio 2022** (Community is fine) with the **.NET desktop development**
workload, or just the **.NET 8 SDK** on Windows.

**Visual Studio:** open `BatteryCapsule.sln`, set configuration to `Release`, build, run.

**Command line (from a Windows machine with the .NET 8 SDK):**
```
cd BatteryCapsule
dotnet build -c Release
dotnet run -c Release
```

To produce a single portable .exe you can just double-click:
```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```
(`--self-contained true` instead if you want it to run on a machine without the .NET runtime
installed — the exe will just be bigger.)

This **will not build on macOS/Linux** — WPF is Windows-only by design, which is also why I
couldn't compile-test this in my own sandbox. If you hit a build error, paste it back to me
and I'll fix it; that's a normal part of building something this size sight-unseen.

## How it works, briefly

- **Reported %** comes from `GetSystemPowerStatus` — the exact call Windows' own taskbar icon uses.
- **Estimated %** comes from `RemainingCapacity / FullChargedCapacity` read straight from the
  battery driver, smoothed slightly over time. It's labeled "Calculated" rather than a mystical
  independent measurement, because — as discussed — both numbers ultimately trace back to the
  same fuel-gauge chip. The panel flags a genuine mismatch (≥10 points, or a stale Windows
  reading during active charge/discharge) rather than claiming to always know better.
- **Time remaining** uses a rolling average of recent power draw (watts) against remaining
  energy (Wh), so it reacts faster to real usage changes than Windows' own generic estimate,
  with a confidence label (Low/Medium/High) based on how stable recent readings are.
- **Battery health** = Full Charge Capacity ÷ Design Capacity × 100 — a real, useful number
  Windows doesn't show anywhere in the UI by default.
- **Temperature and Current** will show "N/A" on most laptops. Temperature in particular is
  rarely exposed by consumer battery firmware to Windows at all — that's expected, not a bug.
- Multiple batteries are summed for capacity/energy and combined sensibly for status.
- No battery → the capsule shows "No battery" (desktop PCs) rather than fabricating a reading.

## Known rough edges to expect on first run

- The mini graph needs a few polling cycles (default every 10s) before it has enough points to draw.
- Temperature will likely read N/A on your machine — that's normal, see above.
- The "Estimated vs Windows" gap will usually be small; a persistent large gap more likely means
  a bug worth reporting than a real hardware discrepancy (see our earlier discussion on this).
- Settings currently apply live for capsule appearance/frequency; a couple of visual settings
  (transparency change) may need the capsule to redraw once — let me know if anything looks off
  and I'll patch it.

## Next steps I'd suggest

1. Build and run it on your actual laptop first — that tells us immediately how close your
   estimated vs Windows % really are, and whether temperature/current show real values on your hardware.
2. Tell me what you see and I'll tune the estimation engine's thresholds/smoothing to match.
3. If you want an actual icon (right now it uses the generic system icon in the tray) or a
   different visual style, send a description or reference image and I'll adjust the XAML.
