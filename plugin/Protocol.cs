using System.Buffers.Binary;
using System.Text;

namespace CS2VoiceRelay;

/// <summary>
/// Wire format shared with client/voiceclient.py. Every datagram starts with the
/// 4-byte magic and a 1-byte type; integers are little-endian, strings are UTF-8
/// prefixed with a u8 length.
///
///   client -> server
///     HELLO : steamid u64, token 8 ASCII bytes
///     AUDIO : seq u32, pcm int16[] (16 kHz mono, 20 ms = 320 samples)
///     PING  : (empty)
///     BYE   : (empty)
///   server -> client
///     WELCOME : status u8 (0 ok, 1 bad token, 2 not on server), name
///     SPEAK   : speaker steamid u64, name, seq u32, pcm int16[]
///     PONG    : (empty)
///     STATE   : hostname, map, rules u8, count u8, then per player:
///               steamid u64, team u8, flags u8, name
///               rules: 1 alltalk, 2 deadtalk, 4 full_alltalk, 8 enemy_dead, 16 enemy_living, 32 voice disabled
///               flags: 1 alive, 2 linked to relay, 4 speaking, 8 you can hear them, 16 this is you, 32 muted
/// </summary>
public static class Protocol
{
    public static ReadOnlySpan<byte> Magic => "CVR1"u8;
    public const int HeaderSize = 5;

    public const byte Hello = 1;
    public const byte Audio = 2;
    public const byte Ping = 3;
    public const byte Pong = 4;
    public const byte Bye = 5;
    public const byte Welcome = 6;
    public const byte Speak = 7;
    public const byte State = 8;

    public const int TokenLength = 8;
    public const int SampleRate = 16000;
    public const int FrameSamples = 320;
    public const int MaxAudioBytes = FrameSamples * 2 * 2; // allow up to 40 ms frames

    public const byte RuleAllTalk = 1, RuleDeadTalk = 2, RuleFullAllTalk = 4, RuleEnemyDead = 8, RuleEnemyLiving = 16, RuleVoiceDisabled = 32;
    public const byte FlagAlive = 1, FlagLinked = 2, FlagSpeaking = 4, FlagCanHear = 8, FlagSelf = 16, FlagMuted = 32;

    public readonly record struct StateEntry(ulong SteamId, byte Team, byte Flags, string Name);

    public static bool HasHeader(ReadOnlySpan<byte> d, out byte type)
    {
        type = 0;
        if (d.Length < HeaderSize || !d[..4].SequenceEqual(Magic))
            return false;
        type = d[4];
        return true;
    }

    private static MemoryStream Begin(byte type)
    {
        var ms = new MemoryStream(64);
        ms.Write(Magic);
        ms.WriteByte(type);
        return ms;
    }

    private static void WriteString(MemoryStream ms, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length > 255)
            bytes = bytes[..255];
        ms.WriteByte((byte)bytes.Length);
        ms.Write(bytes);
    }

    private static void WriteU64(MemoryStream ms, ulong v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        ms.Write(b);
    }

    public static byte[] BuildWelcome(byte status, string name)
    {
        var ms = Begin(Welcome);
        ms.WriteByte(status);
        WriteString(ms, name);
        return ms.ToArray();
    }

    public static byte[] BuildPong()
    {
        return Begin(Pong).ToArray();
    }

    public static byte[] BuildSpeak(ulong steamId, string name, ReadOnlySpan<byte> seqAndPcm)
    {
        var ms = Begin(Speak);
        WriteU64(ms, steamId);
        WriteString(ms, name);
        ms.Write(seqAndPcm);
        return ms.ToArray();
    }

    public static byte[] BuildState(string hostname, string map, byte rules, IReadOnlyList<StateEntry> entries)
    {
        var ms = Begin(State);
        WriteString(ms, hostname);
        WriteString(ms, map);
        ms.WriteByte(rules);
        var count = Math.Min(entries.Count, 255);
        ms.WriteByte((byte)count);
        for (var i = 0; i < count; i++)
        {
            var e = entries[i];
            WriteU64(ms, e.SteamId);
            ms.WriteByte(e.Team);
            ms.WriteByte(e.Flags);
            WriteString(ms, e.Name);
        }
        return ms.ToArray();
    }
}
