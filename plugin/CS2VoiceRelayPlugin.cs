using System.Net;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace CS2VoiceRelay;

public class RelayConfig : BasePluginConfig
{
    /// <summary>Interface and TCP port of the browser player.</summary>
    [JsonPropertyName("BindAddress")] public string BindAddress { get; set; } = "0.0.0.0";
    [JsonPropertyName("Port")] public int Port { get; set; } = 27030;
    /// <summary>Link shown to players, e.g. "http://example.com:27030". Empty: http://SERVER-IP:Port.</summary>
    [JsonPropertyName("PublicUrl")] public string PublicUrl { get; set; } = "";
    /// <summary>
    /// PEM certificate (full chain) and private key for HTTPS. When both are set, Port speaks HTTP
    /// and HTTPS at once, and plain-HTTP visitors are sent to HTTPS. This is what makes the page open
    /// in browsers that remember HSTS for your domain: they only ever try https:// on this port.
    /// The files are re-read when they change, so certificate renewals are picked up.
    /// </summary>
    [JsonPropertyName("TlsCertificate")] public string TlsCertificate { get; set; } = "";
    [JsonPropertyName("TlsPrivateKey")] public string TlsPrivateKey { get; set; } = "";

    /// <summary>Heading, description and footer of the page. URLs in the description and footer become links.</summary>
    [JsonPropertyName("PageTitle")] public string PageTitle { get; set; } = "CS2VoiceRelay";
    [JsonPropertyName("PageDescription")] public string PageDescription { get; set; } = "Hear in-game voice here when CS2 stops playing it. Keep talking in game as normal.";
    [JsonPropertyName("PageFooter")] public string PageFooter { get; set; } = "Keep this tab open while you play. While it's listening, you'll still see who's talking in CS2, but the audio only plays here, so you never hear it twice.";
    /// <summary>Show the live scoreboard (CompStats layout) on the page, to everyone who opens it.</summary>
    [JsonPropertyName("ShowScoreboard")] public bool ShowScoreboard { get; set; } = true;

    /// <summary>
    /// Tournament mode: browser listeners who are spectators on the server, or whose SteamID64 is
    /// in TournamentListeners, hear every player's voice, with buttons to solo one team. Players
    /// on a team still hear only what the game lets them hear.
    /// </summary>
    [JsonPropertyName("TournamentMode")] public bool TournamentMode { get; set; } = false;
    [JsonPropertyName("TournamentListeners")] public List<string> TournamentListeners { get; set; } = new();

    /// <summary>Localhost UDP port the CS2VoiceRelayTap Metamod plugin forwards voice to.</summary>
    [JsonPropertyName("TapPort")] public int TapPort { get; set; } = 27031;
    /// <summary>Hold back in-game voice for players who are listening in a browser, so they don't hear it twice.</summary>
    [JsonPropertyName("SuppressGameVoiceForListeners")] public bool SuppressGameVoiceForListeners { get; set; } = true;
    /// <summary>
    /// For players listening in a browser, send their game silent voice instead of none, so CS2
    /// still shows who's talking (speaking icons) while the audio plays only in the browser.
    /// </summary>
    [JsonPropertyName("ShowInGameSpeakingIndicators")] public bool ShowInGameSpeakingIndicators { get; set; } = true;
    /// <summary>
    /// Reverse proxies (IPs or CIDR ranges) whose X-Forwarded-For identifies the browser, e.g.
    /// ["127.0.0.1", "172.16.0.0/12"] for a proxy on this machine or in Docker. Empty: no proxy.
    /// </summary>
    [JsonPropertyName("TrustedProxies")] public List<string> TrustedProxies { get; set; } = new();
    [JsonPropertyName("MaxConnectionsPerIp")] public int MaxConnectionsPerIp { get; set; } = 6;
    /// <summary>Put in front of the plugin's chat messages. Color tags work here too.</summary>
    [JsonPropertyName("ChatPrefix")] public string ChatPrefix { get; set; } = " {green}[Voice]{default}";

    /// <summary>Show JoinMessage to players when they join. false (or an empty JoinMessage) turns it off.</summary>
    [JsonPropertyName("AnnounceOnJoin")] public bool AnnounceOnJoin { get; set; } = true;
    /// <summary>
    /// Chat message about the voice page. {url} is the PublicUrl link, {name} the player's name;
    /// color tags like {green}, {gold}, {lightblue} or {default} use CounterStrikeSharp's chat colors.
    /// </summary>
    [JsonPropertyName("JoinMessage")] public string JoinMessage { get; set; } = "Can't hear anyone after a map change? Open {lightblue}{url}{default} in your browser.";
    /// <summary>Seconds after a player joins before JoinMessage appears.</summary>
    [JsonPropertyName("JoinMessageDelaySeconds")] public int JoinMessageDelaySeconds { get; set; } = 20;
    /// <summary>Minimum minutes before the same player sees JoinMessage again on joining. 0: on every join and map change.</summary>
    [JsonPropertyName("JoinMessageIntervalMinutes")] public int JoinMessageIntervalMinutes { get; set; } = 180;
    /// <summary>
    /// After that, repeat JoinMessage every N minutes (one server-wide timer) to players who aren't
    /// listening in a browser. Anyone who saw it in the last half interval is skipped. 0: off.
    /// </summary>
    [JsonPropertyName("ReminderIntervalMinutes")] public int ReminderIntervalMinutes { get; set; } = 0;
}

/// <summary>
/// Browser playback for in-game voice. CS2's client stops playing server voice after a
/// workshop addon unloads (Valve issue #3980) and only a game restart brings it back.
/// Players keep talking in game as normal; the CS2VoiceRelayTap Metamod plugin copies the
/// voice the engine sends each player, and this plugin streams it to that player's browser.
/// Routing is the engine's own, so everyone hears exactly what the game would let them hear.
/// </summary>
public class CS2VoiceRelayPlugin : BasePlugin, IPluginConfig<RelayConfig>
{
    public override string ModuleName => "CS2VoiceRelay";
    public override string ModuleVersion => "2.3.1";
    public override string ModuleAuthor => "vindict6";
    public override string ModuleDescription => "Hear in-game voice in your browser when CS2's voice playback breaks";

    public RelayConfig Config { get; set; } = new();

    private WebServer? _web;
    private TapLink? _tap;
    private PrefsStore? _prefs;
    private volatile ServerSnapshot _snapshot = ServerSnapshot.Empty;
    private readonly Dictionary<ulong, DateTime> _announced = new();
    private readonly ScoreTracker _scores = new();
    private ConVar? _cvHostname, _cvIp;
    private bool _warnedTap;
    private Thread? _worker;
    private CancellationTokenSource? _workerStop;

    public void OnConfigParsed(RelayConfig config)
    {
        if (config.Port is < 1 or > 65535)
            throw new Exception($"Port {config.Port} is out of range");
        if (config.TapPort is < 1 or > 65535)
            throw new Exception($"TapPort {config.TapPort} is out of range");
        if (!IPAddress.TryParse(config.BindAddress, out _))
            throw new Exception($"BindAddress '{config.BindAddress}' is not an IP address");
        config.MaxConnectionsPerIp = Math.Clamp(config.MaxConnectionsPerIp, 1, 64);
        config.JoinMessageIntervalMinutes = Math.Max(0, config.JoinMessageIntervalMinutes);
        config.JoinMessageDelaySeconds = Math.Clamp(config.JoinMessageDelaySeconds, 1, 600);
        if (string.IsNullOrWhiteSpace(config.TlsCertificate) != string.IsNullOrWhiteSpace(config.TlsPrivateKey))
            throw new Exception("Set both TlsCertificate and TlsPrivateKey, or neither");
        config.ReminderIntervalMinutes = Math.Max(0, config.ReminderIntervalMinutes);
        foreach (var entry in config.TournamentListeners)
            if (!ulong.TryParse(entry.Trim(), out var id) || id < 76561197960265728UL)
                throw new Exception($"TournamentListeners entry '{entry}' is not a SteamID64");
        foreach (var entry in config.TrustedProxies)
            if (!TryParseNetwork(entry, out _))
                throw new Exception($"TrustedProxies entry '{entry}' is not an IP address or CIDR range");
        Config = config;
        AddConfigNotes();
    }

    private const string ConfigNotesMarker = "// ---- CS2VoiceRelay notes";

    /// <summary>
    /// Adds a comment block to the top of CS2VoiceRelay.json listing the chat colors and message
    /// placeholders. CounterStrikeSharp skips comments when it reads the file, and the block is only
    /// added when it isn't there, so settings are never touched.
    /// </summary>
    private void AddConfigNotes()
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", "CS2VoiceRelay", "CS2VoiceRelay.json"));
            if (!File.Exists(path))
                return;
            var text = File.ReadAllText(path);
            if (text.Contains(ConfigNotesMarker, StringComparison.Ordinal))
                return;

            var colors = typeof(ChatColors).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.FieldType == typeof(char) && !f.IsDefined(typeof(ObsoleteAttribute), false))
                .Select(f => "{" + f.Name.ToLowerInvariant() + "}")
                .ToList();
            var lines = new List<string>
            {
                ConfigNotesMarker + " (comments are ignored when the config is read) ----",
                "// Chat colors for JoinMessage and ChatPrefix. Put the tag in front of the text to color:",
            };
            for (var i = 0; i < colors.Count; i += 7)
                lines.Add("//   " + string.Join(" ", colors.Skip(i).Take(7)));
            lines.AddRange(new[]
            {
                "// JoinMessage placeholders: {url} = PublicUrl (or http://SERVER-IP:Port), {name} = the player's name.",
                "//   \"JoinMessage\": \"Can't hear anyone? Open {lightblue}{url}{default} in your browser.\"",
                "//   \"ChatPrefix\": \" {green}[Voice]{default}\"   (start with a space so the first color shows)",
                "// JoinMessageDelaySeconds: wait after a player joins. ReminderIntervalMinutes: repeat to everyone not listening (0 = off).",
                "// PageTitle, PageDescription, PageFooter: the web page's text. Plain text; links (https://...) become clickable.",
                "// TlsCertificate, TlsPrivateKey: PEM files (e.g. Let's Encrypt fullchain.pem, privkey.pem) to serve HTTPS on Port too.",
                "// TournamentMode: spectators listening in a browser (and SteamID64s in TournamentListeners) hear every player,",
                "//   with buttons to solo CT or T. Players on a team still only hear their own team.",
                "// ----",
            });

            // Keep CounterStrikeSharp's own "generated by" comment line first
            var insertAt = 0;
            while (insertAt < text.Length && text.AsSpan(insertAt).TrimStart(" \t").StartsWith("//"))
            {
                var eol = text.IndexOf('\n', insertAt);
                insertAt = eol < 0 ? text.Length : eol + 1;
            }
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var block = string.Join(newline, lines) + newline;
            File.WriteAllText(path, text.Insert(insertAt, block));
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "Couldn't add notes to the config file");
        }
    }

    private static bool TryParseNetwork(string text, out IPNetwork network)
    {
        text = text.Trim();
        if (IPAddress.TryParse(text, out var single))
        {
            network = new IPNetwork(single, single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }
        return IPNetwork.TryParse(text, out network);
    }

    public override void Load(bool hotReload)
    {
        // Next to CS2VoiceRelay.json: addons/counterstrikesharp/configs/plugins/CS2VoiceRelay/
        var prefsPath = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", "CS2VoiceRelay", "voice_prefs.json"));
        _prefs = new PrefsStore(prefsPath, Logger);
        _prefs.Load();

        _web = new WebServer(
            new WebSettings
            {
                BindAddress = Config.BindAddress,
                Port = Config.Port,
                TrustedProxies = Config.TrustedProxies.Select(t => { TryParseNetwork(t, out var n); return n; }).ToList(),
                MaxConnectionsPerIp = Config.MaxConnectionsPerIp,
                TlsCertificate = Config.TlsCertificate.Trim(),
                TlsPrivateKey = Config.TlsPrivateKey.Trim(),
                PageTitle = Config.PageTitle,
                PageDescription = Config.PageDescription,
                PageFooter = Config.PageFooter,
                ShowScoreboard = Config.ShowScoreboard,
                TournamentMode = Config.TournamentMode,
                TournamentListeners = Config.TournamentListeners.Select(e => ulong.Parse(e.Trim())).ToHashSet(),
            },
            () => _snapshot,
            SendCodeToPlayer,
            _prefs,
            ModuleVersion,
            Logger);
        _tap = new TapLink(Config.TapPort, OnTapVoice, Logger);
        _web.Start();
        _tap.Start();

        // The game thread only reads the game (twice a second, into an immutable snapshot) and
        // prints chat. Voice runs on the tap's receive thread, the site on the web server's
        // threads, and everything periodic on this plugin's own worker thread.
        AddTimer(0.5f, RefreshSnapshot, TimerFlags.REPEAT);
        _workerStop = new CancellationTokenSource();
        _worker = new Thread(() => WorkerLoop(_workerStop.Token)) { IsBackground = true, Name = "CS2VoiceRelay worker" };
        _worker.Start();
        if (Config.ReminderIntervalMinutes > 0)
            AddTimer(Config.ReminderIntervalMinutes * 60f, SendReminders, TimerFlags.REPEAT);

        RegisterListener<Listeners.OnMapStart>(_ => _scores.Reset());
        RegisterEventHandler<EventPlayerDeath>((e, _) =>
        {
            _scores.OnPlayerDeath(e);
            return HookResult.Continue;
        });
        RegisterEventHandler<EventRoundAnnounceMatchStart>((_, _) =>
        {
            _scores.ResetMatch();
            return HookResult.Continue;
        });

        RegisterListener<Listeners.OnClientPutInServer>(slot =>
        {
            if (!Config.AnnounceOnJoin || string.IsNullOrWhiteSpace(Config.JoinMessage))
                return;
            AddTimer(Config.JoinMessageDelaySeconds, () =>
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (player is not { IsValid: true, IsBot: false } || player.SteamID == 0)
                    return;
                var interval = TimeSpan.FromMinutes(Config.JoinMessageIntervalMinutes);
                if (_announced.TryGetValue(player.SteamID, out var when) && DateTime.UtcNow - when < interval)
                    return;
                _announced[player.SteamID] = DateTime.UtcNow;
                player.PrintToChat(FormatJoinMessage(player));
            });
        });
    }

    private void SendReminders()
    {
        if (_web == null || string.IsNullOrWhiteSpace(Config.JoinMessage))
            return;
        var listening = _web.ListeningSteamIds();
        var recent = TimeSpan.FromMinutes(Config.ReminderIntervalMinutes / 2.0);
        foreach (var player in Utilities.GetPlayers())
        {
            if (player is not { IsValid: true, IsBot: false } || player.SteamID == 0 || listening.Contains(player.SteamID))
                continue;
            // Saw it moments ago on joining: wait for the next round of reminders
            if (_announced.TryGetValue(player.SteamID, out var when) && DateTime.UtcNow - when < recent)
                continue;
            _announced[player.SteamID] = DateTime.UtcNow;
            player.PrintToChat(FormatJoinMessage(player));
        }
    }

    private static readonly Dictionary<string, string> ChatColorTags = BuildChatColorTags();

    private static Dictionary<string, string> BuildChatColorTags()
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in typeof(ChatColors).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            if (field.FieldType == typeof(char) && field.GetValue(null) is char c)
                tags.TryAdd("{" + field.Name + "}", c.ToString());
        return tags;
    }

    private static string ApplyColors(string text)
    {
        foreach (var (tag, code) in ChatColorTags)
            text = text.Replace(tag, code, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private string Prefix => ApplyColors(Config.ChatPrefix);

    private string FormatJoinMessage(CCSPlayerController player)
    {
        var text = ApplyColors(Config.JoinMessage.Replace("{url}", PlayerUrl()).Replace("{name}", player.PlayerName));
        return $"{Prefix} {text}";
    }

    /// <summary>
    /// Off the game thread: pushes the page state to browsers every second, renews the tap's
    /// mute mask twice a second, saves settings and checks on the tap.
    /// </summary>
    private void WorkerLoop(CancellationToken stop)
    {
        long nextMask = 0, nextTick = 0, nextFlush = 0, nextTapCheck = Environment.TickCount64 + 10_000;
        while (!stop.IsCancellationRequested)
        {
            var now = Environment.TickCount64;
            try
            {
                if (now >= nextMask)
                {
                    nextMask = now + 500;
                    PushSuppressMask();
                }
                if (now >= nextTick)
                {
                    nextTick = now + 1000;
                    _web?.Tick();
                }
                if (now >= nextFlush)
                {
                    nextFlush = now + 5000;
                    _prefs?.FlushAsync();
                }
                if (now >= nextTapCheck)
                {
                    nextTapCheck = now + 10_000;
                    CheckTap();
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning(e, "CS2VoiceRelay worker");
            }
            stop.WaitHandle.WaitOne(100);
        }
    }

    public override void Unload(bool hotReload)
    {
        _workerStop?.Cancel();
        _worker?.Join(TimeSpan.FromSeconds(2));
        _workerStop?.Dispose();
        _workerStop = null;
        _worker = null;
        _prefs?.FlushAsync().Wait(TimeSpan.FromSeconds(2));
        _tap?.SendSuppressMask(0, 0);
        _tap?.Dispose();
        _web?.Dispose();
        _tap = null;
        _web = null;
    }

    [ConsoleCommand("css_voice", "Show the link to the browser voice player")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnVoiceCommand(CCSPlayerController? player, CommandInfo command)
    {
        var listening = player is { IsValid: true } && _web != null && _web.ListeningSteamIds().Contains(player.SteamID);
        var tap = _tap?.TapAlive == true ? "" : "  \u0002(voice tap not running)\u0001";
        var you = player != null ? $"  You: {(listening ? "\u0004listening" : "not connected")}\u0001" : "";
        command.ReplyToCommand($"{Prefix} Browser voice: \u0003{PlayerUrl()}\u0001{you}{tap}");
    }

    private string PlayerUrl()
    {
        if (!string.IsNullOrWhiteSpace(Config.PublicUrl))
            return Config.PublicUrl.Trim();
        _cvIp ??= ConVar.Find("ip");
        var ip = _cvIp?.StringValue;
        if (string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0" || ip == "localhost")
            ip = "SERVER-IP";
        return $"http://{ip}:{Config.Port}";
    }

    /// <summary>Tap thread: the engine sent this voice packet to the player in RecipientSlot.</summary>
    private void OnTapVoice(TapVoicePacket packet)
    {
        if (_web == null)
            return;
        if (Config.TournamentMode)
            _web.RouteToAllVoice(packet.SpeakerXuid, packet.Frame);
        if (_snapshot.BySlot.TryGetValue(packet.RecipientSlot, out var recipient))
            _web.RouteVoice(recipient.SteamId, packet.Frame);
    }

    private void SendCodeToPlayer(ulong steamId, string code)
    {
        // Called from a web thread; chat must be sent on the game thread
        Server.NextFrame(() =>
        {
            var player = Utilities.GetPlayerFromSteamId(steamId);
            if (player is { IsValid: true })
                player.PrintToChat($"{Prefix} Your browser voice code is \u0004{code}\u0001. Only enter it if you opened the voice page yourself.");
        });
    }

    private void RefreshSnapshot()
    {
        var bySlot = new Dictionary<int, PlayerInfo>();
        var bySteam = new Dictionary<ulong, PlayerInfo>();
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is not { IsValid: true, IsBot: false } || p.SteamID == 0)
                continue;

            IPAddress? addr = null;
            var ip = p.IpAddress; // "a.b.c.d:port"
            if (ip != null)
            {
                var colon = ip.LastIndexOf(':');
                if (IPAddress.TryParse(colon > 0 ? ip[..colon] : ip, out var parsed))
                    addr = parsed;
            }

            var info = new PlayerInfo(p.Slot, p.SteamID, p.PlayerName, p.TeamNum, p.PawnIsAlive, addr);
            bySlot[p.Slot] = info;
            bySteam[p.SteamID] = info;
        }

        _cvHostname ??= ConVar.Find("hostname");
        ScoreBoard board;
        try
        {
            board = Config.ShowScoreboard ? _scores.Build() : ScoreBoard.Empty;
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "Scoreboard read failed");
            board = ScoreBoard.Empty;
        }
        _snapshot = new ServerSnapshot
        {
            BySlot = bySlot,
            BySteamId = bySteam,
            Map = Server.MapName,
            Hostname = _cvHostname?.StringValue ?? "",
            Board = board,
        };
    }

    private void PushSuppressMask()
    {
        if (_tap == null || _web == null)
            return;
        ulong mask = 0;
        if (Config.SuppressGameVoiceForListeners)
        {
            var snap = _snapshot;
            foreach (var steamId in _web.ListeningSteamIds())
                if (snap.BySteamId.TryGetValue(steamId, out var p) && p.Slot is >= 0 and < 64)
                    mask |= 1UL << p.Slot;
        }
        // Valid for 2 s: if this plugin stops, in-game voice comes back on its own
        _tap.SendSuppressMask(mask, 2000, Config.ShowInGameSpeakingIndicators);
    }

    private void CheckTap()
    {
        if (_tap == null || _tap.TapAlive)
        {
            _warnedTap = false;
            return;
        }
        if (_warnedTap)
            return;
        _warnedTap = true;
        Logger.LogWarning("CS2VoiceRelayTap (Metamod plugin) isn't running; the browser player will stay silent. " +
                          "Install addons/cs2voicerelay_tap and addons/metamod/cs2voicerelay_tap.vdf.");
    }
}
