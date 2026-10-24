using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace CS2VoiceRelay;

/// <summary>What the web side needs to know about a player. Built on the game thread.</summary>
public sealed record PlayerInfo(int Slot, ulong SteamId, string Name, int Team, bool Alive, IPAddress? Address);

/// <summary>Immutable view of the server, swapped in by the game thread twice a second.</summary>
public sealed class ServerSnapshot
{
    public static readonly ServerSnapshot Empty = new();
    public Dictionary<int, PlayerInfo> BySlot { get; init; } = new();
    public Dictionary<ulong, PlayerInfo> BySteamId { get; init; } = new();
    public string Map { get; init; } = "";
    public string Hostname { get; init; } = "";
    public ScoreBoard Board { get; init; } = ScoreBoard.Empty;
}

public sealed class WebSettings
{
    public string BindAddress = "0.0.0.0";
    public int Port = 27030;
    /// <summary>Reverse proxies whose X-Forwarded-For is believed. Empty: use the socket address.</summary>
    public List<IPNetwork> TrustedProxies = new();
    public int MaxConnections = 256;
    public int MaxConnectionsPerIp = 6;
    /// <summary>PEM paths. Both set: the port also speaks HTTPS and sends plain-HTTP visitors there.</summary>
    public string TlsCertificate = "";
    public string TlsPrivateKey = "";
    public string PageTitle = "CS2VoiceRelay";
    public string PageDescription = "";
    public string PageFooter = "";
    public bool ShowScoreboard = true;
    /// <summary>Spectators (and TournamentListeners) hear every player instead of their own routing.</summary>
    public bool TournamentMode;
    public HashSet<ulong> TournamentListeners = new();
}

/// <summary>
/// Minimal HTTP + WebSocket server for the browser player. Each browser is matched to the
/// player whose game connection comes from the same IP; if several players share that IP,
/// the browser picks one and confirms with a code sent to that player's chat.
/// Runs on thread-pool threads; game objects are only read through <see cref="ServerSnapshot"/>.
/// </summary>
public sealed class WebServer : IDisposable
{
    public sealed class Session
    {
        public required int Id;
        public required IPAddress Address;
        public required WebSocket Socket;
        public readonly Channel<(byte[] Data, WebSocketMessageType Type)> Outbox =
            Channel.CreateBounded<(byte[], WebSocketMessageType)>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        public volatile bool Ready;
        public ulong SteamId;              // 0 until identified
        public ulong PendingSteamId;
        public string? PendingCode;
        public DateTime PendingExpires;
        public int CodeAttempts;
        public string LastIdentify = "";

        public void Send(byte[] data, WebSocketMessageType type) => Outbox.Writer.TryWrite((data, type));
        public void SendJson(object o) => Send(JsonSerializer.SerializeToUtf8Bytes(o), WebSocketMessageType.Text);
    }

    private readonly WebSettings _settings;
    private readonly ILogger _log;
    private readonly Func<ServerSnapshot> _snapshot;
    private readonly Action<ulong, string> _sendCodeToPlayer;
    private readonly PrefsStore _prefs;
    private readonly string _version;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, Session> _sessions = new();
    private readonly ConcurrentDictionary<IPAddress, int> _perIp = new();
    private readonly Dictionary<string, (byte[] Body, string Type)> _files = new();
    private int _nextId;
    private Task? _acceptLoop;

    /// <summary>Listeners who hear everyone (tournament mode). Swapped by Tick, read by the tap thread.</summary>
    private volatile HashSet<ulong> _allVoice = new();
    private readonly Dictionary<ulong, RecentFrames> _recent = new();

    /// <summary>The engine sends one speaker's packet to many players; remembers the last few to send it once.</summary>
    private sealed class RecentFrames
    {
        private readonly byte[]?[] _frames = new byte[8][];
        private readonly long[] _when = new long[8];
        private int _next;

        public bool SeenRecently(byte[] frame, long now)
        {
            for (var i = 0; i < _frames.Length; i++)
                if (_frames[i] is { } f && now - _when[i] < 250 && f.AsSpan().SequenceEqual(frame))
                    return true;
            _frames[_next] = frame;
            _when[_next] = now;
            _next = (_next + 1) % _frames.Length;
            return false;
        }
    }

    private sealed record TlsCert(SslStreamCertificateContext Context, DateTime CertWritten, DateTime KeyWritten, DateTime NotAfter);
    private readonly object _tlsLock = new();
    private TlsCert? _tls;
    private DateTime _tlsChecked = DateTime.MinValue;
    private string? _tlsError;

    public WebServer(WebSettings settings, Func<ServerSnapshot> snapshot, Action<ulong, string> sendCodeToPlayer, PrefsStore prefs, string version, ILogger log)
    {
        _settings = settings;
        _snapshot = snapshot;
        _sendCodeToPlayer = sendCodeToPlayer;
        _prefs = prefs;
        _version = version;
        _log = log;
        _listener = new TcpListener(IPAddress.Parse(settings.BindAddress), settings.Port);

        LoadFile("/", "index.html", "text/html; charset=utf-8", FillPage);
        LoadFile("/app.js", "app.js", "text/javascript; charset=utf-8");
        LoadFile("/voice-engine.js", "voice-engine.js", "text/javascript; charset=utf-8");
        LoadFile("/opus-decoder.min.js", "opus-decoder.min.js", "text/javascript; charset=utf-8");
    }

    private void LoadFile(string path, string resource, string type, Func<string, string>? fill = null)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("." + resource, StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var body = ms.ToArray();
        if (fill != null)
            body = Encoding.UTF8.GetBytes(fill(Encoding.UTF8.GetString(body)));
        _files[path] = (body, type);
    }

    /// <summary>The page's heading, description and footer come from the plugin config.</summary>
    private string FillPage(string html) => html
        .Replace("{{PAGE_TITLE}}", WebUtility.HtmlEncode(_settings.PageTitle))
        .Replace("{{PAGE_DESCRIPTION}}", Linkify(_settings.PageDescription))
        .Replace("{{PAGE_FOOTER}}", Linkify(_settings.PageFooter));

    private static readonly Regex UrlPattern = new(@"https?://(?:[^\s<>&]|&amp;)+", RegexOptions.Compiled);

    /// <summary>HTML-escapes plain text and turns http(s) URLs in it into links.</summary>
    private static string Linkify(string text) =>
        UrlPattern.Replace(WebUtility.HtmlEncode(text ?? ""), m =>
        {
            var url = m.Value.TrimEnd('.', ',', '!', '?', ')', ';', ':');
            return $"<a href=\"{url}\" target=\"_blank\" rel=\"noopener\">{url}</a>{m.Value[url.Length..]}";
        });

    // ------------------------------------------------------------------ HTTPS on the same port

    private bool TlsConfigured => _settings.TlsCertificate.Length > 0 && _settings.TlsPrivateKey.Length > 0;

    /// <summary>
    /// The certificate for HTTPS, re-read when either file changes (checked at most every 30 s), so
    /// a renewal is picked up without a restart. A broken renewal keeps the last good certificate.
    /// </summary>
    private TlsCert? CurrentTls()
    {
        if (!TlsConfigured)
            return null;
        lock (_tlsLock)
        {
            if (DateTime.UtcNow - _tlsChecked < TimeSpan.FromSeconds(30))
                return _tls;
            _tlsChecked = DateTime.UtcNow;
            try
            {
                var certWritten = File.GetLastWriteTimeUtc(_settings.TlsCertificate);
                var keyWritten = File.GetLastWriteTimeUtc(_settings.TlsPrivateKey);
                if (_tls != null && _tls.CertWritten == certWritten && _tls.KeyWritten == keyWritten)
                    return _tls;

                using var pem = X509Certificate2.CreateFromPemFile(_settings.TlsCertificate, _settings.TlsPrivateKey);
                // A PKCS#12 round trip gives SslStream a key it can use on every OS
                var leaf = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
                var chain = new X509Certificate2Collection();
                chain.ImportFromPemFile(_settings.TlsCertificate);
                var intermediates = new X509Certificate2Collection();
                foreach (var c in chain)
                    if (c.Thumbprint != leaf.Thumbprint)
                        intermediates.Add(c);

                _tls = new TlsCert(SslStreamCertificateContext.Create(leaf, intermediates, offline: true),
                    certWritten, keyWritten, leaf.NotAfter.ToUniversalTime());
                _tlsError = null;
                _log.LogInformation("HTTPS enabled on port {Port}: {Subject}, valid until {NotAfter:yyyy-MM-dd}",
                    _settings.Port, leaf.GetNameInfo(X509NameType.SimpleName, false), _tls.NotAfter);
            }
            catch (Exception e)
            {
                if (_tlsError != e.Message)
                    _log.LogWarning("Can't load the HTTPS certificate ({Cert}, {Key}): {Error}. {Fallback}",
                        _settings.TlsCertificate, _settings.TlsPrivateKey, e.Message,
                        _tls != null ? "Keeping the previous certificate." : "Serving plain HTTP only.");
                _tlsError = e.Message;
            }
            return _tls;
        }
    }

    public void Start()
    {
        CurrentTls();
        _listener.Start(64);
        _acceptLoop = Task.Run(AcceptLoop);
        _log.LogInformation("Web player listening on {EndPoint}", _listener.LocalEndpoint);
    }

    public IEnumerable<Session> Sessions => _sessions.Values;

    /// <summary>SteamIDs with at least one browser that has audio enabled.</summary>
    public HashSet<ulong> ListeningSteamIds()
    {
        var set = new HashSet<ulong>();
        foreach (var s in _sessions.Values)
            if (s.Ready && s.SteamId != 0)
                set.Add(s.SteamId);
        return set;
    }

    /// <summary>Called from the tap thread for every voice packet the engine sent a player.</summary>
    public void RouteVoice(ulong recipientSteamId, byte[] frame)
    {
        // Tournament listeners get everyone through RouteToAllVoice instead
        if (_allVoice.Contains(recipientSteamId))
            return;
        foreach (var s in _sessions.Values)
            if (s.Ready && s.SteamId == recipientSteamId)
                s.Send(frame, WebSocketMessageType.Binary);
    }

    /// <summary>
    /// Tournament mode, tap thread: every voice packet the engine sends anyone, once per packet, to
    /// listeners who hear everyone. A speaker the game sends to nobody (alone on a team, say) can't
    /// be heard this way.
    /// </summary>
    public void RouteToAllVoice(ulong speaker, byte[] frame)
    {
        var allVoice = _allVoice;
        if (allVoice.Count == 0)
            return;
        if (!_recent.TryGetValue(speaker, out var recent))
            _recent[speaker] = recent = new RecentFrames();
        if (recent.SeenRecently(frame, Environment.TickCount64))
            return;
        foreach (var s in _sessions.Values)
            if (s.Ready && s.SteamId != 0 && s.SteamId != speaker && allVoice.Contains(s.SteamId))
                s.Send(frame, WebSocketMessageType.Binary);
    }

    /// <summary>Periodic: identify unbound browsers and push the roster and scoreboard.</summary>
    public void Tick()
    {
        var snap = _snapshot();
        var players = snap.BySteamId.Values
            .OrderBy(p => p.Team <= 1 ? 9 : p.Team).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new { id = p.SteamId.ToString(), name = p.Name, team = p.Team, alive = p.Alive })
            .ToList();
        // Tournament mode: spectators and listed SteamIDs on the server hear every player
        var allVoice = new HashSet<ulong>();
        if (_settings.TournamentMode)
            foreach (var p in snap.BySteamId.Values)
                if (p.Team <= 1 || _settings.TournamentListeners.Contains(p.SteamId))
                    allVoice.Add(p.SteamId);
        _allVoice = allVoice;

        var b = snap.Board;
        object? board = !_settings.ShowScoreboard ? null : new
        {
            warmup = b.Warmup,
            humans = b.Humans,
            tWins = b.TWins,
            ctWins = b.CtWins,
            round = b.Round,
            rows = b.Rows.Select(r => new
            {
                slot = r.Slot, id = r.Id, name = r.Name, team = r.Team, bot = r.Bot,
                k = r.Kills, d = r.Deaths, a = r.Assists, hs = r.HeadshotKills, dmg = r.Damage,
                ud = r.UtilityDamage, ef = r.EnemiesFlashed, z = r.ZeusKills, mvp = r.Mvps,
                score = r.Score, money = r.Money, alive = r.Alive, ping = r.Ping,
            }).ToList(),
        };

        foreach (var s in _sessions.Values)
        {
            if (s.SteamId == 0)
                Identify(s, snap);
            s.SendJson(new
            {
                t = "state",
                map = snap.Map,
                host = snap.Hostname,
                me = s.SteamId == 0 ? null : s.SteamId.ToString(),
                online = s.SteamId != 0 && snap.BySteamId.ContainsKey(s.SteamId),
                players,
                board,
                tournament = _settings.TournamentMode,
                allVoice = s.SteamId != 0 && allVoice.Contains(s.SteamId),
            });
        }
    }

    private void Identify(Session s, ServerSnapshot snap)
    {
        var matches = snap.BySteamId.Values.Where(p => p.Address != null && p.Address.Equals(s.Address)).ToList();
        string key;
        if (matches.Count == 1)
        {
            Bind(s, matches[0]);
            return;
        }
        if (matches.Count == 0)
        {
            key = "none";
            if (s.LastIdentify != key)
                s.SendJson(new { t = "identify", status = "none", ip = s.Address.ToString() });
        }
        else
        {
            key = "choose:" + string.Join(",", matches.Select(m => m.SteamId).OrderBy(x => x));
            if (s.LastIdentify != key)
                s.SendJson(new { t = "identify", status = "choose", players = matches.Select(m => new { id = m.SteamId.ToString(), name = m.Name }) });
        }
        s.LastIdentify = key;
    }

    private void Bind(Session s, PlayerInfo p)
    {
        s.SteamId = p.SteamId;
        s.PendingCode = null;
        s.LastIdentify = "bound";
        s.SendJson(new { t = "bound", id = p.SteamId.ToString(), name = p.Name });
        SendPrefs(s);
        _log.LogInformation("Web player linked to {Name} ({SteamId}) from {Address}", p.Name, p.SteamId, s.Address);
    }

    /// <summary>The listener's saved volume settings; "empty" tells the browser to upload its own.</summary>
    private void SendPrefs(Session s)
    {
        var p = _prefs.Get(s.SteamId);
        s.SendJson(new
        {
            t = "prefs",
            empty = p == null,
            master = p?.Master ?? 100,
            masterMuted = p?.MasterMuted ?? false,
            players = p?.Players ?? new Dictionary<string, PlayerPref>(),
        });
    }

    private static int? ReadInt(JsonElement m, string name) =>
        m.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : null;

    private static bool? ReadBool(JsonElement m, string name) =>
        m.TryGetProperty(name, out var e) && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False) ? e.GetBoolean() : null;

    private void HandleClientMessage(Session s, JsonElement m)
    {
        var type = m.TryGetProperty("t", out var t) ? t.GetString() : null;
        var snap = _snapshot();
        switch (type)
        {
            case "ready":
                s.Ready = m.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True;
                break;

            case "pref":
                // A single change: master volume/mute, or one speaker's volume/mute
                if (s.SteamId == 0)
                    return;
                if (m.TryGetProperty("id", out var speakerEl) && ulong.TryParse(speakerEl.GetString(), out var speaker))
                    _prefs.SetPlayer(s.SteamId, speaker, ReadInt(m, "vol") ?? 100, ReadBool(m, "muted") ?? false);
                else
                    _prefs.SetMaster(s.SteamId, ReadInt(m, "master"), ReadBool(m, "masterMuted"));
                break;

            case "prefs_set":
                // One-time upload of what this browser had before settings were saved server-side
                if (s.SteamId == 0 || _prefs.Get(s.SteamId) != null)
                    return;
                var players = new List<(ulong, int, bool)>();
                if (m.TryGetProperty("players", out var playersEl) && playersEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in playersEl.EnumerateObject())
                    {
                        if (players.Count >= PrefsStore.MaxPlayersPerListener)
                            break;
                        if (ulong.TryParse(entry.Name, out var sid) && entry.Value.ValueKind == JsonValueKind.Object)
                            players.Add((sid, ReadInt(entry.Value, "vol") ?? 100, ReadBool(entry.Value, "muted") ?? false));
                    }
                }
                _prefs.Replace(s.SteamId, ReadInt(m, "master") ?? 100, ReadBool(m, "masterMuted") ?? false, players);
                break;

            case "choose":
                if (s.SteamId != 0 || !m.TryGetProperty("id", out var idEl) || !ulong.TryParse(idEl.GetString(), out var id))
                    return;
                // Only players actually sharing this browser's IP can be chosen
                if (!snap.BySteamId.TryGetValue(id, out var target) || target.Address == null || !target.Address.Equals(s.Address))
                    return;
                s.PendingSteamId = id;
                s.PendingCode = RandomNumberGenerator.GetInt32(1000, 10000).ToString();
                s.PendingExpires = DateTime.UtcNow.AddMinutes(2);
                s.CodeAttempts = 0;
                _sendCodeToPlayer(id, s.PendingCode);
                s.SendJson(new { t = "code_sent", name = target.Name });
                break;

            case "code":
                if (s.SteamId != 0 || s.PendingCode == null || !m.TryGetProperty("code", out var codeEl))
                    return;
                if (DateTime.UtcNow > s.PendingExpires || ++s.CodeAttempts > 5)
                {
                    s.PendingCode = null;
                    s.LastIdentify = "";
                    s.SendJson(new { t = "code_result", ok = false, msg = "Code expired. Pick your name again." });
                    return;
                }
                if (codeEl.GetString() == s.PendingCode && snap.BySteamId.TryGetValue(s.PendingSteamId, out var chosen))
                    Bind(s, chosen);
                else
                    s.SendJson(new { t = "code_result", ok = false, msg = "That code doesn't match. Check your in-game chat." });
                break;
        }
    }

    // ------------------------------------------------------------------ HTTP / WebSocket plumbing

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            _ = Task.Run(() => HandleConnection(client));
        }
    }

    private static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    private bool IsTrustedProxy(IPAddress address)
    {
        foreach (var network in _settings.TrustedProxies)
            if (network.Contains(address))
                return true;
        return false;
    }

    /// <summary>
    /// The browser's address. Behind a trusted reverse proxy this is the last X-Forwarded-For
    /// entry: the one the proxy itself appended. Earlier entries come from the client and can
    /// be forged.
    /// </summary>
    private IPAddress ClientAddress(IPAddress remote, Dictionary<string, string> headers)
    {
        if (!IsTrustedProxy(remote) || !headers.TryGetValue("x-forwarded-for", out var xff))
            return remote;
        var last = xff.Split(',')[^1].Trim();
        return IPAddress.TryParse(last, out var forwarded) ? Normalize(forwarded) : remote;
    }

    private async Task HandleConnection(TcpClient client)
    {
        var remote = Normalize(((IPEndPoint)client.Client.RemoteEndPoint!).Address);
        IPAddress? counted = null;
        SslStream? ssl = null;
        try
        {
            using (client)
            {
                if (_sessions.Count >= _settings.MaxConnections)
                    return;

                client.NoDelay = true;
                Stream stream = client.GetStream();

                // One port, both protocols: a TLS handshake starts with byte 0x16, HTTP with a letter
                var tls = CurrentTls();
                if (TlsConfigured)
                {
                    var first = new byte[1];
                    using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        if (await client.Client.ReceiveAsync(first, SocketFlags.Peek, wait.Token) == 0)
                            return;
                    if (first[0] == 0x16)
                    {
                        if (tls == null)
                            return;
                        ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                        using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificateContext = tls.Context,
                            ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
                        }, handshake.Token);
                        stream = ssl;
                    }
                }

                var request = await ReadRequest(stream);
                if (request == null)
                    return;
                var (method, path, headers) = request.Value;

                // Count per browser, not per socket: behind a proxy every socket has the proxy's address
                var address = ClientAddress(remote, headers);
                counted = address;
                if (_perIp.AddOrUpdate(address, 1, (_, n) => n + 1) > _settings.MaxConnectionsPerIp)
                {
                    await WriteResponse(stream, 429, "text/plain", "Too Many Connections"u8.ToArray(), false);
                    return;
                }

                var query = path.IndexOf('?');
                if (query >= 0)
                    path = path[..query];
                if (path.EndsWith("/ws", StringComparison.Ordinal)
                    && headers.TryGetValue("upgrade", out var up) && up.Equals("websocket", StringComparison.OrdinalIgnoreCase)
                    && headers.TryGetValue("sec-websocket-key", out var key))
                {
                    await RunWebSocket(stream, key, address);
                    return;
                }

                if (method != "GET" && method != "HEAD")
                {
                    await WriteResponse(stream, 405, "text/plain", "Method Not Allowed"u8.ToArray(), method == "HEAD");
                    return;
                }
                // With a certificate, send direct plain-HTTP visitors to HTTPS on this same port.
                // Not through a reverse proxy: that already did TLS, and its requests arrive as plain HTTP.
                if (ssl == null && tls != null && tls.NotAfter > DateTime.UtcNow && !IsTrustedProxy(remote)
                    && headers.TryGetValue("host", out var host) && IsSafeHost(host))
                {
                    await WriteResponse(stream, 307, "text/plain", Array.Empty<byte>(), method == "HEAD",
                        $"Location: https://{host}{request.Value.Path}\r\n");
                    return;
                }
                // Served under a reverse-proxy prefix too (e.g. /voice/app.js)
                var file = _files.Keys.Where(k => k != "/").FirstOrDefault(k => path.EndsWith(k, StringComparison.Ordinal));
                if (file == null && (path == "/" || path.EndsWith("/", StringComparison.Ordinal) || path.EndsWith("/index.html", StringComparison.Ordinal)))
                    file = "/";
                if (file == null)
                {
                    await WriteResponse(stream, 404, "text/plain", "Not Found"u8.ToArray(), method == "HEAD");
                    return;
                }
                var (body, type) = _files[file];
                await WriteResponse(stream, 200, type, body, method == "HEAD");
            }
        }
        catch (Exception e) when (e is IOException or SocketException or WebSocketException or OperationCanceledException
                                       or ObjectDisposedException or System.Security.Authentication.AuthenticationException)
        {
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Web connection from {Address} failed", remote);
        }
        finally
        {
            ssl?.Dispose();
            if (counted != null)
                _perIp.AddOrUpdate(counted, 0, (_, n) => Math.Max(0, n - 1));
        }
    }

    /// <summary>A Host header that's safe to echo into a redirect: host name or IP, optional port.</summary>
    private static bool IsSafeHost(string host) =>
        host.Length is > 0 and <= 255 && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']');

    private static async Task<(string Method, string Path, Dictionary<string, string> Headers)?> ReadRequest(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buf = new byte[8192];
        var len = 0;
        while (true)
        {
            if (len == buf.Length)
                return null;
            var n = await stream.ReadAsync(buf.AsMemory(len), timeout.Token);
            if (n == 0)
                return null;
            len += n;
            var end = buf.AsSpan(0, len).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
                var first = lines[0].Split(' ');
                if (first.Length < 3)
                    return null;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0)
                        headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                return (first[0], first[1], headers);
            }
        }
    }

    private static async Task WriteResponse(Stream stream, int status, string type, byte[] body, bool headOnly, string extraHeaders = "")
    {
        var reason = status switch
        {
            200 => "OK", 307 => "Temporary Redirect", 404 => "Not Found", 405 => "Method Not Allowed",
            429 => "Too Many Requests", _ => "Error",
        };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n{extraHeaders}" +
                   "Cache-Control: no-cache\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\n" +
                   "Referrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        if (!headOnly)
            await stream.WriteAsync(body);
    }

    private async Task RunWebSocket(Stream stream, string key, IPAddress address)
    {
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n"));

        using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20) });
        var session = new Session { Id = Interlocked.Increment(ref _nextId), Address = address, Socket = ws };
        _sessions[session.Id] = session;
        using var done = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var writer = Task.Run(() => WriteLoop(session, done.Token));
        try
        {
            session.SendJson(new { t = "hello", version = _version });
            Identify(session, _snapshot());

            // Messages are small, except a one-time settings upload; assemble up to 64 KB
            const int maxMessage = 64 * 1024;
            var buf = new byte[maxMessage];
            var len = 0;
            var oversized = false;
            while (ws.State == WebSocketState.Open && !done.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buf.AsMemory(len), done.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                len += result.Count;
                if (len == buf.Length && !result.EndOfMessage)
                {
                    oversized = true;
                    len = 0;
                }
                if (!result.EndOfMessage)
                    continue;

                var count = len;
                var skip = oversized || result.MessageType != WebSocketMessageType.Text || count == 0;
                len = 0;
                oversized = false;
                if (skip)
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(buf.AsMemory(0, count));
                    HandleClientMessage(session, doc.RootElement);
                }
                catch (JsonException) { }
            }
        }
        finally
        {
            _sessions.TryRemove(session.Id, out _);
            session.Outbox.Writer.TryComplete();
            done.Cancel();
            try { await writer; } catch { }
            if (ws.State == WebSocketState.Open)
            {
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
            }
        }
    }

    private static async Task WriteLoop(Session s, CancellationToken token)
    {
        await foreach (var (data, type) in s.Outbox.Reader.ReadAllAsync(token))
        {
            if (s.Socket.State != WebSocketState.Open)
                break;
            await s.Socket.SendAsync(data, type, true, token);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        foreach (var s in _sessions.Values)
            s.Outbox.Writer.TryComplete();
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
