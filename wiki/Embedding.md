# Embedding the voice player in your site

You can put the voice player on any web page. Players open your site instead of the plugin's own
page, and everything else works the same: they're linked by IP, hear exactly what the game would
let them hear, and their volume settings are saved on the game server.

The plugin serves a ready-made widget at `/widget.js`. That's all you need.

![The widget](https://raw.githubusercontent.com/vindict6/CS2VoiceRelay/main/docs/widget.png)

## JavaScript

Paste this where you want the player to show up, with your server's address:

```html
<div id="cs2voice"></div>
<script src="http://your-server:27030/widget.js" data-title="Server voice"></script>
```

The widget loads everything else it needs from the same address and draws the player into the
`div`.

Options go on the script tag:

| Attribute | Default | What it does |
|---|---|---|
| `data-title` | `Voice` | Heading |
| `data-target` | `cs2voice` | `id` of the element to draw into. If it doesn't exist, the widget goes right after the script tag |
| `data-theme` | the visitor's system setting | `dark` or `light` |
| `data-accent` | `AccentColor` from the plugin config | Accent color, like `#3a8ee8` |
| `data-logo` | `Logo` from the plugin config | Logo image URL, or `none` to hide it |
| `data-players` | `true` | `false` hides the player list |

## PHP

Save this as `cs2voice.php` next to your pages:

```php
<?php
// Prints the CS2VoiceRelay voice player. $server is the plugin's address,
// $options are the widget's data- attributes without "data-", like ['title' => 'Voice'].
function cs2voice(string $server, array $options = []): string
{
    $target = $options['target'] ?? 'cs2voice';
    $attributes = '';
    foreach ($options as $name => $value) {
        $name = preg_replace('/[^a-z-]/', '', strtolower((string) $name));
        $attributes .= sprintf(' data-%s="%s"', $name, htmlspecialchars((string) $value, ENT_QUOTES));
    }
    return sprintf(
        '<div id="%s"></div><script src="%s"%s></script>',
        htmlspecialchars($target, ENT_QUOTES),
        htmlspecialchars(rtrim($server, '/') . '/widget.js', ENT_QUOTES),
        $attributes
    );
}
```

Then call it wherever the player should go:

```php
<?php require __DIR__ . '/cs2voice.php'; ?>
<!doctype html>
<html>
<head><meta charset="utf-8"><title>My community</title></head>
<body>
  <h1>My community</h1>
  <?= cs2voice('http://your-server:27030', ['title' => 'Server voice', 'theme' => 'dark']) ?>
</body>
</html>
```

## If your site uses HTTPS

Browsers won't load `http://` scripts or connect to `ws://` from an `https://` page. Use one of
these, and put that address in the snippets instead:

- **HTTPS on the plugin's port**: set `TlsCertificate` and `TlsPrivateKey` in the plugin config,
  then use `https://your-server:27030`.
- **A path on your own site**: proxy `/voice/` and the WebSocket at `/voice/ws` to the plugin
  (see the README's reverse proxy section), then use `https://your-site/voice`. Put your proxy
  in `TrustedProxies` so players are still matched by their own IP.

## Building your own player

The widget is built on `voice-engine.js`, which has no UI. If you'd rather draw everything
yourself, load the engine and use its events:

```html
<button id="listen" disabled>Start listening</button>
<span id="status">Connecting</span>
<script src="http://your-server:27030/opus-decoder.min.js"></script>
<script src="http://your-server:27030/voice-engine.js"></script>
<script>
  const vr = CS2VoiceRelay.createEngine({ wsUrl: "ws://your-server:27030/ws" });
  const button = document.getElementById("listen");
  const status = document.getElementById("status");
  vr.on("status", (s) => { status.textContent = s.phase === "linked" ? "Linked as " + s.name : s.phase; });
  vr.on("state", (st) => { button.disabled = !st.online && !vr.listening; });
  vr.on("listening", (on) => { button.textContent = on ? "Stop listening" : "Start listening"; });
  button.onclick = () => (vr.listening ? vr.stop() : vr.start());
</script>
```

The full API is listed at the top of
[`voice-engine.js`](https://github.com/vindict6/CS2VoiceRelay/blob/main/plugin/web/voice-engine.js):
per-player volume, mute, who's speaking, the scoreboard, and team solo in tournament mode.
