using System.Net;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
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
    /// <summary>
    /// Branding. Logo: an image URL (https://...) or a file on the server (absolute, or relative to this
    /// config's folder): png, jpg, gif, webp, svg or ico. Shown in the page header, as the tab icon and
    /// in the widget. Colors are hex, like "#e8a33a".
    /// </summary>
    [JsonPropertyName("Logo")] public string Logo { get; set; } = "";
    [JsonPropertyName("AccentColor")] public string AccentColor { get; set; } = "#e8a33a";
    [JsonPropertyName("CtColor")] public string CtColor { get; set; } = "#7fb2e5";
    [JsonPropertyName("TColor")] public string TColor { get; set; } = "#e6b25f";
    /// <summary>Show the live scoreboard (CompStats layout) on the page, to everyone who opens it.</summary>
    [JsonPropertyName("ShowScoreboard")] public bool ShowScoreboard { get; set; } = true;
    /// <summary>Show CompStats' "Zeus Leader" line under the scoreboard.</summary>
    [JsonPropertyName("ShowZeusLeader")] public bool ShowZeusLeader { get; set; } = false;

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

    /// <summary>
    /// !adminchat: admins with this CounterStrikeSharp permission can talk to and hear every player,
    /// both teams, alive or dead. !adminlisten: hear every player, with their own voice still going
    /// to their team only. Running either again turns it off; both end at map change. Also on the page.
    /// </summary>
    [JsonPropertyName("AdminChatPermission")] public string AdminChatPermission { get; set; } = "@css/chat";
    /// <summary>Tell everyone in chat when an admin starts or stops talking to or listening to everyone.</summary>
    [JsonPropertyName("AnnounceAdminChat")] public bool AnnounceAdminChat { get; set; } = true;

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
    public override string ModuleVersion => "2.6.0";
    public override string ModuleAuthor => "vindict6";
    public override string ModuleDescription => "Hear in-game voice in your browser when CS2's voice playback breaks";

    public RelayConfig Config { get; set; } = new();

    private WebServer? _web;
    private TapLink? _tap;
    private PrefsStore? _prefs;
    private volatile ServerSnapshot _snapshot = ServerSnapshot.Empty;
    private readonly Dictionary<ulong, DateTime> _announced = new();
    private readonly ScoreTracker _scores = new();
    private readonly Dictionary<ulong, DateTime> _lastTestSound = new();
    private readonly HashSet<int> _adminChat = new();     // player slots with !adminchat on
    private readonly HashSet<int> _adminListen = new();   // player slots with !adminlisten on
    private const VoiceFlags AdminFlags = VoiceFlags.All | VoiceFlags.ListenAll;
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
        config.AccentColor = CheckColor("AccentColor", config.AccentColor, "#e8a33a");
        config.CtColor = CheckColor("CtColor", config.CtColor, "#7fb2e5");
        config.TColor = CheckColor("TColor", config.TColor, "#e6b25f");
        Config = config;
        AddConfigNotes();
    }

    private string ConfigFilePath() =>
        Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", "CS2VoiceRelay", "CS2VoiceRelay.json"));

    private static readonly System.Text.RegularExpressions.Regex HexColor = new("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$");

    /// <summary>A typo in a color shouldn't stop voice from working: warn and use the default.</summary>
    private string CheckColor(string key, string value, string fallback)
    {
        value = (value ?? "").Trim();
        if (HexColor.IsMatch(value))
            return value;
        Logger.LogWarning("{Key} '{Value}' isn't a hex color like #e8a33a; using {Default}", key, value, fallback);
        return fallback;
    }

    private const string ConfigNotesMarker = "// ---- CS2VoiceRelay notes";

    /// <summary>
    /// Adds a comment block to the top of CS2VoiceRelay.json listing the chat colors and message
    /// placeholders. CounterStrikeSharp skips comments when it reads the file, and the block is only
    /// added when it isn't there and refreshed when it's out of date; settings are never touched.
    /// </summary>
    private void AddConfigNotes()
    {
        try
        {
            var path = ConfigFilePath();
            if (!File.Exists(path))
                return;
            var text = File.ReadAllText(path);

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
                "// Logo: image URL (https://...) or a file next to this config, e.g. \"logo.png\" (png, jpg, gif, webp, svg, ico).",
                "// AccentColor, CtColor, TColor: hex colors like \"#e8a33a\" for buttons and links, and the two teams.",
                "// TlsCertificate, TlsPrivateKey: PEM files (e.g. Let's Encrypt fullchain.pem, privkey.pem) to serve HTTPS on Port too.",
                "// AdminChatPermission: who can use !adminchat (talk to and hear everyone) and !adminlisten (hear everyone), e.g. \"@css/chat\".",
                "// AnnounceAdminChat: tell everyone in chat when an admin starts or stops either.",
                "// TournamentMode: spectators listening in a browser (and SteamID64s in TournamentListeners) hear every player,",
                "//   with buttons to solo CT or T. Players on a team still only hear their own team.",
                "// ----",
            });

            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var block = string.Join(newline, lines) + newline;

            // Already there: refresh it when this version has more to say (only these comment lines change)
            var start = text.IndexOf(ConfigNotesMarker, StringComparison.Ordinal);
            if (start >= 0)
            {
                var pos = text.IndexOf('\n', start);
                while (pos >= 0 && pos + 1 < text.Length)
                {
                    var next = text.IndexOf('\n', pos + 1);
                    var line = (next < 0 ? text[(pos + 1)..] : text[(pos + 1)..next]).TrimEnd('\r');
                    if (line == "// ----")
                    {
                        var end = next < 0 ? text.Length : next + 1;
                        if (text[start..end] != block)
                            File.WriteAllText(path, text[..start] + block + text[end..]);
                        return;
                    }
                    if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                        return;   // the notes were edited into something else; leave the file alone
                    pos = next;
                }
                return;
            }

            // Keep CounterStrikeSharp's own "generated by" comment line first
            var insertAt = 0;
            while (insertAt < text.Length && text.AsSpan(insertAt).TrimStart(" \t").StartsWith("//"))
            {
                var eol = text.IndexOf('\n', insertAt);
                insertAt = eol < 0 ? text.Length : eol + 1;
            }
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
                ShowZeusLeader = Config.ShowZeusLeader,
                Logo = Config.Logo.Trim(),
                LogoBaseDir = Path.GetDirectoryName(ConfigFilePath()) ?? "",
                AccentColor = Config.AccentColor,
                CtColor = Config.CtColor,
                TColor = Config.TColor,
                TournamentMode = Config.TournamentMode,
                TournamentListeners = Config.TournamentListeners.Select(e => ulong.Parse(e.Trim())).ToHashSet(),
            },
            () => _snapshot,
            SendCodeToPlayer,
            _prefs,
            ModuleVersion,
            Logger,
            SetAdminModeFromWeb);
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

        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _scores.Reset();
            _adminChat.Clear();
            _adminListen.Clear();
        });
        // A leaving admin's voice flags must not carry over to whoever gets the slot next
        RegisterListener<Listeners.OnClientDisconnect>(slot =>
        {
            var had = _adminChat.Remove(slot) | _adminListen.Remove(slot);
            if (!had)
                return;
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is { IsValid: true })
                player.VoiceFlags &= ~AdminFlags;
        });
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
        // Don't leave anyone talking to or listening to everyone after the plugin goes away
        foreach (var slot in _adminChat.Union(_adminListen))
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is { IsValid: true })
                player.VoiceFlags &= ~AdminFlags;
        }
        _adminChat.Clear();
        _adminListen.Clear();
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

    public enum AdminMode { Chat, Listen }

    [ConsoleCommand("css_adminchat", "Admins: talk to and hear every player. Run it again to turn it off.")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnAdminChatCommand(CCSPlayerController? player, CommandInfo command) => AdminCommand(player, command, AdminMode.Chat);

    [ConsoleCommand("css_adminlisten", "Admins: hear every player without talking to them. Run it again to turn it off.")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnAdminListenCommand(CCSPlayerController? player, CommandInfo command) => AdminCommand(player, command, AdminMode.Listen);

    private void AdminCommand(CCSPlayerController? player, CommandInfo command, AdminMode mode)
    {
        if (player is not { IsValid: true })
            return;
        if (!IsChatAdmin(player))
        {
            command.ReplyToCommand($"{Prefix} !admin{(mode == AdminMode.Chat ? "chat" : "listen")} is for admins ({Config.AdminChatPermission.Trim()}).");
            return;
        }
        SetAdminMode(player, mode, !ModeSet(mode).Contains(player.Slot), fromPage: false);
    }

    private HashSet<int> ModeSet(AdminMode mode) => mode == AdminMode.Chat ? _adminChat : _adminListen;

    private bool IsChatAdmin(CCSPlayerController player)
    {
        var permission = Config.AdminChatPermission.Trim();
        return permission.Length == 0 || AdminManager.PlayerHasPermissions(player, permission);
    }

    /// <summary>The voice page's admin toggles (web thread): switch on the game thread, checking again there.</summary>
    private void SetAdminModeFromWeb(ulong steamId, string mode, bool on)
    {
        var which = mode == "listen" ? AdminMode.Listen : AdminMode.Chat;
        Server.NextFrame(() =>
        {
            var player = Utilities.GetPlayerFromSteamId(steamId);
            if (player is { IsValid: true } && IsChatAdmin(player) && on != ModeSet(which).Contains(player.Slot))
                SetAdminMode(player, which, on, fromPage: true);
        });
    }

    /// <summary>
    /// Admin chat (talk to and hear everyone) or admin listen (hear everyone, talk to your team as
    /// usual). The voice flags come from both, so turning one off leaves the other working. Game thread.
    /// </summary>
    private void SetAdminMode(CCSPlayerController player, AdminMode mode, bool on, bool fromPage)
    {
        var set = ModeSet(mode);
        if (on) set.Add(player.Slot);
        else set.Remove(player.Slot);

        var flags = player.VoiceFlags & ~AdminFlags;
        if (_adminChat.Contains(player.Slot))
            flags |= VoiceFlags.All | VoiceFlags.ListenAll;
        if (_adminListen.Contains(player.Slot))
            flags |= VoiceFlags.ListenAll;
        player.VoiceFlags = flags;

        var how = fromPage ? " (from your voice page)" : "";
        var name = player.PlayerName;
        string mine, everyone;
        if (mode == AdminMode.Chat)
        {
            var stillListening = !on && _adminListen.Contains(player.Slot) ? " You're still listening to everyone (admin listen)." : "";
            mine = on
                ? $"Admin chat \u0004on\u0001{how}: every player hears you and you hear every player. Turn it off with !adminchat or on the voice page."
                : $"Admin chat \u0002off\u0001{how}. Your voice goes to your team again.{stillListening}";
            everyone = on ? $"Admin \u0010{name}\u0001 is talking to everyone." : $"\u0010{name}\u0001 stopped talking to everyone.";
        }
        else
        {
            mine = on
                ? $"Admin listen \u0004on\u0001{how}: you hear every player. Your own voice still only goes to your team. Turn it off with !adminlisten or on the voice page."
                : $"Admin listen \u0002off\u0001{how}.";
            everyone = on ? $"Admin \u0010{name}\u0001 is listening to everyone." : $"\u0010{name}\u0001 stopped listening to everyone.";
        }
        player.PrintToChat($"{Prefix} {mine}");
        if (Config.AnnounceAdminChat)
            Server.PrintToChatAll($"{Prefix} {everyone}");
        Logger.LogInformation("Admin {Mode} {State} for {Name} ({SteamId}){How}", mode, on ? "on" : "off", name, player.SteamID, how);
        RefreshSnapshot();   // the page's toggles show the new state on the next update
    }

    [ConsoleCommand("css_testvoice", "Play a test chime on your browser voice page")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnTestVoiceCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true } || player.SteamID == 0 || _web == null)
            return;
        if (_lastTestSound.TryGetValue(player.SteamID, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(3))
            return;
        _lastTestSound[player.SteamID] = DateTime.UtcNow;

        var text = _web.SendTestSound(player.SteamID) switch
        {
            TestSoundResult.Playing => "Playing a test chime on your voice page. If you hear it, browser voice is working.",
            TestSoundResult.NotListening => "Your voice page is open but not listening. Click \u0004Start listening\u0001 there, then try !testvoice again.",
            _ => $"No voice page is linked to you yet. Open \u0003{PlayerUrl()}\u0001 in your browser, then try !testvoice again.",
        };
        command.ReplyToCommand($"{Prefix} {text}");
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

            var info = new PlayerInfo(p.Slot, p.SteamID, p.PlayerName, p.TeamNum, p.PawnIsAlive, addr,
                IsChatAdmin(p), _adminChat.Contains(p.Slot), _adminListen.Contains(p.Slot));
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
