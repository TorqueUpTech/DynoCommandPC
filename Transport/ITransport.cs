namespace DynoCommandPC.Transport;

/// <summary>A byte-stream link to the dyno — classic Bluetooth RFCOMM/SPP or a transparent TCP socket.</summary>
public interface ITransport : IDisposable
{
    bool IsOpen { get; }
    string Description { get; }
    /// <summary>Reason the last <see cref="Open"/> failed, or null. Shown in the connect status.</summary>
    string? LastError { get; }
    bool Open();
    void Close();
    void Write(byte[] data, int length);
    event Action<byte[]>? DataReceived;
}
