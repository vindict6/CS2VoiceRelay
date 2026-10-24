# CS2VoiceRelay

Hear in-game voice in your browser when CS2 stops playing it.

CS2's own voice playback breaks on the **client** after a Workshop map unloads: switching
between Workshop maps, rejoining a server that's on one, or going back to a stock map leaves
players unable to hear anyone until they restart the game. Valve has tracked this since January
2025 ([csgo-osx-linux#3980](https://github.com/ValveSoftware/csgo-osx-linux/issues/3980)): the
client deletes the shared sound all incoming voice plays through and never recreates it.
Nothing a server sends can repair that.

Talking still works; only hearing breaks. So CS2VoiceRelay leaves the microphone side alone and
fixes the receiving side: the server streams the voice it would have sent you to a web page you
keep open next to the game.

- Players talk in game exactly as before, with their normal push-to-talk key.
- Open the page (`http://your-server:27030`), click **Start listening**, and switch back to CS2.
- The page waits until you've joined the server, recognises you by your IP, and plays exactly
  the voice your game would have received: team rules, `sv_alltalk`, dead talk, mutes and every
  other routing decision are the engine's own.
- While the page is listening, your game gets the same voice packets with the audio replaced by
  silence: CS2 still shows who's talking (speaking icons, scoreboard), but you only hear it in the
  browser, never twice. Close the tab and normal in-game voice is back within two seconds.
- Volume control for everyone and per player, remembered between visits. No install, no
  microphone permission, works over plain HTTP in any modern browser.

## How it works

```
player's game ──voice──▶ CS2 server ──svc_VoiceData──▶ your game (playback broken)
                              │
               CS2VoiceRelayTap (Metamod) copies it after the engine's routing
                              │ localhost UDP
               CS2VoiceRelay (CounterStrikeSharp) ──WebSocket──▶ your browser (Opus, WebAssembly)
```

- **`native/`, CS2VoiceRelayTap**: a Metamod plugin hooking `CServerSideClient::SendNetMessage`.
  For every `svc_VoiceData` the engine sends a player, it forwards the speaker's SteamID and the
  Opus audio to `127.0.0.1:27031`. For players who are listening in a browser it swaps the
  in-game copy's audio for Opus silence frames (or drops it), keeping CS2's speaking icons without
  doubling the audio. The audio bytes are swapped in place and restored after each send, so no
  memory changes hands with the engine. That instruction expires after two seconds unless the
  plugin keeps renewing it, so in-game voice can't stay switched off if the plugin stops.
- **`plugin/`, CS2VoiceRelay**: a CounterStrikeSharp plugin that serves the page and a WebSocket
  on TCP 27030, matches each browser to a player by IP, and forwards that player's voice to it.
  When several players share one IP, the page asks which one you are and confirms with a code
  sent to that player's chat.
- **`plugin/web/`**: the page; Opus is decoded with
  [opus-decoder](https://github.com/eshaz/wasm-audio-decoders) (WebAssembly, MIT).

## Install (server)

Requires Metamod:Source 2.0 (dev build 1461 or later) and
[CounterStrikeSharp](https://docs.cssharp.dev/) v1.0.378 or newer.

1. Download the archive for your server's OS from [Releases](../../releases) and extract it into
   `game/csgo/`. It contains:
   - `addons/counterstrikesharp/plugins/CS2VoiceRelay/`
   - `addons/cs2voicerelay_tap/`
   - `addons/metamod/cs2voicerelay_tap.vdf`
2. If you used the older CS2VoiceFix plugin, remove `addons/cs2voicefix/` and
   `addons/metamod/cs2voicefix.vdf`; it hooks the same function and isn't needed.
3. Open **TCP 27030** in the server's firewall, e.g. on firewalld:
   `sudo firewall-cmd --permanent --add-port=27030/tcp && sudo firewall-cmd --reload`
4. Restart the server, then set `PublicUrl` in
   `addons/counterstrikesharp/configs/plugins/CS2VoiceRelay/CS2VoiceRelay.json`, so the link
   players see in chat is right.

| Key | Default | Meaning |
|---|---|---|
| `Port` | `27030` | TCP port of the web page |
| `BindAddress` | `0.0.0.0` | Interface to listen on |
| `PublicUrl` | `""` | Link shown to players, e.g. `http://example.com:27030` (default: `http://SERVER-IP:Port`) |
| `TapPort` | `27031` | Localhost UDP port between the Metamod tap and this plugin. The tap reads it from this file too and follows changes within 5 seconds; reload the plugin after changing it |
| `SuppressGameVoiceForListeners` | `true` | Hold back in-game voice audio for players listening in a browser, so they don't hear it twice |
| `ShowInGameSpeakingIndicators` | `true` | While holding it back, send those players silence instead of nothing, so CS2 still shows who's talking. `false` drops their in-game voice entirely |
| `TrustedProxies` | `[]` | Reverse proxies (IPs or CIDR ranges) whose `X-Forwarded-For` identifies the browser, e.g. `["127.0.0.1", "172.16.0.0/12"]` for a proxy on the same machine or in Docker. Empty means no proxy |
| `MaxConnectionsPerIp` | `6` | Browser connections allowed per player IP |
| `AnnounceOnJoin` | `true` | Show `JoinMessage` to players when they join. `false` (or an empty `JoinMessage`) turns the message off |
| `JoinMessage` | `Can't hear anyone after a map change? Open {lightblue}{url}{default} in your browser.` | The chat message. `{url}` is the `PublicUrl` link, `{name}` the player's name; color tags such as `{green}`, `{gold}`, `{lightblue}`, `{red}` and `{default}` are CounterStrikeSharp's chat colors |
| `JoinMessageIntervalMinutes` | `180` | Minimum minutes before the same player sees the message again. `0` shows it on every join and map change |
| `ReminderIntervalMinutes` | `0` | Repeat the message every N minutes to players who aren't listening in a browser. `0` is off |

Players can type `!voice` in chat to get the link again and see whether their browser is connected.

Each listener's master volume and per-player volume/mute settings are saved on the server,
keyed by their SteamID, in `configs/plugins/CS2VoiceRelay/voice_prefs.json`, so they follow the
player to any browser. Settings a browser already had are uploaded the first time it links.

Upgrading from 1.x: CounterStrikeSharp doesn't add new keys to an existing config, so delete
`CS2VoiceRelay.json` once and reload the plugin to get the 2.0 settings. To change the web port,
change `Port` and open the new TCP port in the firewall.

### Behind a reverse proxy

The page also works under a path on an existing site, e.g. `https://example.com/voice/`, which
also gives it HTTPS. Proxy both HTTP and the WebSocket at `/voice/ws` to the plugin, and list the
proxy's address in `TrustedProxies` so players are matched by their real IP (the plugin uses the
last `X-Forwarded-For` entry, the one your proxy adds). For Apache 2.4.47+ with `mod_proxy_http`:

```apache
ProxyPass        /voice/ws  http://127.0.0.1:27030/ws upgrade=websocket
ProxyPass        /voice/    http://127.0.0.1:27030/
ProxyPassReverse /voice/    http://127.0.0.1:27030/
```

If Apache runs in Docker, use `host.docker.internal` (with `extra_hosts:
["host.docker.internal:host-gateway"]`) instead of `127.0.0.1`, and trust the Docker range,
e.g. `"TrustedProxies": ["172.16.0.0/12"]`. The plugin's port then doesn't need to be open to
the internet at all.

### Embedding the player in your own site

The audio, linking and settings live in [`voice-engine.js`](plugin/web/voice-engine.js), which
has no UI, so you can build the player into your own pages. Proxy `/voice/ws`,
`/voice/voice-engine.js` and `/voice/opus-decoder.min.js` to the plugin, load the two scripts,
and create an engine:

```js
const vr = CS2VoiceRelay.createEngine({ wsUrl: "/voice/ws" });
vr.on("status", (s) => { /* s.phase: connecting | waiting | choose | linked | reconnecting */ });
vr.on("state", (st) => { /* st.players: [{ id, name, team, alive }] */ });
button.onclick = () => vr.start();          // audio needs a click first
vr.setPlayer(steamId, { vol: 50 });         // saved on the server for this listener
vr.isSpeaking(steamId);
```

The full API is listed at the top of `voice-engine.js`. The bundled page
([`plugin/web/index.html`](plugin/web/index.html) with [`app.js`](plugin/web/app.js)) is
built the same way.

## Notes and limitations

- Players are matched by IP. If someone's browser and game reach the server from different
  addresses (a VPN on one but not the other, or IPv6 for the page and IPv4 for the game), the
  page will keep waiting; open it with the server's IPv4 address in that case.
- The in-game speaking icons don't light up while the page is listening; the page shows who's
  talking instead.
- Audio adds roughly 80 ms of buffering on top of normal network latency.
- If a CS2 update changes `CServerSideClient`'s layout, the tap refuses to load (and logs why)
  rather than hooking the wrong function; the web page then stays silent until it's updated.

## Building

```sh
dotnet publish plugin/CS2VoiceRelay.csproj -c Release -o out   # .NET 10 SDK
cd native && xmake f -m release -y && xmake                     # needs HL2SDKCS2 and MMSOURCE112
```

CI builds both platforms on every push and publishes ready-to-extract archives on `v*` tags.

## License

GPL-3.0. Bundled opus-decoder is MIT; see `plugin/web/THIRD_PARTY.md`.
