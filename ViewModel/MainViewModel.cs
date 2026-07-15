using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using DynoCommandPC.Protocol;
using DynoCommandPC.Settings;
using DynoCommandPC.Transport;

namespace DynoCommandPC.ViewModel;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly DynoController _ctrl = new();
    private readonly DispatcherTimer _ui;
    private AppSettings _settings;

    public MainViewModel()
    {
        _settings = SettingsStore.Load();
        ApplySettings();

        // Startup default target: 80 km/h (expressed in the active unit).
        _ctrl.Command.RequestedSpeed = Math.Min(_ctrl.Command.FromKph(80.0), _settings.MaxForCurrentUnit());

        ConnectCommand    = new RelayCommand(Connect, () => !IsConnected);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        SetModeCommand    = new RelayCommand(p => { SetMode(Enum.Parse<DynoMode>((string)p!)); Beep(); });
        ToggleBrakeCommand = new RelayCommand(ToggleBrake);
        AdvancedCycleCommand = new RelayCommand(() => RunCycle(basic: false));
        BasicCycleCommand    = new RelayCommand(() => RunCycle(basic: true));
        CancelRampCommand    = new RelayCommand(CancelRamp);
        NudgeSpeedCommand = new RelayCommand(p => TargetSpeed += SpeedStep((string)p!));
        NudgeLoadCommand  = new RelayCommand(p => TargetLoad  += double.Parse((string)p!));
        NudgeRampCommand  = new RelayCommand(p => RampRate    += double.Parse((string)p!));
        NudgeBedCommand   = new RelayCommand(p => BedPosition += double.Parse((string)p!));
        NudgeBalanceCommand = new RelayCommand(p => Balance += double.Parse((string)p!));
        SnapshotCommand   = new RelayCommand(() => { _ctrl.Command.SnapshotRequests++; Beep(); });
        GraphClearCommand = new RelayCommand(() => { _ctrl.Command.GraphClearRequests++; Beep(); });
        SwapScreensCommand = new RelayCommand(() => { _ctrl.Command.SwapScreenRequests++; Beep(); });
        ClearLogCommand   = new RelayCommand(() => { _ctrl.ClearLog(); LogText = ""; OnPropertyChanged(nameof(LogText)); });
        SaveLogCommand    = new RelayCommand(SaveLog);
        ClearStatsCommand = new RelayCommand(() => _ctrl.ClearStats());

        _ui = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _ui.Tick += (_, _) => Refresh();
        _ui.Start();
    }

    // ---- connection (Bluetooth direct only) ----
    public bool IsConnected => _ctrl.IsConnected;
    public string EdmAddress => _settings.EdmBluetoothAddress;

    private string _status = "Disconnected";
    public string Status { get => _status; private set { _status = value; OnPropertyChanged(); } }

    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand SetModeCommand { get; }
    public RelayCommand ToggleBrakeCommand { get; }
    public RelayCommand AdvancedCycleCommand { get; }
    public RelayCommand BasicCycleCommand { get; }
    public RelayCommand CancelRampCommand { get; }
    public RelayCommand NudgeSpeedCommand { get; }
    public RelayCommand NudgeLoadCommand { get; }
    public RelayCommand NudgeRampCommand { get; }
    public RelayCommand NudgeBedCommand { get; }
    public RelayCommand NudgeBalanceCommand { get; }
    public RelayCommand SnapshotCommand { get; }
    public RelayCommand GraphClearCommand { get; }
    public RelayCommand SwapScreensCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand SaveLogCommand { get; }
    public RelayCommand ClearStatsCommand { get; }

    private void Connect() => _ = ConnectAsync();

    /// <summary>
    /// Called once when the main window has loaded. If the operator enabled
    /// "Auto-connect at startup", open the Bluetooth link to the EDM.
    /// </summary>
    public void TryAutoConnectAtStartup()
    {
        if (_settings.AutoConnectAtStartup && !IsConnected)
            _ = ConnectAsync();
    }

    private bool _connecting;
    private async Task ConnectAsync()
    {
        if (_connecting || IsConnected) return;

        if (string.IsNullOrWhiteSpace(_settings.EdmBluetoothAddress))
        {
            Status = "Set the EDM address in Settings";
            return;
        }

        _connecting = true;
        Status = "Connecting…";
        try
        {
            // Address field routes the transport:
            //   "ds" / "ds:<ip>"  -> DynoServer network as the handheld (loopback / that server IP)
            //   IP:port           -> transparent TCP (same 0xA0 stream) to a simulator
            //   12-hex MAC        -> Bluetooth RFCOMM (as before)
            var addr = _settings.EdmBluetoothAddress?.Trim() ?? "";
            ITransport transport;
            if (addr.Equals("ds", StringComparison.OrdinalIgnoreCase) || addr.Equals("dynoserver", StringComparison.OrdinalIgnoreCase))
                transport = new DynoServerTransport(System.Net.IPAddress.Loopback);
            else if (addr.StartsWith("ds:", StringComparison.OrdinalIgnoreCase) && System.Net.IPAddress.TryParse(addr[3..], out var dsIp))
                transport = new DynoServerTransport(dsIp);
            else if (TcpTransport.TryParseEndpoint(addr, out var tcpHost, out var tcpPort))
                transport = new TcpTransport(tcpHost, tcpPort);
            else
                transport = new BluetoothRfcommTransport(addr);
            _ctrl.LogEnabled = LogEnabled;
            _ctrl.SetRawCapture(_settings.ShowRawRx);
            // Open() can block while the RFCOMM link is negotiated — do it off the
            // UI thread so the window stays responsive (and startup doesn't stall).
            bool ok = await Task.Run(() => _ctrl.Connect(transport));
            if (ok)
                Status = $"Connected — {_ctrl.TransportName}";
            else
                Status = transport.LastError != null ? $"Connect failed — {transport.LastError}" : "Connect failed";
        }
        finally
        {
            _connecting = false;
            RaiseConnState();
        }
    }

    private void Disconnect()
    {
        _ctrl.Disconnect();
        Status = "Disconnected";
        RaiseConnState();
    }

    private void RaiseConnState()
    {
        OnPropertyChanged(nameof(IsConnected));
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
    }

    private void OpenSettings()
    {
        var editable = SettingsStore.Clone(_settings);
        var dlg = new SettingsWindow(editable) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() == true)
        {
            _settings = editable;
            SettingsStore.Save(_settings);
            ApplySettings();
        }
    }

    private void ApplySettings()
    {
        var c = _ctrl.Command;
        c.Is4Wd = _settings.Is4Wd;
        c.GearRatio = _settings.GearRatio;
        c.RollerDiameter = _settings.RollerDiameter;
        c.SpeedUnits = _settings.SpeedUnits;
        c.SpeedControl = _settings.SpeedControl;
        c.SpeedMaxKph = _settings.SpeedMaxKph;
        c.SpeedMaxMph = _settings.SpeedMaxMph;
        c.SpeedMaxRpm = _settings.SpeedMaxRpm;
        c.StartSpeed = _settings.StartSpeed;
        c.EdmAddress = _settings.EdmBluetoothAddress;
        _ctrl.SetCommsLogging(_settings.LogCommsToFile);
        if (IsConnected) _ctrl.SetRawCapture(_settings.ShowRawRx);
        if (c.TargetSpeed > _settings.MaxForCurrentUnit()) c.TargetSpeed = _settings.MaxForCurrentUnit();

        foreach (var n in new[]
        {
            nameof(EdmAddress), nameof(Is4Wd), nameof(SpeedUnitLabel), nameof(LiveUnitLabel),
            nameof(SpeedDecSmallLabel), nameof(SpeedIncSmallLabel),
            nameof(SpeedDecLargeLabel), nameof(SpeedIncLargeLabel), nameof(TargetSpeed),
            nameof(ShowPacketLog), nameof(ShowPacketsSeen), nameof(ShowRawRx)
        }) OnPropertyChanged(n);
        RaiseVisibility();
    }

    private void SaveLog()
    {
        var lines = _ctrl.LogSnapshot();
        if (lines.Length == 0)
        {
            Status = "No packets captured — tick 'Log packets' first";
            return;
        }
        var dlg = new SaveFileDialog
        {
            FileName = $"dyno-capture-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "Packet capture (*.txt)|*.txt|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                File.WriteAllLines(dlg.FileName, lines);
                Status = $"Saved {lines.Length} packets to {Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                Status = "Save failed: " + ex.Message;
            }
        }
    }

    private double SpeedStep(string token)
    {
        double mag = token.StartsWith("L") ? _settings.SpeedIncLarge : _settings.SpeedIncSmall;
        return token.EndsWith("-") ? -mag : mag;
    }

    public string SpeedDecLargeLabel => $"−{_settings.SpeedIncLarge:0.###}";
    public string SpeedDecSmallLabel => $"−{_settings.SpeedIncSmall:0.###}";
    public string SpeedIncSmallLabel => $"+{_settings.SpeedIncSmall:0.###}";
    public string SpeedIncLargeLabel => $"+{_settings.SpeedIncLarge:0.###}";
    public string SpeedUnitLabel => _settings.UnitLabel();

    /// <summary>Unit shown for the live road-speed read-outs (km/h or mph; rpm falls back to km/h).</summary>
    public string LiveUnitLabel => _settings.SpeedUnits == SpeedUnit.Mph ? "mph" : "km/h";

    // ---- command (requested) ----
    public string ModeText => _inRampRun
        ? $"{(_runIsBasic ? "Manual Ramp" : "Advanced")} — {ActiveSeq[_rampStep].State}"
        : _ctrl.Command.Mode.ToString();

    private void SetMode(DynoMode m)
    {
        _inRampRun = false;
        _brakePending = false;
        _ctrl.Command.Mode = m;
        RaiseModeUi();
    }

    public bool IsBrakeOn => _ctrl.Command.Mode == DynoMode.Brake;
    private bool _brakePending; // armed, waiting for speed < 5 km/h
    public string BrakeButtonText => IsBrakeOn ? "Brake: ON" : _brakePending ? "Brake: armed (<5)" : "Brake";

    // ---- Ramp runs: one cycling button per run type (mirrors the original) ----
    // The first step drives to the configured StartSpeed via Speed mode (the normal
    // Knob/Speed path — the protocol has no dedicated start-speed packet), then:
    // Advanced: Speed(start) -> Ramp Up -> Hold(finish) -> Ramp Down -> off.
    // Basic:    Speed(start) -> Ramp Up -> Hold(finish) -> off (no ramp-down; coasts).
    private static readonly (DynoMode Mode, string Next, string State)[] AdvancedSeq =
    {
        (DynoMode.Speed,    "Ramp Up",           "Drive to start speed"),
        (DynoMode.RampUp,   "Hold (finish run)", "Ramping up"),
        (DynoMode.RampHold, "Ramp Down",         "Hold (run finished)"),
        (DynoMode.RampDown, "End (free spin)",   "Ramping down"),
    };
    private static readonly (DynoMode Mode, string Next, string State)[] BasicSeq =
    {
        (DynoMode.Speed,    "Ramp Up",            "Drive to start speed"),
        (DynoMode.RampUp,   "Hold (finish run)",  "Ramping up"),
        (DynoMode.RampHold, "End (free spin)",    "Hold (run finished)"),
    };
    private (DynoMode Mode, string Next, string State)[] ActiveSeq => _runIsBasic ? BasicSeq : AdvancedSeq;

    private int _rampStep;
    private bool _inRampRun;
    private bool _runIsBasic;

    public string AdvancedButtonText => (_inRampRun && !_runIsBasic) ? AdvancedSeq[_rampStep].Next : "Advanced ▶";
    public string BasicButtonText    => (_inRampRun &&  _runIsBasic) ? BasicSeq[_rampStep].Next    : "Manual Ramp";
    public string AdvancedActiveTag  => (_inRampRun && !_runIsBasic) ? "ACTIVE" : "";
    public string BasicActiveTag     => (_inRampRun &&  _runIsBasic) ? "ACTIVE" : "";

    private void RunCycle(bool basic)
    {
        _brakePending = false;
        var seq = basic ? BasicSeq : AdvancedSeq;
        if (!_inRampRun || _runIsBasic != basic)
        {
            _inRampRun = true;
            _runIsBasic = basic;
            _rampStep = 0; // first press: drive to the configured start speed (Speed mode)
            _ctrl.Command.RequestedSpeed = Math.Min(_ctrl.Command.StartSpeed, _settings.MaxForCurrentUnit());
            OnPropertyChanged(nameof(TargetSpeed));
        }
        else if (_rampStep >= seq.Length - 1)
        {
            _inRampRun = false;
            _ctrl.Command.Mode = DynoMode.NoLoad; // end run -> free spin (coast)
            RaiseModeUi();
            return;
        }
        else
        {
            _rampStep++;
        }
        _ctrl.Command.Mode = seq[_rampStep].Mode;
        RaiseModeUi();
    }

    /// <summary>Abort an in-progress ramp run and return to the main mode screen (coast / No Load).</summary>
    private void CancelRamp()
    {
        _inRampRun = false;
        _brakePending = false;
        _ctrl.Command.Mode = DynoMode.NoLoad;
        RaiseModeUi();
        Beep();
    }

    private void ToggleBrake()
    {
        _inRampRun = false;
        if (_ctrl.Command.Mode == DynoMode.Brake || _brakePending)
        {
            // Release brake / cancel a pending arm.
            _brakePending = false;
            if (_ctrl.Command.Mode == DynoMode.Brake)
            {
                _ctrl.Command.Mode = DynoMode.NoLoad;
                _ctrl.Command.BrakeClearRequests = 6; // pulse Speed bit ~0.3 s to clear the PC brake warning
            }
        }
        else if (_ctrl.State.SpeedKph < BrakeEngageMaxKph)
        {
            _ctrl.Command.Mode = DynoMode.Brake; // safe to engage now
        }
        else
        {
            // Too fast to brake: arm and coast (No Load); auto-engage when below 5 km/h.
            _brakePending = true;
            _ctrl.Command.Mode = DynoMode.NoLoad;
        }
        RaiseModeUi();
    }

    // Shootout: PC takes over the ramp rate; the operator's rate control is locked.
    public bool IsShootoutActive => _ctrl.State.ShootoutActive;
    public bool RampUnlocked => !_ctrl.State.ShootoutActive;

    // The PC dictates ramp rate as a core (revs/s) value; convert it to the same
    // km/h-per-second working unit the handheld shows (x circumference x 3.6) so
    // the PC-locked figure reads on the same scale as the local ramp rate.
    private double PcRampRateDisplay =>
        _ctrl.State.PcRampRate * _ctrl.Command.RollerCircumference * 3.6;

    public string ShootoutBanner => _ctrl.State.ShootoutActive
        ? $"🔒 {_ctrl.State.ShootoutMode.ToUpper()} — PC is controlling the ramp rate, locked at {PcRampRateDisplay:F0} km/h/s"
        : "";

    // Context-sensitive control visibility.
    public bool ShowLoad => _ctrl.Command.Mode == DynoMode.Load && !_inRampRun;
    public bool ShowRamp => _inRampRun;
    public bool ShowBed => _ctrl.Command.Mode == DynoMode.SetBed && _ctrl.Command.Is4Wd;
    public bool Show4WdFeatures => _ctrl.Command.Is4Wd;

    // While a Manual Ramp / Advanced ramp cycle is running, the rest of the mode
    // panel is hidden so the operator steps the run through to completion without
    // fumbling into another mode. Only the active cycling button stays visible;
    // when the cycle ends (returns to No Load) every button reappears.
    public bool ShowModeButtons => !_inRampRun;                       // No Load / Speed / Load / Hands-free
    public bool ShowManualRampButton => !_inRampRun || _runIsBasic;   // idle, or the active basic run
    public bool ShowAdvancedButton   => !_inRampRun || !_runIsBasic;  // idle, or the active advanced run
    public bool ShowSetBedButton => _ctrl.Command.Is4Wd && !_inRampRun;
    public bool ShowRampCancel => _inRampRun;                         // abort button, only during a run

    // Brake is only offered when it can actually engage (at/below the interlock
    // speed), or while already engaged so it can be released. This also keeps it
    // hidden during a high-speed ramp and brings it back once the roller slows.
    private const double BrakeEngageMaxKph = 5.0;
    public bool ShowBrakeButton => IsBrakeOn || _ctrl.State.SpeedKph < BrakeEngageMaxKph;

    private void RaiseVisibility()
    {
        OnPropertyChanged(nameof(ShowLoad));
        OnPropertyChanged(nameof(ShowRamp));
        OnPropertyChanged(nameof(ShowBed));
        OnPropertyChanged(nameof(Show4WdFeatures));
        OnPropertyChanged(nameof(ShowBalance));
        OnPropertyChanged(nameof(ShowModeButtons));
        OnPropertyChanged(nameof(ShowManualRampButton));
        OnPropertyChanged(nameof(ShowAdvancedButton));
        OnPropertyChanged(nameof(ShowSetBedButton));
        OnPropertyChanged(nameof(ShowBrakeButton));
        OnPropertyChanged(nameof(ShowRampCancel));
    }

    private void RaiseModeUi()
    {
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(BrakeButtonText));
        OnPropertyChanged(nameof(AdvancedButtonText));
        OnPropertyChanged(nameof(BasicButtonText));
        OnPropertyChanged(nameof(AdvancedActiveTag));
        OnPropertyChanged(nameof(BasicActiveTag));
        RaiseVisibility();
    }

    public double TargetSpeed
    {
        get => _ctrl.Command.RequestedSpeed;
        set { _ctrl.Command.RequestedSpeed = Math.Clamp(value, 0, _settings.MaxForCurrentUnit()); OnPropertyChanged(); }
    }
    public double TargetLoad
    {
        get => _ctrl.Command.TargetLoad;
        set { _ctrl.Command.TargetLoad = Math.Max(0, value); OnPropertyChanged(); }
    }
    public double RampRate
    {
        get => _ctrl.Command.RampRate;
        set { _ctrl.Command.RampRate = Math.Max(0, value); OnPropertyChanged(); }
    }
    public double BedPosition
    {
        get => _ctrl.Command.BedRequestedPosition;
        set { _ctrl.Command.BedRequestedPosition = Math.Max(0, value); OnPropertyChanged(); }
    }

    public bool Is4Wd
    {
        get => _ctrl.Command.Is4Wd;
        set { _ctrl.Command.Is4Wd = value; OnPropertyChanged(); RaiseVisibility(); }
    }

    /// <summary>4WD front/rear balance. Non-zero arms the balance packet (BalanceActive).</summary>
    public double Balance
    {
        get => _ctrl.Command.Balance;
        set { _ctrl.Command.Balance = value; _ctrl.Command.BalanceActive = value != 0.0; OnPropertyChanged(); }
    }
    public bool ShowBalance => _ctrl.Command.Is4Wd;

    private void Beep()
    {
        if (_settings.AudibleFeedback) System.Media.SystemSounds.Beep.Play();
    }

    // Diagnostics panel visibility, driven by Settings.
    public bool ShowPacketLog => _settings.ShowPacketLog;
    public bool ShowPacketsSeen => _settings.ShowPacketsSeen;
    public bool ShowRawRx => _settings.ShowRawRx;

    public Array FanModes => Enum.GetValues(typeof(FanMode));
    public FanMode Fan1 { get => _ctrl.Command.Fan1; set { _ctrl.Command.Fan1 = value; OnPropertyChanged(); } }
    public FanMode Fan2 { get => _ctrl.Command.Fan2; set { _ctrl.Command.Fan2 = value; OnPropertyChanged(); } }

    private bool _logEnabled;
    public bool LogEnabled
    {
        get => _logEnabled;
        set { _logEnabled = value; _ctrl.LogEnabled = value; OnPropertyChanged(); }
    }

    // ---- live read-outs ----
    public string ActualSpeed { get; private set; } = "0.0";
    public string SetSpeed { get; private set; } = "0";
    public string SpeedRevs { get; private set; } = "0.000";
    public string RawMaster { get; private set; } = "0";
    public string CurrentRampRate { get; private set; } = "—";
    public string BrakeState { get; private set; } = "—";
    public string HandsfreeState { get; private set; } = "—";
    public string TxCount { get; private set; } = "0";
    public string RxCount { get; private set; } = "0";
    public string ErrCount { get; private set; } = "0";
    public string CommsLosses { get; private set; } = "0";
    public string LastTx { get; private set; } = "—";
    public string LastRx { get; private set; } = "—";
    public string LinkAge { get; private set; } = "no data";
    public string LogText { get; private set; } = "";
    public string RawText { get; private set; } = "";
    public string InspectorText { get; private set; } = "";

    // Dyno-reported config + bed read-back + device-online status.
    public string BedActual { get; private set; } = "—";
    public string BedTarget { get; private set; } = "—";
    public string TransmissionText { get; private set; } = "—";
    public string In4wdText { get; private set; } = "—";
    public string DeviceStatus { get; private set; } = "Pit ○  PC ○  Bed ○";

    private static string TransLabel(TransmissionType t) => t switch
    {
        TransmissionType.Standard2Wd          => "2WD",
        TransmissionType.Locked4Wd            => "4WD (locked)",
        TransmissionType.CenterDiff4Wd        => "4WD (centre diff)",
        TransmissionType.CenterViscous4Wd     => "4WD (centre viscous)",
        TransmissionType.ComputerDogClutch4Wd => "4WD (dog clutch)",
        TransmissionType.TractionStability4Wd => "4WD (traction/stability)",
        TransmissionType.FrontViscous4Wd      => "4WD (front viscous)",
        _                                     => "—"
    };

    private static string LabelFor(int id, int cmd, int p0)
    {
        int src = id & 0xF0;
        if (cmd == 0x0F && p0 == 0x0A && src == 0x20) return "roller speed (master)";
        if (cmd == 0x0F && p0 == 0x5B && src == 0x70) return "roller speed (slave)";
        if (cmd == 0x0F && p0 == 0x1A && src == 0x20) return "pit brake (master)";
        if (cmd == 0x0F && p0 == 0x56 && src == 0x70) return "pit brake (slave)";
        if (cmd == 0x25) return "HFR / shootout status";
        if (cmd == 0xA0 && p0 == 0x32) return "bed current pos";
        if (cmd == 0xA0 && p0 == 0x31) return "bed target pos";
        if (cmd == 0xA0 && p0 == 0x67) return "PC ramp rate";
        if (cmd == 0x6F && p0 == 0x60) return "speed-unit cmd";
        if (cmd == 0x2F) return "set config";
        if (cmd == 0x3F) return "get config";
        if (cmd == 0xFB) return "ping";
        if (cmd == 0xE5) return "FW ctrl";
        // Shootout run parameters the PC broadcasts (0F 05 block, decoded from logs).
        if (cmd == 0x05 && p0 == 0xA0) return "shootout start speed";
        if (cmd == 0x05 && p0 == 0xC0) return "shootout ramp rate";
        if (cmd == 0x05 && p0 == 0xD0) return "shootout finish speed";
        return "unknown";
    }

    private double ToDisplayUnit(double kph) => _settings.SpeedUnits == SpeedUnit.Mph ? kph / 1.609344 : kph;

    private void Refresh()
    {
        var s = _ctrl.State;

        // Auto-engage an armed brake once the dyno has slowed below 5 km/h.
        if (_brakePending && s.SpeedKph < BrakeEngageMaxKph)
        {
            _brakePending = false;
            _ctrl.Command.Mode = DynoMode.Brake;
            RaiseModeUi();
        }

        ActualSpeed = ToDisplayUnit(s.SpeedKph).ToString("F1");
        SetSpeed = Fmt(_ctrl.Command.RequestedSpeed);
        SpeedRevs = s.SpeedRevsPerSec.ToString("F3");
        RawMaster = s.RawSpeedMaster.ToString();
        CurrentRampRate = s.ShootoutActive
            ? $"{PcRampRateDisplay:F0} km/h/s — PC ({s.ShootoutMode})"
            : $"{_ctrl.Command.RampRate:F0} km/h/s";
        BrakeState = s.BrakeState;
        HandsfreeState = s.HandsfreeState;
        TxCount = _ctrl.TxPackets.ToString();
        RxCount = _ctrl.RxPackets.ToString();
        ErrCount = _ctrl.RxErrors.ToString();
        CommsLosses = _ctrl.CommsLossEvents.ToString();
        LastTx = _ctrl.LastTxHex;
        LastRx = _ctrl.LastRxHex;
        LinkAge = s.LastRxUtc == DateTime.MinValue ? "no data" : $"{(DateTime.UtcNow - s.LastRxUtc).TotalMilliseconds:F0} ms ago";

        BedActual = s.BedCurrentPosition > 0 ? $"{s.BedCurrentPosition:F3} m" : "—";
        BedTarget = s.BedTargetPosition  > 0 ? $"{s.BedTargetPosition:F3} m" : "—";
        TransmissionText = TransLabel(s.Transmission);
        In4wdText = s.ConfigReceived ? (s.In4wdMode ? "Yes" : "No") : "—";
        DeviceStatus = $"Pit {(s.PitControllerOnline ? "●" : "○")}  PC {(s.PcOnline ? "●" : "○")}  Bed {(s.BedCtrlOnline ? "●" : "○")}";

        // Honour a dyno-commanded speed-unit change (CMD 0x6F 0x60): the parser
        // updates Command.SpeedUnits; mirror it into settings so labels follow.
        if (_ctrl.Command.SpeedUnits != _settings.SpeedUnits)
        {
            _settings.SpeedUnits = _ctrl.Command.SpeedUnits;
            foreach (var n in new[]
            {
                nameof(SpeedUnitLabel), nameof(LiveUnitLabel), nameof(SpeedDecSmallLabel),
                nameof(SpeedIncSmallLabel), nameof(SpeedDecLargeLabel), nameof(SpeedIncLargeLabel)
            }) OnPropertyChanged(n);
        }

        if (LogEnabled)
        {
            var snap = _ctrl.LogSnapshot();
            int take = Math.Min(150, snap.Length);
            LogText = string.Join(Environment.NewLine, snap.Skip(snap.Length - take));
        }

        if (_settings.ShowRawRx)
        {
            var raw = _ctrl.RawSnapshot();
            int take = Math.Min(200, raw.Length);
            RawText = string.Join(Environment.NewLine, raw.Skip(raw.Length - take));
        }

        InspectorText = string.Join(Environment.NewLine, _ctrl.Stats
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                int id = (kv.Key >> 16) & 0xFF, cmd = (kv.Key >> 8) & 0xFF, p0 = kv.Key & 0xFF;
                var s = kv.Value;
                return $"ID={id:X2} CMD={cmd:X2} P0={p0:X2}  val={s.Last,6} [{s.Min}..{s.Max}]  x{s.Count,-6}  {LabelFor(id, cmd, p0)}";
            }));

        foreach (var n in new[]
        {
            nameof(ActualSpeed), nameof(SetSpeed), nameof(SpeedRevs), nameof(RawMaster), nameof(CurrentRampRate), nameof(BrakeState),
            nameof(HandsfreeState), nameof(TxCount), nameof(RxCount), nameof(ErrCount),
            nameof(CommsLosses),
            nameof(LastTx), nameof(LastRx), nameof(LinkAge), nameof(LogText), nameof(RawText), nameof(InspectorText), nameof(BrakeButtonText),
            nameof(ShowBrakeButton),
            nameof(IsShootoutActive), nameof(RampUnlocked), nameof(ShootoutBanner),
            nameof(BedActual), nameof(BedTarget), nameof(TransmissionText), nameof(In4wdText), nameof(DeviceStatus),
            nameof(ShowBalance), nameof(Show4WdFeatures), nameof(ShowBed)
        }) OnPropertyChanged(n);
    }

    private static string Fmt(double v) => v == Math.Floor(v) ? ((long)v).ToString() : v.ToString("0.##");

    public void Shutdown() => _ctrl.Disconnect();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
