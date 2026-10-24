// CS2VoiceRelay browser player. Receives the Opus voice packets the server would have sent
// your game, decodes them with opus-decoder (WebAssembly, works on plain http) and plays
// them through Web Audio. Volume settings are kept in localStorage.
(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const store = {
    get(key, fallback) {
      try { const v = localStorage.getItem(key); return v === null ? fallback : JSON.parse(v); } catch { return fallback; }
    },
    set(key, value) { try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* private mode */ } },
  };

  const JITTER = 0.08;          // seconds of buffer before a talk spurt starts
  const SPEAKING_MS = 350;

  const st = {
    ws: null,
    backoff: 500,
    me: null,
    players: new Map(),       // steamid -> {name, team, alive}
    ctx: null,
    master: null,
    listening: false,
    speakers: new Map(),      // steamid -> {gain, dec, ready, queue, nextTime, last}
    warnedFormat: false,
  };

  let masterVolume = store.get("cvr.master", 100);
  let masterMuted = store.get("cvr.masterMuted", false);
  const prefs = store.get("cvr.players", {});   // steamid -> {vol, muted}

  // ------------------------------------------------------------------ connection

  function wsUrl() {
    const base = location.pathname.replace(/[^/]*$/, "");
    return (location.protocol === "https:" ? "wss://" : "ws://") + location.host + base + "ws";
  }

  function setConn(text, cls) {
    const el = $("conn");
    el.textContent = text;
    el.className = "pill " + (cls || "");
  }

  function connect() {
    setConn("Connecting", "warn");
    const ws = new WebSocket(wsUrl());
    ws.binaryType = "arraybuffer";
    st.ws = ws;
    ws.onopen = () => {
      st.backoff = 500;
      if (st.listening) send({ t: "ready", on: true });
    };
    ws.onmessage = (ev) => {
      if (typeof ev.data === "string") onJson(JSON.parse(ev.data));
      else onVoice(ev.data);
    };
    ws.onclose = () => {
      setConn("Reconnecting", "bad");
      setTimeout(connect, st.backoff);
      st.backoff = Math.min(st.backoff * 2, 8000);
    };
  }

  function send(obj) {
    if (st.ws && st.ws.readyState === WebSocket.OPEN) st.ws.send(JSON.stringify(obj));
  }

  function show(section) {
    for (const id of ["identify-none", "identify-choose", "main"]) $(id).classList.toggle("hidden", id !== section);
  }

  function onJson(m) {
    switch (m.t) {
      case "hello":
        $("version").textContent = "v" + m.version;
        break;
      case "identify":
        if (m.status === "none") {
          show("identify-none");
          $("your-ip").textContent = m.ip ? "Looking for a player connected from " + m.ip + "." : "";
          setConn("Not in game", "warn");
        } else if (m.status === "choose") {
          show("identify-choose");
          renderChoices(m.players);
          setConn("Confirm player", "warn");
        }
        break;
      case "code_sent":
        $("code-row").classList.remove("hidden");
        $("code-hint").textContent = "A 4-digit code was sent to " + m.name + "'s in-game chat.";
        $("code-error").textContent = "";
        $("code").focus();
        break;
      case "code_result":
        $("code-error").textContent = m.msg || "";
        break;
      case "bound":
        st.me = m.id;
        $("me").textContent = m.name;
        show("main");
        updateConn();
        break;
      case "state":
        onState(m);
        break;
    }
  }

  function updateConn() {
    if (!st.me) return;
    if (st.listening) setConn("Listening", "ok");
    else setConn("Linked", "warn");
  }

  // ------------------------------------------------------------------ identify UI

  function renderChoices(players) {
    const box = $("choices");
    box.replaceChildren();
    $("code-row").classList.add("hidden");
    $("code-hint").textContent = "";
    for (const p of players) {
      const b = document.createElement("button");
      b.textContent = p.name;
      b.onclick = () => send({ t: "choose", id: p.id });
      box.appendChild(b);
    }
  }

  $("code-go").onclick = () => send({ t: "code", code: $("code").value.trim() });
  $("code").addEventListener("keydown", (e) => { if (e.key === "Enter") $("code-go").click(); });

  // ------------------------------------------------------------------ roster

  function onState(m) {
    $("host").textContent = m.host || "CS2 server";
    $("map").textContent = m.map || "";
    $("offline").classList.toggle("hidden", !st.me || m.online);
    st.players = new Map(m.players.map((p) => [p.id, p]));
    renderPlayers();
  }

  function pref(id) {
    return prefs[id] || { vol: 100, muted: false };
  }

  function savePref(id, p) {
    prefs[id] = p;
    store.set("cvr.players", prefs);
    const s = st.speakers.get(id);
    if (s) applyGain(id, s);
  }

  function renderPlayers() {
    const list = $("players");
    const existing = new Map([...list.children].map((el) => [el.dataset.id, el]));
    let index = 0;
    for (const [id, p] of st.players) {
      let row = existing.get(id);
      if (!row) row = createRow(id);
      existing.delete(id);
      row.className = "player team-" + p.team + (p.alive || p.team < 2 ? "" : " dead") + (id === st.me ? " self" : "");
      row.querySelector(".name-text").textContent = p.name;
      row.querySelector("small").textContent = id === st.me ? "you" : (p.team >= 2 && !p.alive ? "dead" : "");
      for (const el of row.querySelectorAll(".ctl")) el.classList.toggle("hidden", id === st.me);
      if (list.children[index] !== row) list.insertBefore(row, list.children[index] || null);
      index++;
    }
    for (const el of existing.values()) el.remove();
  }

  function createRow(id) {
    const row = document.createElement("div");
    row.dataset.id = id;
    const dot = document.createElement("span");
    dot.className = "dot";
    const name = document.createElement("span");
    name.className = "name";
    const text = document.createElement("span");
    text.className = "name-text";
    name.append(text, document.createElement("small"));

    const p = pref(id);
    const slider = document.createElement("input");
    slider.type = "range"; slider.min = "0"; slider.max = "200"; slider.value = String(p.vol);
    slider.className = "ctl";
    slider.setAttribute("aria-label", "Volume");
    const value = document.createElement("span");
    value.className = "value ctl";
    value.textContent = p.vol + "%";
    slider.oninput = () => {
      const cur = pref(id);
      value.textContent = slider.value + "%";
      savePref(id, { vol: Number(slider.value), muted: cur.muted });
    };
    const mute = document.createElement("button");
    mute.className = "icon ctl" + (p.muted ? " on" : "");
    mute.textContent = p.muted ? "Off" : "On";
    mute.title = "Mute this player";
    mute.onclick = () => {
      const cur = pref(id);
      const muted = !cur.muted;
      mute.classList.toggle("on", muted);
      mute.textContent = muted ? "Off" : "On";
      savePref(id, { vol: cur.vol, muted });
    };
    row.append(dot, name, slider, value, mute);
    return row;
  }

  setInterval(() => {
    const now = performance.now();
    for (const row of $("players").children) {
      const s = st.speakers.get(row.dataset.id);
      row.classList.toggle("speaking", !!s && now - s.last < SPEAKING_MS);
    }
  }, 120);

  // ------------------------------------------------------------------ audio

  function masterGainValue() {
    return masterMuted ? 0 : masterVolume / 100;
  }

  function applyMaster() {
    $("master").value = String(masterVolume);
    $("master-value").textContent = masterVolume + "%";
    $("master-mute").classList.toggle("on", masterMuted);
    $("master-mute").textContent = masterMuted ? "Muted" : "Mute";
    if (st.master) st.master.gain.setTargetAtTime(masterGainValue(), st.ctx.currentTime, 0.015);
  }

  function applyGain(id, s) {
    const p = pref(id);
    s.gain.gain.setTargetAtTime(p.muted ? 0 : p.vol / 100, st.ctx.currentTime, 0.015);
  }

  $("master").oninput = () => {
    masterVolume = Number($("master").value);
    store.set("cvr.master", masterVolume);
    applyMaster();
  };
  $("master-mute").onclick = () => {
    masterMuted = !masterMuted;
    store.set("cvr.masterMuted", masterMuted);
    applyMaster();
  };
  applyMaster();

  async function startListening() {
    if (!st.ctx) {
      st.ctx = new (window.AudioContext || window.webkitAudioContext)({ latencyHint: "interactive" });
      st.master = st.ctx.createGain();
      st.master.gain.value = masterGainValue();
      st.master.connect(st.ctx.destination);
    }
    await st.ctx.resume();
    st.listening = true;
    send({ t: "ready", on: true });
    $("listen").textContent = "Stop listening";
    $("listen").classList.remove("primary");
    $("listen-hint").textContent = "Listening. You can switch back to CS2; this tab keeps playing in the background.";
    updateConn();
  }

  function stopListening() {
    st.listening = false;
    send({ t: "ready", on: false });
    for (const s of st.speakers.values()) s.nextTime = 0;
    $("listen").textContent = "Start listening";
    $("listen").classList.add("primary");
    $("listen-hint").textContent = "In-game voice comes back within a couple of seconds after you stop.";
    updateConn();
  }

  $("listen").onclick = () => (st.listening ? stopListening() : startListening());

  function speaker(id) {
    let s = st.speakers.get(id);
    if (s) return s;
    const gain = st.ctx.createGain();
    gain.connect(st.master);
    const dec = new window["opus-decoder"].OpusDecoder({
      channels: 1, streamCount: 1, coupledStreamCount: 0, channelMappingTable: [0], sampleRate: 48000,
    });
    s = { gain, dec, ready: false, queue: [], nextTime: 0, last: 0 };
    dec.ready.then(() => {
      s.ready = true;
      for (const f of s.queue) play(s, f);
      s.queue = [];
    });
    st.speakers.set(id, s);
    applyGain(id, s);
    return s;
  }

  // packet_offsets are frame positions in voice_data; accept start offsets, end offsets
  // or plain frame lengths, whichever this build sends.
  function splitFrames(data, offsets) {
    const len = data.length;
    if (offsets.length === 0) return [data];
    const sum = offsets.reduce((a, b) => a + b, 0);
    let cuts;
    const increasing = offsets.every((o, i) => i === 0 || o > offsets[i - 1]);
    if (increasing && offsets[offsets.length - 1] <= len) {
      cuts = [...new Set([0, ...offsets, len])].filter((o) => o >= 0 && o <= len).sort((a, b) => a - b);
    } else if (sum === len) {
      cuts = [0];
      for (const o of offsets) cuts.push(cuts[cuts.length - 1] + o);
    } else {
      return [data];
    }
    const frames = [];
    for (let i = 0; i + 1 < cuts.length; i++) if (cuts[i + 1] > cuts[i]) frames.push(data.subarray(cuts[i], cuts[i + 1]));
    return frames;
  }

  function onVoice(buf) {
    if (!st.listening || !st.ctx || buf.byteLength < 16) return;
    const dv = new DataView(buf);
    if (dv.getUint8(0) !== 1) return;
    const id = dv.getBigUint64(1, true).toString();
    const format = dv.getUint8(9);
    const count = dv.getUint16(14, true);
    const offsets = [];
    for (let i = 0; i < count; i++) offsets.push(dv.getUint32(16 + i * 4, true));
    const data = new Uint8Array(buf, 16 + count * 4);
    if (format !== 2) {
      if (!st.warnedFormat) {
        st.warnedFormat = true;
        console.warn("CS2VoiceRelay: unsupported voice format", format);
      }
      return;
    }
    const s = speaker(id);
    s.last = performance.now();
    for (const frame of splitFrames(data, offsets)) {
      if (s.ready) play(s, frame);
      else if (s.queue.length < 50) s.queue.push(frame);
    }
  }

  function play(s, frame) {
    let out;
    try { out = s.dec.decodeFrame(frame); } catch { return; }
    if (!out || !out.samplesDecoded) return;
    const ctx = st.ctx;
    const audio = ctx.createBuffer(1, out.samplesDecoded, out.sampleRate);
    audio.copyToChannel(out.channelData[0], 0);
    const src = ctx.createBufferSource();
    src.buffer = audio;
    src.connect(s.gain);
    const now = ctx.currentTime;
    // New talk spurt, or drifted too far ahead: restart the small playout buffer
    if (s.nextTime < now + 0.01 || s.nextTime > now + 0.6) s.nextTime = now + JITTER;
    src.start(s.nextTime);
    s.nextTime += audio.duration;
  }

  connect();
})();
