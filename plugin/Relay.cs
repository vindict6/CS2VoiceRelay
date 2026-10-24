using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CS2VoiceRelay;

/// <summary>What the relay needs to know about a connected game client. Built on the game thread.</summary>
public sealed record PlayerInfo(ulong SteamId, string Name, int Team, bool Alive, bool Muted, bool VoiceAll, bool ListenAll);

/// <summary>The server's own voice routing cvars, sampled on the game thread.</summary>
public sealed record VoiceRules(bool VoiceEnabled, bool AllTalk, bool DeadTalk, bool FullAllTalk, bool TalkEnemyDead, bool TalkEnemyLiving, int SpecHear)
{
    public static readonly VoiceRules Default = new(true, false, false, false, false, false, 3);

    public byte ToWireFlags()
    {
        byte f = 0;
        if (AllTalk) f |= Protocol.RuleAllTalk;
        if (DeadTalk) f |= Protocol.RuleDeadTalk;
        if (FullAllTalk) f |= Protocol.RuleFullAllTalk;
        if (TalkEnemyDead) f |= Protocol.RuleEnemyDead;
        if (TalkEnemyLiving) f |= Protocol.RuleEnemyLiving;
        if (!VoiceEnabled) f |= Protocol.RuleVoiceDisabled;
        return f;
    }
}

public sealed class RelaySettings
{
    public bool RequireMatchingIp;
    public int SessionTimeoutSeconds = 10;
    public int MaxPacketsPerSecond = 80;
    /// <summary>Forget a player's token and voice session once they've been off the server this long.</summary>
    public int ForgetAfterSeconds = 300;
}

/// <summary>
/// UDP relay. Runs on its own thread and never touches game objects: the plugin hands it an
/// immutable player snapshot plus the server's voice rules, and it routes audio with exactly
/// the same rules the game itself would apply.
/// </summary>
public sealed class UdpRelay : IDisposable
{
    private sealed class Session
    {
        public ulong SteamId;
        public IPEndPoint EndPoint = null!;
        public DateTime LastSeen;
        public DateTime WindowStart;
        public int WindowPackets;
    }

    private readonly UdpClient _udp;
    private readonly ILogger _log;
    private readonly RelaySettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<IPEndPoint, Session> _sessions = new();
    private readonly ConcurrentDictionary<ulong, string> _tokens = new();
    private readonly ConcurrentDictionary<ulong, DateTime> _lastAudio = new();
    private readonly ConcurrentDictionary<ulong, DateTime> _lastInGame = new();
    private volatile Dictionary<ulong, PlayerInfo> _players = new();
    private volatile Dictionary<ulong, IPAddress> _gameAddresses = new();
    private volatile VoiceRules _rules = VoiceRules.Default;
    private Task? _loop;

    public UdpRelay(string bindAddress, int port, RelaySettings settings, ILogger log)
    {
        _settings = settings;
        _log = log;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Parse(bindAddress), port));
    }

    public void Start()
    {
        _loop = Task.Run(ReceiveLoop);
        _log.LogInformation("Voice relay listening on {EndPoint}", _udp.Client.LocalEndPoint);
    }

    // ---- called from the game thread ----

    /// <summary>
    /// Publishes the game state. Players are forgotten only after being absent for
    /// ForgetAfterSeconds, never on a disconnect event: CounterStrikeSharp reports a
    /// disconnect for everyone at every map change, and the voice client must survive that.
    /// </summary>
    public void UpdateSnapshot(Dictionary<ulong, PlayerInfo> players, Dictionary<ulong, IPAddress> addresses, VoiceRules rules)
    {
        _players = players;
        _gameAddresses = addresses;
        _rules = rules;

        var now = DateTime.UtcNow;
        foreach (var id in players.Keys)
            _lastInGame[id] = now;

        var cutoff = now - TimeSpan.FromSeconds(_settings.ForgetAfterSeconds);
        foreach (var kv in _lastInGame)
        {
            if (kv.Value < cutoff && !players.ContainsKey(kv.Key))
            {
                _lastInGame.TryRemove(kv.Key, out _);
                Forget(kv.Key);
            }
        }
    }

    /// <summary>Returns the player's existing token, or issues one. Tokens are stable across map changes.</summary>
    public string GetOrIssueToken(ulong steamId)
    {
        _lastInGame.TryAdd(steamId, DateTime.UtcNow);
        return _tokens.GetOrAdd(steamId, _ =>
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var chars = new char[Protocol.TokenLength];
            for (var i = 0; i < chars.Length; i++)
                chars[i] = alphabet[Random.Shared.Next(alphabet.Length)];
            return new string(chars);
        });
    }

    public bool IsLinked(ulong steamId)
    {
        foreach (var s in _sessions.Values)
            if (s.SteamId == steamId)
                return true;
        return false;
    }

    public int LinkedCount => _sessions.Count;

    public List<ulong> LinkedSteamIds()
    {
        var list = new List<ulong>(_sessions.Count);
        foreach (var s in _sessions.Values)
            list.Add(s.SteamId);
        return list;
    }

    public void Forget(ulong steamId)
    {
        _tokens.TryRemove(steamId, out _);
        _lastAudio.TryRemove(steamId, out _);
        foreach (var kv in _sessions)
            if (kv.Value.SteamId == steamId)
                _sessions.TryRemove(kv.Key, out _);
    }

    public bool IsSpeaking(ulong steamId, TimeSpan window)
        => _lastAudio.TryGetValue(steamId, out var t) && t >= DateTime.UtcNow - window;

    /// <summary>SteamIDs that sent audio within the last <paramref name="window"/>.</summary>
    public List<ulong> CurrentSpeakers(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        var list = new List<ulong>();
        foreach (var kv in _lastAudio)
            if (kv.Value >= cutoff)
                list.Add(kv.Key);
        return list;
    }

    public bool CanHear(ulong speakerId, ulong listenerId)
    {
        var players = _players;
        if (!players.TryGetValue(speakerId, out var speaker) || !players.TryGetValue(listenerId, out var listener))
            return false;
        return CanHear(speaker, listener, _rules);
    }

    /// <summary>Send a server-built packet (e.g. STATE) to one linked player. Socket sends are thread-safe.</summary>
    public void SendToPlayer(ulong steamId, byte[] packet)
    {
        foreach (var s in _sessions.Values)
            if (s.SteamId == steamId)
                Send(s.EndPoint, packet);
    }

    // ---- routing rules ----

    /// <summary>
    /// Mirrors the game's own voice routing: sv_voiceenable, admin mutes (VoiceFlags.Muted),
    /// sv_full_alltalk, sv_spec_hear, sv_alltalk, sv_deadtalk, sv_talk_enemy_dead/living.
    /// Team 0/1 is unassigned/spectator.
    /// </summary>
    private static bool CanHear(PlayerInfo s, PlayerInfo l, VoiceRules r)
    {
        if (!r.VoiceEnabled || s.Muted)
            return false;
        if (r.FullAllTalk || s.VoiceAll || l.ListenAll)
            return true;

        var speakerSpec = s.Team <= 1;
        var listenerSpec = l.Team <= 1;
        if (listenerSpec)
        {
            // 0: only spectators; 1: all players; 2: spectated team (unknown here, treated as all); 3: self only; 4: nobody
            return r.SpecHear switch
            {
                0 => speakerSpec,
                1 or 2 => true,
                _ => false,
            };
        }
        if (speakerSpec)
            return false;

        if (r.AllTalk)
            return true;

        if (s.Team == l.Team)
            return s.Alive || !l.Alive || r.DeadTalk;

        if (s.Alive && l.Alive)
            return r.TalkEnemyLiving;
        if (!s.Alive && !l.Alive)
            return r.TalkEnemyDead;
        return false;
    }

    // ---- relay thread ----

    private async Task ReceiveLoop()
    {
        var lastSweep = DateTime.UtcNow;
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await _udp.ReceiveAsync(_cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; } // ICMP port-unreachable from a client that went away
            catch (ObjectDisposedException) { break; }

            try
            {
                Handle(r.RemoteEndPoint, r.Buffer);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Bad voice packet from {EndPoint}", r.RemoteEndPoint);
            }

            var now = DateTime.UtcNow;
            if ((now - lastSweep).TotalSeconds >= 2)
            {
                lastSweep = now;
                var cutoff = now - TimeSpan.FromSeconds(_settings.SessionTimeoutSeconds);
                foreach (var kv in _sessions)
                    if (kv.Value.LastSeen < cutoff)
                        _sessions.TryRemove(kv.Key, out _);
            }
        }
    }

    private void Handle(IPEndPoint from, byte[] data)
    {
        if (!Protocol.HasHeader(data, out var type))
            return;
        var body = data.AsSpan(Protocol.HeaderSize);
        var now = DateTime.UtcNow;

        if (type == Protocol.Hello)
        {
            if (body.Length < 8 + Protocol.TokenLength)
                return;
            var steamId = BinaryPrimitives.ReadUInt64LittleEndian(body);
            var token = Encoding.ASCII.GetString(body.Slice(8, Protocol.TokenLength));

            if (!_tokens.TryGetValue(steamId, out var expected) || !string.Equals(expected, token, StringComparison.Ordinal))
            {
                Send(from, Protocol.BuildWelcome(1, ""));
                return;
            }
            if (!_players.TryGetValue(steamId, out var info))
            {
                Send(from, Protocol.BuildWelcome(2, ""));
                return;
            }
            if (_settings.RequireMatchingIp
                && _gameAddresses.TryGetValue(steamId, out var gameAddr)
                && !gameAddr.Equals(from.Address))
            {
                _log.LogWarning("Voice client for {SteamId} came from {From} but the game is on {Game}", steamId, from.Address, gameAddr);
                Send(from, Protocol.BuildWelcome(1, ""));
                return;
            }

            // One session per player: drop any older endpoint for this SteamID.
            foreach (var kv in _sessions)
                if (kv.Value.SteamId == steamId && !kv.Key.Equals(from))
                    _sessions.TryRemove(kv.Key, out _);

            _sessions[from] = new Session { SteamId = steamId, EndPoint = from, LastSeen = now, WindowStart = now };
            Send(from, Protocol.BuildWelcome(0, info.Name));
            _log.LogInformation("Voice client linked: {Name} ({SteamId}) from {From}", info.Name, steamId, from);
            return;
        }

        if (!_sessions.TryGetValue(from, out var session))
            return;
        session.LastSeen = now;

        switch (type)
        {
            case Protocol.Ping:
                Send(from, Protocol.BuildPong());
                break;

            case Protocol.Bye:
                _sessions.TryRemove(from, out _);
                _lastAudio.TryRemove(session.SteamId, out _);
                break;

            case Protocol.Audio:
                if (body.Length < 4 + 2 || body.Length > 4 + Protocol.MaxAudioBytes)
                    return;
                if ((now - session.WindowStart).TotalSeconds >= 1)
                {
                    session.WindowStart = now;
                    session.WindowPackets = 0;
                }
                if (++session.WindowPackets > _settings.MaxPacketsPerSecond)
                    return;

                var players = _players;
                var rules = _rules;
                if (!players.TryGetValue(session.SteamId, out var speaker))
                    return;
                if (!rules.VoiceEnabled || speaker.Muted)
                    return; // silently drop: the game wouldn't forward this player either

                _lastAudio[session.SteamId] = now;
                var packet = Protocol.BuildSpeak(speaker.SteamId, speaker.Name, body);
                foreach (var kv in _sessions)
                {
                    var listener = kv.Value;
                    if (listener.SteamId == speaker.SteamId)
                        continue;
                    if (!players.TryGetValue(listener.SteamId, out var listenerInfo))
                        continue;
                    if (CanHear(speaker, listenerInfo, rules))
                        Send(listener.EndPoint, packet);
                }
                break;
        }
    }

    private void Send(IPEndPoint to, byte[] packet)
    {
        try { _udp.Send(packet, packet.Length, to); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
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
