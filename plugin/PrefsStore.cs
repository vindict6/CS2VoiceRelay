using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CS2VoiceRelay;

public sealed class PlayerPref
{
    [JsonPropertyName("vol")] public int Vol { get; set; } = 100;
    [JsonPropertyName("muted")] public bool Muted { get; set; }
}

/// <summary>One listener's volume settings: master plus per-speaker, keyed by speaker SteamID.</summary>
public sealed class ListenerPrefs
{
    [JsonPropertyName("master")] public int Master { get; set; } = 100;
    [JsonPropertyName("masterMuted")] public bool MasterMuted { get; set; }
    [JsonPropertyName("players")] public Dictionary<string, PlayerPref> Players { get; set; } = new();
    /// <summary>Even out voices: bring quiet and loud players to a similar level in the browser.</summary>
    [JsonPropertyName("normalize")] public bool Normalize { get; set; }
    [JsonPropertyName("updated")] public DateTime Updated { get; set; }

    [JsonIgnore]
    public bool IsDefault => Master == 100 && !MasterMuted && !Normalize && Players.Count == 0;

    public ListenerPrefs Copy() => new()
    {
        Master = Master,
        MasterMuted = MasterMuted,
        Normalize = Normalize,
        Players = Players.ToDictionary(kv => kv.Key, kv => new PlayerPref { Vol = kv.Value.Vol, Muted = kv.Value.Muted }),
        Updated = Updated,
    };
}

/// <summary>
/// Per-listener volume settings, saved on the game server so they follow a player to any
/// browser. Kept in memory and written to voice_prefs.json in the plugin's config folder,
/// which deploys don't overwrite. Thread-safe; writes happen on a background task.
/// </summary>
public sealed class PrefsStore
{
    public const int MaxPlayersPerListener = 500;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private Dictionary<string, ListenerPrefs> _data = new();
    private bool _dirty;
    private int _saving;

    public PrefsStore(string path, ILogger log)
    {
        _path = path;
        _log = log;
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, ListenerPrefs>>(File.ReadAllText(_path), JsonOptions);
            if (loaded != null)
            {
                lock (_lock)
                    _data = loaded;
                _log.LogInformation("Loaded voice settings for {Count} player(s)", loaded.Count);
            }
        }
        catch (Exception e)
        {
            // Keep the unreadable file for inspection instead of overwriting it
            var backup = _path + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try { File.Move(_path, backup); } catch { }
            _log.LogWarning(e, "Could not read {Path}; moved it to {Backup} and started fresh", _path, backup);
        }
    }

    /// <summary>A copy of the listener's settings, or null if they have none saved.</summary>
    public ListenerPrefs? Get(ulong listener)
    {
        lock (_lock)
            return _data.TryGetValue(listener.ToString(), out var p) ? p.Copy() : null;
    }

    public void SetMaster(ulong listener, int? master, bool? muted)
    {
        Update(listener, p =>
        {
            if (master.HasValue)
                p.Master = Math.Clamp(master.Value, 0, 200);
            if (muted.HasValue)
                p.MasterMuted = muted.Value;
        });
    }

    public void SetNormalize(ulong listener, bool on) => Update(listener, p => p.Normalize = on);

    public void SetPlayer(ulong listener, ulong speaker, int vol, bool muted)
    {
        Update(listener, p =>
        {
            var key = speaker.ToString();
            vol = Math.Clamp(vol, 0, 200);
            if (vol == 100 && !muted)
                p.Players.Remove(key);
            else if (p.Players.Count < MaxPlayersPerListener || p.Players.ContainsKey(key))
                p.Players[key] = new PlayerPref { Vol = vol, Muted = muted };
        });
    }

    /// <summary>Replaces everything (used once to upload what a browser had before saving moved server-side).</summary>
    public void Replace(ulong listener, int master, bool masterMuted, IEnumerable<(ulong Speaker, int Vol, bool Muted)> players, bool normalize = false)
    {
        Update(listener, p =>
        {
            p.Master = Math.Clamp(master, 0, 200);
            p.MasterMuted = masterMuted;
            p.Normalize = normalize;
            p.Players.Clear();
            foreach (var (speaker, vol, muted) in players.Take(MaxPlayersPerListener))
            {
                var v = Math.Clamp(vol, 0, 200);
                if (v != 100 || muted)
                    p.Players[speaker.ToString()] = new PlayerPref { Vol = v, Muted = muted };
            }
        });
    }

    private void Update(ulong listener, Action<ListenerPrefs> change)
    {
        lock (_lock)
        {
            var key = listener.ToString();
            if (!_data.TryGetValue(key, out var p))
                p = new ListenerPrefs();
            change(p);
            p.Updated = DateTime.UtcNow;
            if (p.IsDefault)
                _data.Remove(key);
            else
                _data[key] = p;
            _dirty = true;
        }
    }

    /// <summary>Write pending changes in the background. Call periodically and on unload.</summary>
    public Task FlushAsync()
    {
        string json;
        lock (_lock)
        {
            if (!_dirty)
                return Task.CompletedTask;
            json = JsonSerializer.Serialize(_data, JsonOptions);
            _dirty = false;
        }
        if (Interlocked.Exchange(ref _saving, 1) == 1)
        {
            lock (_lock)
                _dirty = true; // a write is already running; try again next flush
            return Task.CompletedTask;
        }
        return Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception e)
            {
                lock (_lock)
                    _dirty = true;
                _log.LogWarning(e, "Could not save voice settings to {Path}", _path);
            }
            finally
            {
                Interlocked.Exchange(ref _saving, 0);
            }
        });
    }
}
