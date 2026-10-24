# CS2VoiceRelay

Out-of-game voice for CS2 community servers that run Workshop maps.

CS2's own voice chat breaks on the **client** after a Workshop map unloads: switching between
Workshop maps, rejoining a server that's on one, or going back to a stock map leaves players
unable to hear anyone until they restart the game. Valve has tracked this since January 2025
([csgo-osx-linux#3980](https://github.com/ValveSoftware/csgo-osx-linux/issues/3980)); the
client deletes the shared sound it plays all incoming voice through and never recreates it.
Nothing a server sends can repair that, so no server plugin can fix in-game voice.

CS2VoiceRelay sidesteps it: a small voice client on each player's PC captures and plays audio,
and a CounterStrikeSharp plugin on the server relays it between them. The game is only used
for identity, team/alive state and the server's voice rules, so **players hear exactly what
the server would normally let them hear**, and it keeps working across every map change.

## What you get

- **Server plugin** (`plugin/`, CounterStrikeSharp): UDP relay on a second port. Enforces the
  server's real voice cvars (`sv_alltalk`, `sv_deadtalk`, `sv_full_alltalk`,
  `sv_talk_enemy_dead`, `sv_talk_enemy_living`, `sv_spec_hear`, `sv_voiceenable`) and admin
  mutes (`VoiceFlags.Muted`), so the relay never lets anyone hear more than the game would.
  Shows who's talking as a HUD line, since the real voice icons can't be driven.
- **Voice client** (`client/`, Python, or the prebuilt `CS2VoiceRelay.exe`): push-to-talk using
  your CS2 `+voicerecord` bind (read from your local Steam install), live player list with team,
  alive/dead, who you can currently hear and who's speaking, mic meter, device selection,
  local per-player mute. All of that information is pushed to the client by the plugin.

## Install (server)

1. Install [CounterStrikeSharp](https://docs.cssharp.dev/) (v1.0.378 or newer).
2. Download `CS2VoiceRelay-plugin.zip` from [Releases](../../releases) and extract it into
   `game/csgo/` so you have `addons/counterstrikesharp/plugins/CS2VoiceRelay/CS2VoiceRelay.dll`.
3. Open UDP port **27030** (or whatever you set) on the server's firewall/host panel.
4. Start the server once; edit `addons/counterstrikesharp/configs/plugins/CS2VoiceRelay/CS2VoiceRelay.json`:

   | Key | Default | Meaning |
   |---|---|---|
   | `Port` | `27030` | UDP port the relay listens on |
   | `BindAddress` | `0.0.0.0` | Interface to bind |
   | `PublicAddress` | `""` | Host players should connect to; shown in the chat hint |
   | `RequireMatchingIp` | `false` | Only accept a voice client from the same IP as the game connection |
   | `ShowSpeakerHud` | `true` | Show "who is talking" as a HUD line |
   | `SessionTimeoutSeconds` | `10` | Drop a voice client after this long without traffic |
   | `MaxPacketsPerSecond` | `80` | Per-client rate limit (normal speech is 50/s) |
   | `ForgetAfterSeconds` | `300` | Keep a player's token and voice session this long after they leave the server |

Players get a token in chat when they join (`!voice` shows it again). The token is tied to
their SteamID, so nobody can transmit as someone else.

The voice client stays connected through map changes and quick rejoins: the token stays the
same, and the relay keeps the player's session until they've been off the server for
`ForgetAfterSeconds`. While the next map loads, players who haven't loaded in yet aren't routed
any audio; nobody has to reconnect the voice client.

## Install (players)

1. Download `CS2VoiceRelay-client-windows.zip` from [Releases](../../releases) and run
   `CS2VoiceRelay.exe` (or `python voiceclient.py` with `pip install -r requirements.txt`).
2. Join the server, read the address and token from chat, paste them into the client, press
   **Connect**. Your SteamID and push-to-talk key are filled in from Steam automatically.
3. Hold your normal voice key. The client shows who you can hear and who's talking.

The client keeps its settings in `%APPDATA%\CS2VoiceRelay\settings.json`.

## How voice routing is enforced

Audio only ever flows server → listener when the plugin's copy of the game's rules says so:

| Situation | Hears? |
|---|---|
| `sv_voiceenable 0`, or speaker is admin-muted | never |
| `sv_full_alltalk 1` | everyone, including spectators |
| listener is spectator | per `sv_spec_hear` (0 spectators only, 1/2 everyone, 3/4 nobody) |
| `sv_alltalk 1` | all players |
| same team, speaker alive | yes |
| same team, speaker dead | dead listeners yes; living listeners only with `sv_deadtalk 1` |
| enemies, both alive | only with `sv_talk_enemy_living 1` |
| enemies, both dead | only with `sv_talk_enemy_dead 1` |

`sv_spec_hear 2` ("spectated team") can't be evaluated outside the game and is treated as 1.
Players' *client-side* mutes in the scoreboard aren't visible to the server; use the local mute
in the voice client instead.

## Limitations

- No positional/proximity voice; audio is plain 16 kHz mono PCM (about 32 KB/s per speaker).
- Everyone who wants voice needs the client running. Players without it still show up in the list.
- The in-game voice icons don't light up; the HUD line and the client's list replace them.

## Building

```sh
dotnet publish plugin/CS2VoiceRelay.csproj -c Release -o out   # needs the .NET 10 SDK
pip install -r client/requirements.txt pyinstaller
pyinstaller --onefile --windowed --name CS2VoiceRelay client/voiceclient.py
```

CI builds both on every push and attaches them to a GitHub Release on `v*` tags.

## License

GPL-3.0. The protocol is documented at the top of `plugin/Protocol.cs`.
