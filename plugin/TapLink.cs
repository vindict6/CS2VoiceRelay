using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace CS2VoiceRelay;

/// <summary>One svc_VoiceData the engine sent to a player, as forwarded by the native tap.</summary>
public readonly record struct TapVoicePacket(int RecipientSlot, ulong SpeakerXuid, byte[] Frame);

/// <summary>
/// Localhost UDP link to the CS2VoiceRelayTap Metamod plugin.
///
///   tap -> plugin  "CVT1" voice packet, "CVH1" heartbeat
///   plugin -> tap  "CVC1" u64 suppress mask (bit = player slot), u32 valid_ms
///
/// Runs on its own thread; never touches game objects.
/// </summary>
public sealed class TapLink : IDisposable
{
    private readonly UdpClient _udp;
    private readonly ILogger _log;
    private readonly Action<TapVoicePacket> _onVoice;
    private readonly CancellationTokenSource _cts = new();
    private volatile IPEndPoint? _tap;
    private DateTime _lastHeard = DateTime.MinValue;
    private Task? _loop;
    private bool _loggedFirst;

    public TapLink(int port, Action<TapVoicePacket> onVoice, ILogger log)
    {
        _onVoice = onVoice;
        _log = log;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
    }

    /// <summary>True when the native tap has been heard from in the last few seconds.</summary>
    public bool TapAlive => DateTime.UtcNow - _lastHeard < TimeSpan.FromSeconds(5);

    public void Start()
    {
        _loop = Task.Run(ReceiveLoop);
        _log.LogInformation("Waiting for CS2VoiceRelayTap on {EndPoint}", _udp.Client.LocalEndPoint);
    }

    /// <summary>Tell the tap which recipients are listening in a browser (their in-game copy is held back).</summary>
    public void SendSuppressMask(ulong mask, uint validMs)
    {
        var tap = _tap;
        if (tap == null)
            return;
        var buf = new byte[16];
        "CVC1"u8.CopyTo(buf);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(4), mask);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), validMs);
        try { _udp.Send(buf, buf.Length, tap); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await _udp.ReceiveAsync(_cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            if (!IPAddress.IsLoopback(r.RemoteEndPoint.Address) || r.Buffer.Length < 4)
                continue;

            _tap = r.RemoteEndPoint;
            _lastHeard = DateTime.UtcNow;

            try
            {
                if (r.Buffer.AsSpan(0, 4).SequenceEqual("CVT1"u8) && TryParse(r.Buffer, out var packet))
                {
                    if (!_loggedFirst)
                    {
                        _loggedFirst = true;
                        _log.LogInformation("Receiving voice from CS2VoiceRelayTap");
                    }
                    _onVoice(packet);
                }
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Bad packet from CS2VoiceRelayTap");
            }
        }
    }

    /// <summary>
    /// "CVT1" u8 recipient_slot, u64 speaker_xuid, i32 speaker_entity, i32 speaker_client,
    /// u8 format, u32 sample_rate, u32 section_number, i32 sequence_bytes,
    /// u16 offset_count, u32 offsets[], u16 data_len, data.
    ///
    /// Re-encoded for the browser as: u8 1, u64 speaker_xuid, u8 format, u32 sample_rate,
    /// u16 offset_count, u32 offsets[], data.
    /// </summary>
    private static bool TryParse(byte[] b, out TapVoicePacket packet)
    {
        packet = default;
        var s = b.AsSpan();
        const int fixedLen = 4 + 1 + 8 + 4 + 4 + 1 + 4 + 4 + 4 + 2;
        if (s.Length < fixedLen + 2)
            return false;

        var recipient = s[4];
        var xuid = BinaryPrimitives.ReadUInt64LittleEndian(s[5..]);
        var format = s[21];
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(s[22..]);
        var offsetCount = BinaryPrimitives.ReadUInt16LittleEndian(s[34..]);
        var pos = fixedLen;
        if (offsetCount > 64 || s.Length < pos + offsetCount * 4 + 2)
            return false;
        var offsets = s.Slice(pos, offsetCount * 4);
        pos += offsetCount * 4;
        var dataLen = BinaryPrimitives.ReadUInt16LittleEndian(s[pos..]);
        pos += 2;
        if (dataLen == 0 || s.Length < pos + dataLen)
            return false;
        var data = s.Slice(pos, dataLen);

        var frame = new byte[1 + 8 + 1 + 4 + 2 + offsets.Length + data.Length];
        var f = frame.AsSpan();
        f[0] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(f[1..], xuid);
        f[9] = format;
        BinaryPrimitives.WriteUInt32LittleEndian(f[10..], sampleRate);
        BinaryPrimitives.WriteUInt16LittleEndian(f[14..], offsetCount);
        offsets.CopyTo(f[16..]);
        data.CopyTo(f[(16 + offsets.Length)..]);

        packet = new TapVoicePacket(recipient, xuid, frame);
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Close();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _udp.Dispose();
        _cts.Dispose();
    }
}
