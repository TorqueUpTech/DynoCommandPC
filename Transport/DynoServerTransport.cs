using System.Net;
using System.Text;
using DynoCommandPC.Protocol;
using DynoDynamics.Connection;
using DynoDynamics.DynoServer;
using DynoDynamics.IO;

namespace DynoCommandPC.Transport;

/// <summary>
/// Speaks the DynoServer PC-side network as the handheld, instead of Bluetooth to the comms box.
/// Registers as a producer (reusing the proven <c>DynoDotNet.dll</c> — the same library the feeder
/// uses; it loads fine under .NET 8) and translates between the app's 0xA0 CanPacket stream and
/// DynoServer <c>CANDataMessage</c>s. The app is unchanged: <see cref="DynoController"/> still wraps/
/// parses 0xA0 frames; this transport just recovers the packet, re-sends it as a network message, and
/// re-wraps inbound ones. Handheld packets already carry SourceId = HANDHELD (id nibble 0x4), which is
/// what DYNO_Main routes on — the same path the real handheld takes via DynoClientUSB.
///
/// PROOF OF CONCEPT: confirms a network-registered HANDHELD producer can drive the dyno. Selected by
/// entering "ds" (loopback) or "ds:&lt;ip&gt;" in the EDM-address field.
/// </summary>
public sealed class DynoServerTransport : ITransport
{
    // Registration type. Routing to a device keys on the per-packet SourceId (HANDHELD), NOT this —
    // but DYNO_Main's consumer only ingests producer types it subscribes to, and 'PC' (its own kind)
    // is ignored. WeatherStation is the type the working comms/EDM simulator uses, so we match it:
    // the producer is just a carrier; the packets inside are sourced from HANDHELD.
    private const eProducerType ProducerKind = eProducerType.WeatherStation;
    private const byte CmdDeviceStatus = 37; // eNewCmd.DEVICE_STATUS — what DYNO_Main's presence watchdog keys on

    private readonly IPAddress _server;
    private readonly int _startTick = Environment.TickCount; // for a live packet timestamp
    private readonly FrameParser _wrapParser = new(); // recovers CanPackets from the app's 0xA0 writes
    private readonly List<ConsumerConnection> _consumers = new();
    private ProducerConnection? _conn;
    private Thread? _thread;
    private System.Threading.Timer? _presence;
    private volatile bool _registered;
    private volatile bool _stopping;

    public event Action<byte[]>? DataReceived;
    public bool IsOpen => _registered;
    public string Description => $"DynoServer {_server}";
    public string? LastError { get; private set; }

    public DynoServerTransport(IPAddress server)
    {
        _server = server;
        _wrapParser.PacketReceived += SendToServer;
    }

    public bool Open()
    {
        try
        {
            // Register as the comms box ("COMMS") — the SAME identity the working comms emulator uses.
            // DYNO_Main dynamically subscribes to a "COMMS" producer (so it can connect/disconnect/re-
            // register anytime), but ignores an unknown "HANDHELD" producer unless it happened to be up
            // when DYNO_Main launched. We're the comms box carrying the handheld's packets (each still
            // SourceId=HANDHELD), exactly like the real comms box relays a connected handheld.
            _conn = new ProducerConnection("COMMS", ProducerKind, eDynoGeneration.Digital);
            _conn.onRegister = (st, _) => _registered = st == IOStatus.Succeeded;
            _conn.onRecv = (msg, _) => OnServerMessage(msg);
            _conn.onError = (ex, _) => LastError = ex.Message;

            // ConnectAndRunWithRetries blocks (discover + connect + register + run), so it lives on
            // its own thread; onRegister flips _registered once we're in.
            _thread = new Thread(() => _conn.ConnectAndRunWithRetries(_server, this))
            {
                IsBackground = true, Name = "DynoSrv-TX"
            };
            _thread.Start();

            for (int i = 0; i < 60 && !_registered; i++) Thread.Sleep(50); // ~3 s; loopback registers fast
            if (!_registered) { LastError ??= "DynoServer registration timed out"; return false; }

            // Presence: announce the handheld to DYNO_Main's device watchdog with a DEVICE_STATUS from
            // HANDHELD every second — the same mechanism the comms/EDM simulator uses to "go active".
            // Without this the command packets alone don't mark the Command Module connected.
            _presence = new System.Threading.Timer(_ => SendPresence(), null, 200, 1000);

            // RX: one consumer that enumerates the server's producers (by real 11-byte ID) and
            // subscribes to all of them as a Listener; the parser ignores anything it doesn't know.
            new Thread(ConsumerLoop) { IsBackground = true, Name = "DynoSrv-RX" }.Start();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    private void ConsumerLoop()
    {
        while (!_stopping)
        {
            try
            {
                // The register match is on the exact 11-byte producer ID, not the name, so enumerate
                // the live producer list first, then subscribe (Listener) to every one of them.
                var ids = EnumerateProducerIds();
                if (ids.Count > 0)
                {
                    var subs = ids.Select(id => new ConsumerConnection.ProducerConnect
                    {
                        ID = id,
                        ConnectMode = eConnectMode.Listener
                    }).ToArray();

                    var c = new ConsumerConnection("HANDHELD");
                    c.onRecv = (msg, _) => OnServerMessage(msg);
                    lock (_consumers) _consumers.Add(c);
                    c.ConnectAndRun(_server, subs, this); // blocks while subscribed
                    lock (_consumers) _consumers.Remove(c);
                }
            }
            catch { /* transient — retry below */ }
            if (!_stopping) Thread.Sleep(2000);
        }
    }

    /// <summary>Discover + connect + ask the server for its producer list, returning each producer's
    /// exact ID bytes (what a consumer register must match on).</summary>
    private List<byte[]> EnumerateProducerIds()
    {
        var list = new List<byte[]>();
        var c = new ConsumerConnection("HANDHELD-enum");
        try
        {
            if (c.Discover(_server, out _, out var reply) != IOStatus.Succeeded || reply == null) return list;
            if (c.Connect(reply.remoteEndPoint.Address, reply.message.PortNumber) != IOStatus.Succeeded) return list;
            c.SendMessageDirect(new ProducerListRequestMessage(), 1000, null);
            c.WaitForMessages((msg, _) =>
            {
                if (msg is ProducerListReplyMessage pl)
                {
                    foreach (var p in pl.GetProducers())
                        if (p.ID != null) list.Add(p.ID);
                    return true;
                }
                return false;
            }, 2000, null);
        }
        catch { /* best effort */ }
        finally { try { c.Stop(); } catch { } }
        return list;
    }

    /// <summary>App → dyno: the controller hands us 0xA0-wrapped frames; feed the de-framer.</summary>
    public void Write(byte[] data, int length) => _wrapParser.Feed(data, length);

    /// <summary>Live 16-bit timestamp. DYNO_Main's device watchdog ignores stale/zero-timestamp
    /// packets — the real handheld echoes the comms-box time, and the comms emulator stamps ms; we
    /// do the same so our packets actually register.</summary>
    private ushort NextTs() => (ushort)((Environment.TickCount - _startTick) & 0xFFFF);

    private void SendToServer(CanPacket cp)
    {
        // ToBytesNoCrc() = [id, cmd, p0..p3, tsl, tsh] — exactly a DynoCAN_Packet's 8 payload bytes.
        try
        {
            var p = new DynoCAN_Packet(cp.ToBytesNoCrc(), 0, 8);
            p.Timestamp = NextTs(); // override the app's (0) timestamp with a live one
            _conn?.Send(p);
        }
        catch { /* link dropping */ }
    }

    private void SendPresence()
    {
        if (!_registered) return;
        try
        {
            var p = new DynoCAN_Packet { SourceId = eDeviceId.HANDHELD, DestId = eDeviceId.PC, cmd = CmdDeviceStatus };
            p.Param = 1;            // "present / OK" (matches CommsEmulator)
            p.ValueUnsigned = 0;
            p.Timestamp = NextTs();
            _conn?.Send(p);
        }
        catch { /* link dropping */ }
    }

    /// <summary>Dyno → app: re-wrap each inbound CAN data packet into the 0xA0 stream the app parses.</summary>
    private void OnServerMessage(ServerMessage msg)
    {
        if (msg is not CANDataMessage cdm) return;
        try
        {
            var b8 = new byte[8];
            cdm.Packet.CopyToArray(b8, 0);
            DataReceived?.Invoke(PacketFramer.Wrap(CanPacket.FromBytes(b8)));
        }
        catch { /* malformed — skip */ }
    }

    public void Close()
    {
        _stopping = true;
        _registered = false;
        try { _presence?.Dispose(); } catch { /* best effort */ }
        _presence = null;
        try { _conn?.Stop(); } catch { /* best effort */ }
        _conn = null;
        lock (_consumers)
        {
            foreach (var c in _consumers) { try { c.Stop(); } catch { /* best effort */ } }
            _consumers.Clear();
        }
    }

    public void Dispose() => Close();
}
