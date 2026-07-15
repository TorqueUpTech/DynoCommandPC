using System.Net;
using System.Net.Sockets;

namespace DynoCommandPC.Transport;

/// <summary>
/// Transparent TCP link to a dyno/handheld simulator. Carries the IDENTICAL 0xA0-framed CanPacket
/// stream as <see cref="BluetoothRfcommTransport"/> — it's just a socket instead of RFCOMM/SPP — so
/// the same <c>PacketFramer</c>/<c>FrameParser</c> handle it unchanged. Selected when the EDM-address
/// field holds an <c>IP:port</c> (or <c>localhost:port</c>) instead of a Bluetooth MAC, letting the
/// app run against a same-PC TCP simulator with no Bluetooth adapter.
/// </summary>
public sealed class TcpTransport : ITransport
{
    private const int ConnectTimeoutMs = 4000;

    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Thread? _reader;
    private volatile bool _running;

    public event Action<byte[]>? DataReceived;
    public bool IsOpen { get; private set; }
    public string Description => $"TCP {_host}:{_port}";

    /// <summary>Set when Open() fails, e.g. "connect timed out (127.0.0.1:51730)".</summary>
    public string? LastError { get; private set; }

    public TcpTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public bool Open()
    {
        try
        {
            LastError = null;
            _client = new TcpClient();
            if (!_client.ConnectAsync(_host, _port).Wait(ConnectTimeoutMs))
            {
                LastError = $"connect timed out ({_host}:{_port})";
                Close();
                return false;
            }
            _client.NoDelay = true;
            _stream = _client.GetStream();

            _running = true;
            IsOpen = true;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "TCP-reader" };
            _reader.Start();
            return true;
        }
        catch (Exception ex)
        {
            // ConnectAsync surfaces the real cause (refused/unreachable) as an AggregateException.
            LastError = (ex.InnerException ?? ex).Message;
            Close();
            return false;
        }
    }

    private void ReadLoop()
    {
        var buf = new byte[512];
        try
        {
            while (_running)
            {
                int n = _stream!.Read(buf, 0, buf.Length);
                if (n <= 0) break; // peer closed
                var d = new byte[n];
                Array.Copy(buf, d, n);
                DataReceived?.Invoke(d);
            }
        }
        catch { /* socket closed/errored — fall through to mark closed */ }
        IsOpen = false;
    }

    public void Write(byte[] data, int length)
    {
        try { _stream?.Write(data, 0, length); }
        catch { /* drop on a broken link; the reader loop will mark it closed */ }
    }

    public void Close()
    {
        _running = false;
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        try { _reader?.Join(300); } catch { }
        IsOpen = false;
    }

    public void Dispose() => Close();

    /// <summary>
    /// True (with host/port) if <paramref name="s"/> is a TCP endpoint — an <c>IP:port</c> or
    /// <c>localhost:port</c> — rather than a Bluetooth MAC. Requires a numeric IP or "localhost" for
    /// the host so a colon-separated MAC (e.g. 00:12:F3:04:96:2C) is never mistaken for host:port.
    /// </summary>
    public static bool TryParseEndpoint(string? s, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;

        int i = s!.LastIndexOf(':');
        if (i <= 0 || i == s.Length - 1) return false;
        if (!int.TryParse(s[(i + 1)..], out port) || port < 1 || port > 65535) return false;

        host = s[..i].Trim();
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || IPAddress.TryParse(host, out _);
    }
}
