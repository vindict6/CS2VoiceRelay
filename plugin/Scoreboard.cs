using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace CS2VoiceRelay;

/// <summary>One scoreboard line, in the column order of CompStats' player readout.</summary>
public sealed record ScoreRow(
    int Slot, string? Id, string Name, int Team, bool Bot,
    int Kills, int Deaths, int Assists, int HeadshotKills, int Damage, int UtilityDamage, int EnemiesFlashed,
    int ZeusKills, int Mvps, int Score, int Money, bool Alive, int Ping);

/// <summary>The whole scoreboard: CompStats' status lines plus one row per player.</summary>
public sealed class ScoreBoard
{
    public static readonly ScoreBoard Empty = new();
    public List<ScoreRow> Rows { get; init; } = new();
    public bool Warmup { get; init; }
    public int Humans { get; init; }
    public int TWins { get; init; }
    public int CtWins { get; init; }
    /// <summary>Rounds played including the current one, as CompStats counts them for ADR.</summary>
    public int Round { get; init; } = 1;
}

/// <summary>
/// Reads the scoreboard straight from the game: kills, deaths, assists, damage, utility damage and
/// flashes from each player's match stats, score, MVPs, money and ping from the controller, team
/// wins and warmup from the team and game-rules entities. Zeus kills are the one stat the engine
/// doesn't keep, so they're counted from kill events with CompStats' rules: a taser kill of a human
/// outside warmup. Game thread only.
/// </summary>
public sealed class ScoreTracker
{
    private const int TeamT = 2, TeamCt = 3;

    private readonly Dictionary<ulong, int> _zeus = new();
    private readonly Dictionary<int, int> _lastDeathTick = new();
    private CCSGameRulesProxy? _rules;
    private readonly List<CCSTeam> _teams = new();

    public void Reset()
    {
        _zeus.Clear();
        _lastDeathTick.Clear();
        _rules = null;
        _teams.Clear();
    }

    /// <summary>A new match started (map start or warmup ending): the engine resets its stats too.</summary>
    public void ResetMatch() => _zeus.Clear();

    private static ulong Key(CCSPlayerController p) => p.IsBot || p.SteamID == 0 ? (1UL << 63) | (uint)p.Slot : p.SteamID;

    public void OnPlayerDeath(EventPlayerDeath e)
    {
        var victim = e.Userid;
        var attacker = e.Attacker;
        if (victim is not { IsValid: true } || attacker is not { IsValid: true } || attacker == victim)
            return;
        // CS2 can fire the same death twice on one tick
        var tick = Server.TickCount;
        if (_lastDeathTick.TryGetValue(victim.Slot, out var last) && last == tick)
            return;
        _lastDeathTick[victim.Slot] = tick;

        if (victim.IsBot || IsWarmup() || !(e.Weapon ?? "").Contains("taser", StringComparison.OrdinalIgnoreCase))
            return;
        var key = Key(attacker);
        _zeus[key] = _zeus.GetValueOrDefault(key) + 1;
    }

    private CCSGameRules? Rules()
    {
        if (_rules is not { IsValid: true })
            _rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        return _rules is { IsValid: true } ? _rules.GameRules : null;
    }

    public bool IsWarmup() => Rules()?.WarmupPeriod ?? false;

    private (int T, int Ct) TeamWins()
    {
        if (_teams.Count == 0 || _teams.Any(t => !t.IsValid))
        {
            _teams.Clear();
            _teams.AddRange(Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager"));
        }
        int t = 0, ct = 0;
        foreach (var team in _teams)
        {
            if (team.TeamNum == TeamT) t = team.Score;
            else if (team.TeamNum == TeamCt) ct = team.Score;
        }
        return (t, ct);
    }

    public ScoreBoard Build()
    {
        var rows = new List<ScoreRow>();
        var humans = 0;
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is not { IsValid: true } || p.IsHLTV || p.Connected != PlayerConnectedState.Connected)
                continue;
            if (!p.IsBot)
                humans++;
            try
            {
                var stats = p.ActionTrackingServices?.MatchStats;
                rows.Add(new ScoreRow(
                    p.Slot,
                    p.IsBot || p.SteamID == 0 ? null : p.SteamID.ToString(),
                    p.PlayerName ?? "",
                    p.TeamNum,
                    p.IsBot,
                    stats?.Kills ?? 0,
                    stats?.Deaths ?? 0,
                    stats?.Assists ?? 0,
                    stats?.HeadShotKills ?? 0,
                    stats?.Damage ?? 0,
                    stats?.UtilityDamage ?? 0,
                    stats?.EnemiesFlashed ?? 0,
                    _zeus.GetValueOrDefault(Key(p)),
                    p.MVPs,
                    p.Score,
                    p.InGameMoneyServices?.Account ?? 0,
                    p.PawnIsAlive,
                    p.IsBot ? 0 : (int)p.Ping));
            }
            catch
            {
                // A player leaving mid-read
            }
        }

        var rules = Rules();
        var (tWins, ctWins) = TeamWins();
        return new ScoreBoard
        {
            Rows = rows,
            Warmup = rules?.WarmupPeriod ?? false,
            Humans = humans,
            TWins = tWins,
            CtWins = ctWins,
            Round = Math.Max(1, (rules?.TotalRoundsPlayed ?? 0) + 1),
        };
    }
}
