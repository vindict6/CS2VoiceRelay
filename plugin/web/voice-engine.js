// CS2VoiceRelay voice engine. No UI: connects to the plugin, links the browser to its player,
// plays the voice the server would have sent their game, and keeps volume settings. Settings
// are saved per player on the game server (so they follow you to any browser) and cached in
// localStorage. app.js builds the standalone page on this; other sites can too.
//
//   const vr = CS2VoiceRelay.createEngine({ wsUrl: "/voice/ws" });   // default: "ws" next to the page
//   vr.on("status", s => ...)   s.phase: connecting | waiting | choose | linked | reconnecting
//                               s.ip (waiting), s.choices (choose), s.me, s.name (linked)
//   vr.on("state", st => ...)   st.map, st.host, st.online, st.players [{id, name, team, alive}],
//                               st.board (scoreboard, or null when the server turns it off),
//                               st.tournament (server in tournament mode), st.allVoice (you hear every player)
//   vr.getSolo() -> 0 | 2 | 3;  vr.setSolo(team)   with allVoice: hear everyone (0), only T (2) or only CT (3)
//   vr.on("prefs", () => ...)   volume settings changed (from the server or this page)
//   vr.on("listening", on => ...), vr.on("code_sent", name => ...), vr.on("code_result", msg => ...)
//   await vr.start()            call from a click: browsers only allow audio after a user gesture
//   vr.stop(); vr.choose(id); vr.submitCode(code)
//   vr.getMaster() -> {volume, muted};  vr.setMaster({volume, muted})
//   vr.getPlayer(id) -> {vol, muted};   vr.setPlayer(id, {vol, muted})
//   vr.isSpeaking(id)
(function (global) {
  "use strict";

  const JITTER = 0.08;          // seconds of buffer before a talk spurt starts
  const SPEAKING_MS = 350;

  const store = {
    get(key, fallback) {
      try { const v = localStorage.getItem(key); return v === null ? fallback : JSON.parse(v); } catch { return fallback; }
    },
    set(key, value) { try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* private mode */ } },
  };

  function clampVolume(v) {
    v = Math.round(Number(v));
    return Number.isFinite(v) ? Math.min(200, Math.max(0, v)) : 100;
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

  function createEngine(options) {
    options = options || {};
    const handlers = {};
    const speakers = new Map();   // steamid -> {gain, dec, ready, queue, nextTime, last}
    let ws = null;
    let backoff = 500;
    let closed = false;
    let ctx = null;
    let masterGain = null;
    let warnedFormat = false;

    // Volume settings: localStorage until the server sends this player's saved copy
    let master = { volume: clampVolume(store.get("cvr.master", 100)), muted: !!store.get("cvr.masterMuted", false) };
    let players = store.get("cvr.players", {}) || {};
    let solo = [2, 3].includes(store.get("cvr.solo", 0)) ? store.get("cvr.solo", 0) : 0;
    const teamOf = new Map();     // steamid -> team, from the latest state

    const engine = {
      status: { phase: "connecting" },
      state: null,
      listening: false,
      version: "",

      on(event, fn) {
        (handlers[event] = handlers[event] || []).push(fn);
        return engine;
      },

      async start() {
        if (!ctx) {
          ctx = new (global.AudioContext || global.webkitAudioContext)({ latencyHint: "interactive" });
          masterGain = ctx.createGain();
          masterGain.gain.value = masterValue();
          masterGain.connect(ctx.destination);
        }
        await ctx.resume();
        engine.listening = true;
        send({ t: "ready", on: true });
        emit("listening", true);
      },

      stop() {
        engine.listening = false;
        send({ t: "ready", on: false });
        for (const s of speakers.values()) s.nextTime = 0;
        emit("listening", false);
      },

      choose(id) { send({ t: "choose", id: String(id) }); },
      submitCode(code) { send({ t: "code", code: String(code).trim() }); },

      getMaster() { return { volume: master.volume, muted: master.muted }; },
      setMaster(next) {
        master = {
          volume: next.volume === undefined ? master.volume : clampVolume(next.volume),
          muted: next.muted === undefined ? master.muted : !!next.muted,
        };
        saveLocal();
        applyMaster();
        send({ t: "pref", master: master.volume, masterMuted: master.muted });
        emit("prefs");
      },

      getPlayer(id) {
        const p = players[id];
        return { vol: p ? clampVolume(p.vol) : 100, muted: !!(p && p.muted) };
      },
      setPlayer(id, next) {
        id = String(id);
        const cur = engine.getPlayer(id);
        const p = {
          vol: next.vol === undefined ? cur.vol : clampVolume(next.vol),
          muted: next.muted === undefined ? cur.muted : !!next.muted,
        };
        if (p.vol === 100 && !p.muted) delete players[id];
        else players[id] = p;
        saveLocal();
        const s = speakers.get(id);
        if (s) applyGain(id, s);
        send({ t: "pref", id, vol: p.vol, muted: p.muted });
        emit("prefs");
      },

      getSolo() { return solo; },
      setSolo(team) {
        solo = [2, 3].includes(Number(team)) ? Number(team) : 0;
        store.set("cvr.solo", solo);
        emit("prefs");
      },

      isSpeaking(id) {
        const s = speakers.get(String(id));
        return !!s && performance.now() - s.last < SPEAKING_MS;
      },

      close() {
        closed = true;
        if (ws) ws.close();
      },
    };

    function emit(event, arg) {
      for (const fn of handlers[event] || []) {
        try { fn(arg); } catch (e) { console.error(e); }
      }
    }

    function setStatus(status) {
      engine.status = status;
      emit("status", status);
    }

    function wsUrl() {
      const target = options.wsUrl || location.pathname.replace(/[^/]*$/, "") + "ws";
      if (/^wss?:\/\//.test(target)) return target;
      return (location.protocol === "https:" ? "wss://" : "ws://") + location.host + target;
    }

    function connect() {
      if (closed) return;
      const socket = new WebSocket(wsUrl());
      socket.binaryType = "arraybuffer";
      ws = socket;
      socket.onopen = () => {
        backoff = 500;
        if (engine.listening) send({ t: "ready", on: true });
      };
      socket.onmessage = (ev) => {
        if (typeof ev.data === "string") onJson(JSON.parse(ev.data));
        else onVoice(ev.data);
      };
      socket.onclose = () => {
        if (closed) return;
        setStatus({ phase: "reconnecting" });
        setTimeout(connect, backoff);
        backoff = Math.min(backoff * 2, 8000);
      };
    }

    function send(obj) {
      if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
    }

    function onJson(m) {
      switch (m.t) {
        case "hello":
          engine.version = m.version || "";
          emit("hello", engine.version);
          break;
        case "identify":
          if (m.status === "none") setStatus({ phase: "waiting", ip: m.ip || "" });
          else if (m.status === "choose") setStatus({ phase: "choose", choices: m.players || [] });
          break;
        case "code_sent":
          emit("code_sent", m.name);
          break;
        case "code_result":
          emit("code_result", m.msg || "");
          break;
        case "bound":
          setStatus({ phase: "linked", me: m.id, name: m.name });
          break;
        case "prefs":
          onServerPrefs(m);
          break;
        case "state":
          engine.state = {
            map: m.map || "", host: m.host || "", online: !!m.online, players: m.players || [], board: m.board || null,
            tournament: !!m.tournament, allVoice: !!m.allVoice,
          };
          teamOf.clear();
          for (const p of engine.state.players) teamOf.set(p.id, p.team);
          emit("state", engine.state);
          break;
      }
    }

    // The server's saved copy wins; if it has nothing yet, upload what this browser has
    function onServerPrefs(m) {
      const local = { master: master.volume, masterMuted: master.muted, players };
      const localHasData = master.volume !== 100 || master.muted || Object.keys(players).length > 0;
      if (m.empty) {
        if (localHasData) send({ t: "prefs_set", master: local.master, masterMuted: local.masterMuted, players: local.players });
        return;
      }
      master = { volume: clampVolume(m.master), muted: !!m.masterMuted };
      players = {};
      for (const [id, p] of Object.entries(m.players || {})) players[id] = { vol: clampVolume(p.vol), muted: !!p.muted };
      saveLocal();
      applyMaster();
      for (const [id, s] of speakers) applyGain(id, s);
      emit("prefs");
    }

    function saveLocal() {
      store.set("cvr.master", master.volume);
      store.set("cvr.masterMuted", master.muted);
      store.set("cvr.players", players);
    }

    function masterValue() {
      return master.muted ? 0 : master.volume / 100;
    }

    function applyMaster() {
      if (masterGain) masterGain.gain.setTargetAtTime(masterValue(), ctx.currentTime, 0.015);
    }

    function applyGain(id, s) {
      const p = engine.getPlayer(id);
      s.gain.gain.setTargetAtTime(p.muted ? 0 : p.vol / 100, ctx.currentTime, 0.015);
    }

    function speaker(id) {
      let s = speakers.get(id);
      if (s) return s;
      const gain = ctx.createGain();
      gain.connect(masterGain);
      const dec = new global["opus-decoder"].OpusDecoder({
        channels: 1, streamCount: 1, coupledStreamCount: 0, channelMappingTable: [0], sampleRate: 48000,
      });
      s = { gain, dec, ready: false, queue: [], nextTime: 0, last: 0 };
      dec.ready.then(() => {
        s.ready = true;
        for (const f of s.queue) play(s, f);
        s.queue = [];
      });
      speakers.set(id, s);
      applyGain(id, s);
      return s;
    }

    function onVoice(buf) {
      if (!engine.listening || !ctx || buf.byteLength < 16) return;
      const dv = new DataView(buf);
      if (dv.getUint8(0) !== 1) return;
      const id = dv.getBigUint64(1, true).toString();
      const format = dv.getUint8(9);
      const count = dv.getUint16(14, true);
      const offsets = [];
      for (let i = 0; i < count; i++) offsets.push(dv.getUint32(16 + i * 4, true));
      const data = new Uint8Array(buf, 16 + count * 4);
      if (format !== 2) {
        if (!warnedFormat) {
          warnedFormat = true;
          console.warn("CS2VoiceRelay: unsupported voice format", format);
        }
        return;
      }
      const s = speaker(id);
      s.last = performance.now();
      // Team solo (tournament listeners): still shows who's talking, plays only the chosen team
      if (solo && engine.state && engine.state.allVoice && teamOf.get(id) !== solo) return;
      for (const frame of splitFrames(data, offsets)) {
        if (s.ready) play(s, frame);
        else if (s.queue.length < 50) s.queue.push(frame);
      }
    }

    function play(s, frame) {
      let out;
      try { out = s.dec.decodeFrame(frame); } catch { return; }
      if (!out || !out.samplesDecoded) return;
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
    return engine;
  }

  global.CS2VoiceRelay = { createEngine, splitFrames };
})(window);
