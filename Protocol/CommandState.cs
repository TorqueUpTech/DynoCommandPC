namespace DynoCommandPC.Protocol;

/// <summary>
/// The "requested" side — what the operator wants the dyno to do. The TX loop
/// reads this each cycle to build outgoing packets (knob, digital inputs, mode).
/// </summary>
public sealed class CommandState
{
    public DynoMode Mode = DynoMode.NoLoad;

    /// <summary>What the operator dialled in (active speed unit). The safety
    /// governor feeds this into <see cref="TargetSpeed"/> gradually.</summary>
    public double RequestedSpeed;
    /// <summary>Governed target actually sent by the knob packet (active unit).</summary>
    public double TargetSpeed;
    /// <summary>Target load (%/Nm units as configured).</summary>
    public double TargetLoad;
    /// <summary>Operator-set ramp rate.</summary>
    public double RampRate = 80;
    /// <summary>Operator-defined run start speed (active speed unit). Mirrored from settings.</summary>
    public double StartSpeed = 80;
    /// <summary>True while a shootout is active (PC owns the ramp rate); set by the controller from received state.</summary>
    public bool ShootoutActive;
    /// <summary>PC-dictated ramp rate, mirrored from received state.</summary>
    public double PcRampRate;
    /// <summary>Rate actually sent: the PC's during a shootout, otherwise the operator's.</summary>
    public double EffectiveRampRate => ShootoutActive ? PcRampRate : RampRate;
    /// <summary>4WD front/rear balance value.</summary>
    public double Balance;
    public bool BalanceActive;

    public SpeedUnit SpeedUnits = SpeedUnit.Kph;
    public SpeedControl SpeedControl = SpeedControl.Road;

    public FanMode Fan1 = FanMode.PC;
    public FanMode Fan2 = FanMode.PC;

    /// <summary>Bed target position in metres (Set-Bed mode).</summary>
    public double BedRequestedPosition;

    // Physical config used for speed scaling.
    public double GearRatio = 1.0;
    public double RollerDiameter = 0.2186;
    public double RollerCircumference => RollerDiameter * Math.PI;

    // Config the handheld reports back when the dyno queries it (CMD 0x3F) or
    // exchanges the BT address (CMD 0xD1). Mirrored from AppSettings.
    public double SpeedMaxKph = 200, SpeedMaxMph = 124, SpeedMaxRpm = 5000;
    public int FeatureLevel;
    public bool DemoMode;
    /// <summary>EDM Bluetooth MAC (12 hex digits) — what we answer the address handshake with.</summary>
    public string EdmAddress = "";

    // Convert between the active road-speed unit and km/h (rpm passes through; the
    // governor is gated to road control so rpm conversion is never used).
    public double ToKph(double v) => SpeedUnits == SpeedUnit.Mph ? v * 1.609344 : v;
    public double FromKph(double kph) => SpeedUnits == SpeedUnit.Mph ? kph / 1.609344 : kph;

    // Roller revs/sec is the physical core the original (SYS_Unit) stored speed in;
    // every unit is a view of it (rpm = roller rpm = revs/s x 60). Used to preserve
    // the physical roller speed when the display unit changes, as the original did.
    private double ToRevsPerSec(double value, SpeedUnit unit) => unit switch
    {
        SpeedUnit.Mph => value * 1.609344 / (RollerCircumference * 3.6),
        SpeedUnit.Rpm => value / 60.0,
        _             => value / (RollerCircumference * 3.6), // Kph
    };
    private double FromRevsPerSec(double revs, SpeedUnit unit) => unit switch
    {
        SpeedUnit.Mph => revs * RollerCircumference * 3.6 * 0.621371192237334,
        SpeedUnit.Rpm => revs * 60.0,
        _             => revs * RollerCircumference * 3.6, // Kph
    };
    /// <summary>Re-express a speed in a different unit, preserving the physical roller speed.</summary>
    public double ConvertSpeed(double value, SpeedUnit from, SpeedUnit to) =>
        FromRevsPerSec(ToRevsPerSec(value, from), to);

    public bool Is4Wd;

    // One-shot requests (decremented as they are sent).
    public int GraphClearRequests;
    public int SnapshotRequests;
    public int SwapScreenRequests;
    /// <summary>When >0, briefly send the Speed command bit to clear the dyno's latched brake warning.</summary>
    public int BrakeClearRequests;
}
