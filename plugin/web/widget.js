// CS2VoiceRelay embeddable widget. Put this on any page:
//
//   <div id="cs2voice"></div>
//   <script src="http://your-server:27030/widget.js"></script>
//
// It loads the decoder and voice engine from the same place, connects to the plugin and draws
// a small player: status, Start listening, volume, and who's talking with per-player volume.
// Options, as attributes on the script tag:
//   data-target="id"         element to draw into (default "cs2voice"; created after the script if missing)
//   data-title="Voice"       heading
//   data-theme="dark|light"  default: follows the visitor's system setting
//   data-accent="#e8a33a"    accent color (default: AccentColor from the plugin config)
//   data-logo="url|none"     logo image (default: Logo from the plugin config)
//   data-players="false"     hide the player list
(() => {
  "use strict";

  const script = document.currentScript;
  if (!script || !script.src) return;
  const base = new URL(".", script.src);
  const opt = script.dataset;
  const DEFAULT_ACCENT = "{{ACCENT}}";
  const DEFAULT_LOGO = "{{LOGO}}";

  let root = document.getElementById(opt.target || "cs2voice");
  if (!root) {
    root = document.createElement("div");
    root.id = opt.target || "cs2voice";
    script.after(root);
  }

  function load(src) {
    return new Promise((resolve, reject) => {
      const s = document.createElement("script");
      s.src = new URL(src, base).href;
      s.onload = resolve;
      s.onerror = () => reject(new Error("Couldn't load " + s.src));
      document.head.appendChild(s);
    });
  }

  const CSS = `
.cs2vr{--a:#e8a33a;--on-a:#1b1405;--bg:#181c22;--bg2:#1f242c;--line:#2a3039;--fg:#e8eaed;--dim:#9aa3ad;--ok:#63c46b;--bad:#e0625b;
  font:14px/1.45 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;color:var(--fg);background:var(--bg);border:1px solid var(--line);
  border-radius:12px;padding:14px;max-width:420px;box-sizing:border-box}
.cs2vr[data-theme=light]{--bg:#fff;--bg2:#f3f4f6;--line:#dfe3e8;--fg:#1d2329;--dim:#5f6b76}
.cs2vr *{box-sizing:border-box}
.cs2vr-head{display:flex;align-items:center;gap:8px;margin-bottom:6px}
.cs2vr-logo{width:24px;height:24px;border-radius:6px;object-fit:contain;flex:none}
.cs2vr-title{font-weight:700;font-size:15px;flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.cs2vr-pill{font-size:12px;padding:2px 9px;border-radius:999px;border:1px solid var(--line);color:var(--dim);white-space:nowrap}
.cs2vr-pill[data-s=ok]{color:var(--ok);border-color:var(--ok)}
.cs2vr-msg{color:var(--dim);font-size:13px;margin:0 0 10px}
.cs2vr button{font:inherit;cursor:pointer;color:var(--fg);background:var(--bg2);border:1px solid var(--line);border-radius:8px;padding:6px 12px}
.cs2vr button:disabled{opacity:.5;cursor:default}
.cs2vr .cs2vr-go{width:100%;padding:10px;background:var(--a);color:var(--on-a);border-color:transparent;font-weight:700}
.cs2vr .cs2vr-go[data-on]{background:var(--bg2);color:var(--fg);border-color:var(--line)}
.cs2vr input[type=range]{accent-color:var(--a);width:100%;margin:0}
.cs2vr-vol{display:grid;grid-template-columns:auto 1fr 42px auto;gap:8px;align-items:center;margin-top:10px}
.cs2vr-vol span:nth-child(3){color:var(--dim);text-align:right;font-variant-numeric:tabular-nums}
.cs2vr button.cs2vr-off{color:var(--bad)}
.cs2vr-list{list-style:none;margin:10px 0 0;padding:0;display:flex;flex-direction:column;gap:4px}
.cs2vr-list li{display:grid;grid-template-columns:10px 1fr 90px auto;gap:8px;align-items:center;padding:5px 8px;border-radius:8px;background:var(--bg2)}
.cs2vr-list li.me{grid-template-columns:10px 1fr}
.cs2vr-list li[data-team="2"] .cs2vr-name{color:#e6b25f}.cs2vr-list li[data-team="3"] .cs2vr-name{color:#7fb2e5}
.cs2vr-dot{width:8px;height:8px;border-radius:50%;background:var(--line)}
.cs2vr-list li.talking .cs2vr-dot{background:var(--ok);box-shadow:0 0 6px var(--ok)}
.cs2vr-name{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.cs2vr-list button{padding:2px 8px;font-size:12px}
.cs2vr-pick{display:flex;flex-wrap:wrap;gap:6px;margin-bottom:8px}
.cs2vr-code{display:flex;gap:6px;margin-bottom:8px}
.cs2vr-code input{font:inherit;width:90px;padding:5px 8px;border-radius:8px;border:1px solid var(--line);background:var(--bg2);color:var(--fg);letter-spacing:3px}
.cs2vr [hidden]{display:none!important}`;

  if (!document.getElementById("cs2vr-style")) {
    const style = document.createElement("style");
    style.id = "cs2vr-style";
    style.textContent = CSS;
    document.head.appendChild(style);
  }

  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    return e;
  }

  // ------------------------------------------------------------------ layout
  const box = el("div", "cs2vr");
  const accent = /^#[0-9a-f]{3}([0-9a-f]{3})?$/i.test(opt.accent || "") ? opt.accent
    : /^#[0-9a-f]{3}([0-9a-f]{3})?$/i.test(DEFAULT_ACCENT) ? DEFAULT_ACCENT : "#e8a33a";
  box.style.setProperty("--a", accent);
  box.style.setProperty("--on-a", textOn(accent));
  const theme = opt.theme || (matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark");
  box.dataset.theme = theme;

  const head = el("div", "cs2vr-head");
  const title = el("span", "cs2vr-title", opt.title || "Voice");
  const pill = el("span", "cs2vr-pill", "Connecting");
  const logoSrc = opt.logo === "none" ? "" : opt.logo || (DEFAULT_LOGO.startsWith("{{") ? "" : DEFAULT_LOGO);
  if (logoSrc) {
    const img = el("img", "cs2vr-logo");
    img.alt = "";
    img.src = new URL(logoSrc, base).href;
    head.append(img);
  }
  head.append(title, pill);
  const msg = el("p", "cs2vr-msg", "Connecting to the server…");
  const pick = el("div", "cs2vr-pick");
  pick.hidden = true;
  const codeRow = el("div", "cs2vr-code");
  const codeIn = el("input");
  codeIn.inputMode = "numeric";
  codeIn.maxLength = 4;
  codeIn.placeholder = "0000";
  codeIn.setAttribute("aria-label", "Code from your in-game chat");
  const codeGo = el("button", "", "Confirm");
  codeRow.append(codeIn, codeGo);
  codeRow.hidden = true;
  const go = el("button", "cs2vr-go", "Start listening");
  go.disabled = true;
  const vol = el("div", "cs2vr-vol");
  const volRange = el("input");
  volRange.type = "range"; volRange.min = "0"; volRange.max = "200";
  volRange.setAttribute("aria-label", "Volume");
  const volVal = el("span");
  const volMute = el("button", "", "Mute");
  vol.append(el("span", "", "Volume"), volRange, volVal, volMute);
  const list = el("ul", "cs2vr-list");
  list.hidden = opt.players === "false";
  box.append(head, msg, pick, codeRow, go, vol, list);
  root.replaceChildren(box);

  // Dark or light text on the accent, whichever reads better
  function textOn(hex) {
    let h = hex.slice(1);
    if (h.length === 3) h = h.replace(/./g, "$&$&");
    const [r, g, b] = [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16) / 255)
      .map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
    return 0.2126 * r + 0.7152 * g + 0.0722 * b > 0.3 ? "#14110a" : "#ffffff";
  }

  // ------------------------------------------------------------------ engine
  Promise.resolve()
    .then(() => (window["opus-decoder"] ? null : load("opus-decoder.min.js")))
    .then(() => (window.CS2VoiceRelay ? null : load("voice-engine.js")))
    .then(start)
    .catch((e) => {
      msg.textContent = "Couldn't reach the voice server.";
      pill.textContent = "Offline";
      console.error(e);
    });

  function start() {
    const ws = new URL("ws", base);
    ws.protocol = base.protocol === "https:" ? "wss:" : "ws:";
    const vr = window.CS2VoiceRelay.createEngine({ wsUrl: ws.href });
    let me = null;
    let online = false;
    let testUntil = 0;   // keep the !testvoice message up while the chime plays

    function refresh() {
      go.disabled = !(me && online) && !vr.listening;
      go.textContent = vr.listening ? "Stop listening" : "Start listening";
      if (vr.listening) go.dataset.on = ""; else delete go.dataset.on;
      if (vr.listening) { pill.textContent = "Listening"; pill.dataset.s = "ok"; }
      else if (me) { pill.textContent = online ? "Ready" : "Not in game"; delete pill.dataset.s; }
      if (me && Date.now() > testUntil) msg.textContent = !online ? "Join the server to listen." : vr.listening
        ? "Listening. Keep this tab open while you play." : "Click Start listening to hear voice chat here.";
      const m = vr.getMaster();
      if (document.activeElement !== volRange) volRange.value = String(m.volume);
      volVal.textContent = m.volume + "%";
      volMute.textContent = m.muted ? "Muted" : "Mute";
      volMute.classList.toggle("cs2vr-off", m.muted);
    }

    vr.on("status", (s) => {
      pick.hidden = s.phase !== "choose";
      if (s.phase !== "choose") codeRow.hidden = true;
      if (s.phase === "connecting" || s.phase === "reconnecting") {
        pill.textContent = s.phase === "connecting" ? "Connecting" : "Reconnecting";
        delete pill.dataset.s;
      } else if (s.phase === "waiting") {
        pill.textContent = "Not in game";
        msg.textContent = "Join the server and this links to you automatically.";
      } else if (s.phase === "choose") {
        pill.textContent = "Confirm player";
        msg.textContent = "More than one player shares your connection. Which one are you?";
        pick.replaceChildren(...s.choices.map((c) => {
          const b = el("button", "", c.name);
          b.onclick = () => vr.choose(c.id);
          return b;
        }));
      } else if (s.phase === "linked") {
        me = s.me;
      }
      refresh();
    });
    vr.on("code_sent", (name) => {
      codeRow.hidden = false;
      msg.textContent = "Enter the code sent to " + name + "'s in-game chat.";
      codeIn.focus();
    });
    vr.on("code_result", (text) => { msg.textContent = text; });
    codeGo.onclick = () => vr.submitCode(codeIn.value);
    codeIn.onkeydown = (e) => { if (e.key === "Enter") codeGo.click(); };

    vr.on("state", (st) => {
      online = st.online;
      renderPlayers(st.players);
      refresh();
    });
    vr.on("prefs", () => {
      refresh();
      for (const li of list.children) syncRow(li);
    });
    vr.on("listening", refresh);
    vr.on("test", () => {
      testUntil = Date.now() + 6000;
      msg.textContent = "Test chime from !testvoice. If you heard it, voice is working.";
    });

    go.onclick = () => (vr.listening ? vr.stop() : vr.start());
    volRange.oninput = () => vr.setMaster({ volume: Number(volRange.value) });
    volMute.onclick = () => vr.setMaster({ muted: !vr.getMaster().muted });

    function renderPlayers(players) {
      const old = new Map([...list.children].map((li) => [li.dataset.id, li]));
      players.forEach((p, i) => {
        let li = old.get(p.id);
        if (!li) li = makeRow(p.id);
        old.delete(p.id);
        li.dataset.team = p.team;
        li.classList.toggle("me", p.id === me);
        li.querySelector(".cs2vr-name").textContent = p.name + (p.id === me ? " (you)" : "");
        for (const c of li.querySelectorAll(".cs2vr-ctl")) c.hidden = p.id === me;
        if (list.children[i] !== li) list.insertBefore(li, list.children[i] || null);
      });
      for (const li of old.values()) li.remove();
    }

    function makeRow(id) {
      const li = el("li");
      li.dataset.id = id;
      const range = el("input", "cs2vr-ctl");
      range.type = "range"; range.min = "0"; range.max = "200";
      range.setAttribute("aria-label", "Player volume");
      range.oninput = () => vr.setPlayer(id, { vol: Number(range.value) });
      const mute = el("button", "cs2vr-ctl");
      mute.onclick = () => vr.setPlayer(id, { muted: !vr.getPlayer(id).muted });
      li.append(el("span", "cs2vr-dot"), el("span", "cs2vr-name"), range, mute);
      syncRow(li);
      return li;
    }

    function syncRow(li) {
      const p = vr.getPlayer(li.dataset.id);
      const range = li.querySelector("input");
      if (document.activeElement !== range) range.value = String(p.vol);
      const mute = li.querySelector("button");
      mute.textContent = p.muted ? "Unmute" : "Mute";
      mute.classList.toggle("cs2vr-off", p.muted);
    }

    setInterval(() => {
      for (const li of list.children) li.classList.toggle("talking", vr.isSpeaking(li.dataset.id));
    }, 150);

    root.cs2voice = vr;   // the engine, for pages that want more
  }
})();
