using DynoCommandPC.Protocol;

namespace DynoCommandPC.Settings;

/// <summary>
/// Persisted operator settings — the PC-app equivalent of the original
/// PdaSettings.xml. Serialized to settings.json beside the executable and
/// created with defaults on first run.
/// </summary>
public sealed class AppSettings
{
    // --- Connection ---
    // A 12-hex Bluetooth MAC (e.g. 0012F304962C) => direct RFCOMM/SPP; an IP:port
    // (e.g. 127.0.0.1:51730 or localhost:51730) => transparent TCP to a simulator.
    public string EdmBluetoothAddress { get; set; } = "0012F304962C";
    public bool AutoConnectAtStartup { get; set; }
    public bool LogCommsToFile { get; set; }

    // --- Dyno type ---
    public bool Is4Wd { get; set; }

    // --- 4WD bed travel limits (metres). The dyno doesn't report these; they mirror
    // the original's BedLength config. Shorten drives to Min, Lengthen to Max. ---
    public double BedLengthMin { get; set; } = 2.25;
    public double BedLengthMax { get; set; } = 3.5;

    // --- Diagnostics panel visibility (main screen) ---
    public bool ShowPacketLog { get; set; } = true;
    public bool ShowPacketsSeen { get; set; } = true;
    /// <summary>Show the raw inbound-byte hex panel (pre-framing) + capture to logs\dyno-raw-*.log.</summary>
    public bool ShowRawRx { get; set; }

    // --- Feedback ---
    public bool AudibleFeedback { get; set; }

    // --- Speed ---
    public SpeedUnit SpeedUnits { get; set; } = SpeedUnit.Kph;
    public SpeedControl SpeedControl { get; set; } = SpeedControl.Road;
    public double RollerDiameter { get; set; } = 0.2186; // metres
    public double GearRatio { get; set; } = 1.0;
    public double SpeedMaxKph { get; set; } = 200;
    public double SpeedMaxMph { get; set; } = 124;
    public double SpeedMaxRpm { get; set; } = 5000;

    // Speed-nudge increments, held per unit — a step that suits km/h (±1/±5) is
    // uselessly fine in rpm (which wants ±10/±100), so the active set follows the
    // effective mode. Mirrors the original's per-unit SpeedInc_* values.
    public double SpeedIncSmallKph { get; set; } = 1;
    public double SpeedIncLargeKph { get; set; } = 5;
    public double SpeedIncSmallMph { get; set; } = 1;
    public double SpeedIncLargeMph { get; set; } = 5;
    public double SpeedIncSmallRpm { get; set; } = 100;
    public double SpeedIncLargeRpm { get; set; } = 1000;
    /// <summary>Operator-defined run start speed (in the active speed unit).</summary>
    public double StartSpeed { get; set; } = 80;

    public double MaxForCurrentUnit() => SpeedUnits switch
    {
        SpeedUnit.Rpm => SpeedMaxRpm,
        SpeedUnit.Mph => SpeedMaxMph,
        _ => SpeedMaxKph
    };

    /// <summary>True when speed is commanded in engine terms — rpm units, or Engine
    /// control regardless of unit. Matches the gate used by the governor, the TX
    /// encoder and the ramp start-speed logic.</summary>
    public bool IsEngineMode => SpeedUnits == SpeedUnit.Rpm || SpeedControl == SpeedControl.Engine;

    /// <summary>Small nudge increment for the active mode/unit.</summary>
    public double ActiveSpeedIncSmall() =>
        IsEngineMode ? SpeedIncSmallRpm : SpeedUnits == SpeedUnit.Mph ? SpeedIncSmallMph : SpeedIncSmallKph;

    /// <summary>Large nudge increment for the active mode/unit.</summary>
    public double ActiveSpeedIncLarge() =>
        IsEngineMode ? SpeedIncLargeRpm : SpeedUnits == SpeedUnit.Mph ? SpeedIncLargeMph : SpeedIncLargeKph;

    public string UnitLabel() => SpeedUnits switch
    {
        SpeedUnit.Rpm => "rpm",
        SpeedUnit.Mph => "mph",
        _ => "km/h"
    };
}
