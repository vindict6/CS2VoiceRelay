using System.Net;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace CS2VoiceRelay;

public class RelayConfig : BasePluginConfig
{
    [JsonPropertyName("BindAddress")] public string BindAddress { get; set; } = "0.0.0.0";
    [JsonPropertyName("Port")] public int Port { get; set; } = 27030;
    /// <summary>Host shown to players in the connect hint. Leave empty to show a placeholder.</summary>
    [JsonPropertyName("PublicAddress")] public string PublicAddress { get; set; } = "";
    /// <summary>Only accept a voice client from the same IP the game connection uses.</summary>
    [JsonPropertyName("RequireMatchingIp")] public bool RequireMatchingIp { get; set; } = false;
    [JsonPropertyName("ShowSpeakerHud")] public bool ShowSpeakerHud { get; set; } = true;
    [JsonPropertyName("SessionTimeoutSeconds")] public int SessionTimeoutSeconds { get; set; } = 10;
    [JsonPropertyName("MaxPacketsPerSecond")] public int MaxPacketsPerSecond { get; set; } = 80;
    /// <summary>Keep a player's token and voice session this long after they leave (covers map changes and quick rejoins).</summary>
    [JsonPropertyName("ForgetAfterSeconds")] public int ForgetAfterSeconds { get; set; } = 300;
    [JsonPropertyName("ChatPrefix")] public string ChatPrefix { get; set; } = " \u0004[Voice]\u0001";
}

/// <summary>
/// Voice that bypasses CS2's own playback. CS2's client stops playing server voice after a
/// workshop addon unloads (Valve issue #3980) and nothing a server sends can repair it, so
/// this plugin relays audio between external clients instead and uses the game only for
/// identity, team/alive state and the server's voice rules.
/// </summary>
public class CS2VoiceRelayPlugin : BasePlugin, IPluginConfig<RelayConfig>
{
    public override string ModuleName => "CS2VoiceRelay";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "vindict6";
    public override string ModuleDescription => "Out-of-game voice relay that survives workshop map changes";

    public RelayConfig Config { get; set; } = new();

    private UdpRelay? _relay;
    private Dictionary<ulong, PlayerInfo> _snapshot = new();
    private VoiceRules _rules = VoiceRules.Default;

    private ConVar? _cvVoiceEnable, _cvAllTalk, _cvDeadTalk, _cvFullAllTalk, _cvEnemyDead, _cvEnemyLiving, _cvSpecHear, _cvHostname;

    public void OnConfigParsed(RelayConfig config)
    {
        if (config.Port is < 1 or > 65535)
            throw new Exception($"Port {config.Port} is out of range");
        if (!IPAddress.TryParse(config.BindAddress, out _))
            throw new Exception($"BindAddress '{config.BindAddress}' is not an IP address");
        config.SessionTimeoutSeconds = Math.Max(3, config.SessionTimeoutSeconds);
        config.MaxPacketsPerSecond = Math.Max(10, config.MaxPacketsPerSecond);
        config.ForgetAfterSeconds = Math.Max(60, config.ForgetAfterSeconds);
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        var settings = new RelaySettings
        {
            RequireMatchingIp = Config.RequireMatchingIp,
            SessionTimeoutSeconds = Config.SessionTimeoutSeconds,
            MaxPacketsPerSecond = Config.MaxPacketsPerSecond,
            ForgetAfterSeconds = Config.ForgetAfterSeconds,
        };
        _relay = new UdpRelay(Config.BindAddress, Config.Port, settings, Logger);
        _relay.Start();

        AddTimer(0.5f, RefreshSnapshot, TimerFlags.REPEAT);
        AddTimer(0.5f, UpdateSpeakerHud, TimerFlags.REPEAT);
        AddTimer(1.0f, BroadcastState, TimerFlags.REPEAT);

        // OnClientAuthorized fires again after every map change, and OnClientDisconnect is reported
        // for everyone at map end, so neither is used to drop relay state. Tokens stay stable and
        // the relay forgets players only after ForgetAfterSeconds off the server.
        RegisterListener<Listeners.OnClientAuthorized>((slot, steamId) =>
        {
            var id = steamId.SteamId64;
            var token = _relay.GetOrIssueToken(id);
            // Chat isn't visible yet while the player is still loading in.
            AddTimer(8f, () =>
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (_relay == null || player is not { IsValid: true, IsBot: false } || player.SteamID != id)
                    return;
                // Always offer the token in the console, where the voice client picks it up automatically.
                PrintRelayInfo(player);
                if (!_relay.IsLinked(id))
                    SendConnectHint(player, token);
            });
        });

        if (hotReload)
        {
            foreach (var player in Utilities.GetPlayers())
            {
                if (player is not { IsValid: true, IsBot: false } || player.SteamID == 0)
                    continue;
                PrintRelayInfo(player);
                SendConnectHint(player, _relay.GetOrIssueToken(player.SteamID));
            }
        }
    }

    public override void Unload(bool hotReload)
    {
        _relay?.Dispose();
        _relay = null;
    }

    [ConsoleCommand("css_voice", "Show your voice relay token and link status")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnVoiceCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true } || _relay == null || player.SteamID == 0)
            return;

        var token = _relay.GetOrIssueToken(player.SteamID);
        SendConnectHint(player, token);
        PrintRelayInfo(player);
        var linked = _relay.IsLinked(player.SteamID);
        player.PrintToChat($"{Config.ChatPrefix} Status: {(linked ? "\u0004linked" : "\u0002not linked")}\u0001, {_relay.LinkedCount} player(s) on the relay.");
    }

    /// <summary>
    /// Quiet, console-only variant of !voice. The voice client runs this through CS2's
    /// -netconport console and reads the reply to fill in the server and token itself.
    /// </summary>
    [ConsoleCommand("css_voicerelay", "Print your voice relay token and status to the console (used by the voice client)")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnVoiceRelayInfoCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true } || _relay == null || player.SteamID == 0)
            return;
        PrintRelayInfo(player);
    }

    /// <summary>
    /// One machine-readable console line per player, parsed by client/voiceclient.py:
    ///   CS2VoiceRelay relay=HOST:PORT token=XXXXXXXX steamid=7656... status=linked|unlinked proto=1
    /// HOST is empty when PublicAddress isn't configured; the client then uses the game server's IP.
    /// </summary>
    private void PrintRelayInfo(CCSPlayerController player)
    {
        if (_relay == null)
            return;
        var token = _relay.GetOrIssueToken(player.SteamID);
        var status = _relay.IsLinked(player.SteamID) ? "linked" : "unlinked";
        player.PrintToConsole($"CS2VoiceRelay relay={Config.PublicAddress.Trim()}:{Config.Port} token={token} steamid={player.SteamID} status={status} proto=1");
    }

    private void SendConnectHint(CCSPlayerController player, string token)
    {
        var host = string.IsNullOrWhiteSpace(Config.PublicAddress) ? "<server-ip>" : Config.PublicAddress;
        player.PrintToChat($"{Config.ChatPrefix} This server uses CS2VoiceRelay for voice (CS2's own voice breaks on workshop maps).");
        player.PrintToChat($"{Config.ChatPrefix} Server \u0003{host}:{Config.Port}\u0001  Token \u0003{token}\u0001  (type !voice to see this again)");
    }

    // ---- game-thread sampling ----

    private static bool CvarBool(ref ConVar? cache, string name, bool fallback)
    {
        cache ??= ConVar.Find(name);
        return cache?.GetPrimitiveValue<bool>() ?? fallback;
    }

    private static int CvarInt(ref ConVar? cache, string name, int fallback)
    {
        cache ??= ConVar.Find(name);
        return cache?.GetPrimitiveValue<int>() ?? fallback;
    }

    private VoiceRules ReadRules() => new(
        VoiceEnabled: CvarBool(ref _cvVoiceEnable, "sv_voiceenable", true),
        AllTalk: CvarBool(ref _cvAllTalk, "sv_alltalk", false),
        DeadTalk: CvarBool(ref _cvDeadTalk, "sv_deadtalk", false),
        FullAllTalk: CvarBool(ref _cvFullAllTalk, "sv_full_alltalk", false),
        TalkEnemyDead: CvarBool(ref _cvEnemyDead, "sv_talk_enemy_dead", false),
        TalkEnemyLiving: CvarBool(ref _cvEnemyLiving, "sv_talk_enemy_living", false),
        SpecHear: CvarInt(ref _cvSpecHear, "sv_spec_hear", 3));

    /// <summary>Publish an immutable view of who is on the server, and the server's voice rules, to the relay thread.</summary>
    private void RefreshSnapshot()
    {
        if (_relay == null)
            return;

        var players = new Dictionary<ulong, PlayerInfo>();
        var addresses = new Dictionary<ulong, IPAddress>();
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is not { IsValid: true, IsBot: false } || p.SteamID == 0)
                continue;

            var flags = p.VoiceFlags;
            players[p.SteamID] = new PlayerInfo(
                p.SteamID, p.PlayerName, p.TeamNum, p.PawnIsAlive,
                Muted: flags.HasFlag(VoiceFlags.Muted),
                VoiceAll: flags.HasFlag(VoiceFlags.All),
                ListenAll: flags.HasFlag(VoiceFlags.ListenAll));

            // IpAddress is "a.b.c.d:port"
            var ip = p.IpAddress;
            if (ip != null)
            {
                var colon = ip.LastIndexOf(':');
                var host = colon > 0 ? ip[..colon] : ip;
                if (IPAddress.TryParse(host, out var addr))
                    addresses[p.SteamID] = addr;
            }
        }

        _snapshot = players;
        _rules = ReadRules();
        _relay.UpdateSnapshot(players, addresses, _rules);
    }

    /// <summary>Feed each linked client the roster, rules and who they can currently hear.</summary>
    private void BroadcastState()
    {
        if (_relay == null)
            return;
        var linked = _relay.LinkedSteamIds();
        if (linked.Count == 0)
            return;

        _cvHostname ??= ConVar.Find("hostname");
        var hostname = _cvHostname?.StringValue ?? "";
        var map = Server.MapName;
        var rules = _rules.ToWireFlags();
        var speaking = new HashSet<ulong>(_relay.CurrentSpeakers(TimeSpan.FromMilliseconds(700)));
        var linkedSet = new HashSet<ulong>(linked);

        foreach (var listenerId in linked)
        {
            var entries = new List<Protocol.StateEntry>(_snapshot.Count);
            foreach (var p in _snapshot.Values)
            {
                byte f = 0;
                if (p.Alive) f |= Protocol.FlagAlive;
                if (linkedSet.Contains(p.SteamId)) f |= Protocol.FlagLinked;
                if (speaking.Contains(p.SteamId)) f |= Protocol.FlagSpeaking;
                if (p.SteamId == listenerId) f |= Protocol.FlagSelf;
                else if (_relay.CanHear(p.SteamId, listenerId)) f |= Protocol.FlagCanHear;
                if (p.Muted) f |= Protocol.FlagMuted;
                entries.Add(new Protocol.StateEntry(p.SteamId, (byte)p.Team, f, p.Name));
            }
            _relay.SendToPlayer(listenerId, Protocol.BuildState(hostname, map, rules, entries));
        }
    }

    /// <summary>The real voice icon can't be driven, so show speakers as a HUD line.</summary>
    private void UpdateSpeakerHud()
    {
        if (_relay == null || !Config.ShowSpeakerHud)
            return;

        var speakers = _relay.CurrentSpeakers(TimeSpan.FromMilliseconds(700));
        if (speakers.Count == 0)
            return;

        foreach (var listener in Utilities.GetPlayers())
        {
            if (listener is not { IsValid: true, IsBot: false } || listener.SteamID == 0)
                continue;

            var names = new List<string>();
            foreach (var speakerId in speakers)
            {
                if (speakerId == listener.SteamID)
                    continue;
                if (_relay.CanHear(speakerId, listener.SteamID) && _snapshot.TryGetValue(speakerId, out var info))
                    names.Add(WebUtility.HtmlEncode(info.Name));
            }

            if (names.Count > 0)
                listener.PrintToCenterHtml($"<font color='#9fdc5c'>&#127908;</font> {string.Join(", ", names)}", 1);
        }
    }
}
