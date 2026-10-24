// CS2VoiceRelay standalone page, built on voice-engine.js (load that first).
//
// The page needs elements with these ids:
//   conn, identify-none, your-ip, identify-choose, choices, code-row, code, code-go,
//   code-hint, code-error, main, offline, host, map, me, listen, master, master-value,
//   master-mute, listen-hint, players, version
// It toggles the class "hidden" to show and hide sections, sets data-state="ok|warn|bad" on
// #conn and data-listening on #listen, and builds player rows from the classes player,
// team-N, dead, self, speaking, dot, name, name-text, value, pbtn, ctl, on.
(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const vr = window.CS2VoiceRelay.createEngine();
  let me = null;

  function setConn(text, state) {
    const el = $("conn");
    el.textContent = text;
    el.dataset.state = state || "";
  }

  function show(section) {
    for (const id of ["identify-none", "identify-choose", "main"]) $(id).classList.toggle("hidden", id !== section);
  }

  function updateConn() {
    if (!me) return;
    if (vr.listening) setConn("Listening", "ok");
    else setConn("Linked", "warn");
  }

  vr.on("hello", (version) => { $("version").textContent = "v" + version; });

  vr.on("status", (s) => {
    switch (s.phase) {
      case "connecting":
        setConn("Connecting", "warn");
        break;
      case "reconnecting":
        setConn("Reconnecting", "bad");
        break;
      case "waiting":
        show("identify-none");
        $("your-ip").textContent = s.ip ? "Looking for a player connected from " + s.ip + "." : "";
        setConn("Not in game", "warn");
        break;
      case "choose":
        show("identify-choose");
        renderChoices(s.choices);
        setConn("Confirm player", "warn");
        break;
      case "linked":
        me = s.me;
        $("me").textContent = s.name;
        show("main");
        updateConn();
        break;
    }
  });

  // ------------------------------------------------------------------ identify UI

  function renderChoices(players) {
    const box = $("choices");
    box.replaceChildren();
    $("code-row").classList.add("hidden");
    $("code-hint").textContent = "";
    for (const p of players) {
      const b = document.createElement("button");
      b.textContent = p.name;
      b.onclick = () => vr.choose(p.id);
      box.appendChild(b);
    }
  }

  vr.on("code_sent", (name) => {
    $("code-row").classList.remove("hidden");
    $("code-hint").textContent = "A 4-digit code was sent to " + name + "'s in-game chat.";
    $("code-error").textContent = "";
    $("code").focus();
  });
  vr.on("code_result", (msg) => { $("code-error").textContent = msg; });

  $("code-go").onclick = () => vr.submitCode($("code").value);
  $("code").addEventListener("keydown", (e) => { if (e.key === "Enter") $("code-go").click(); });

  // ------------------------------------------------------------------ roster

  vr.on("state", (st) => {
    $("host").textContent = st.host || "CS2 server";
    $("map").textContent = st.map;
    $("offline").classList.toggle("hidden", !me || st.online);
    renderPlayers(st.players);
  });

  vr.on("prefs", () => {
    applyMaster();
    for (const row of $("players").children) syncRow(row);
  });

  function renderPlayers(players) {
    const list = $("players");
    const existing = new Map([...list.children].map((el) => [el.dataset.id, el]));
    let index = 0;
    for (const p of players) {
      let row = existing.get(p.id);
      if (!row) row = createRow(p.id);
      existing.delete(p.id);
      row.className = "player team-" + p.team + (p.alive || p.team < 2 ? "" : " dead") + (p.id === me ? " self" : "");
      row.querySelector(".name-text").textContent = p.name;
      row.querySelector("small").textContent = p.id === me ? "you" : (p.team >= 2 && !p.alive ? "dead" : "");
      for (const el of row.querySelectorAll(".ctl")) el.classList.toggle("hidden", p.id === me);
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

    const slider = document.createElement("input");
    slider.type = "range"; slider.min = "0"; slider.max = "200";
    slider.className = "ctl";
    slider.setAttribute("aria-label", "Volume");
    const value = document.createElement("span");
    value.className = "value ctl";
    slider.oninput = () => {
      value.textContent = slider.value + "%";
      vr.setPlayer(id, { vol: Number(slider.value) });
    };
    const mute = document.createElement("button");
    mute.title = "Mute this player";
    mute.onclick = () => vr.setPlayer(id, { muted: !vr.getPlayer(id).muted });
    row.append(dot, name, slider, value, mute);
    syncRow(row);
    return row;
  }

  function syncRow(row) {
    const p = vr.getPlayer(row.dataset.id);
    const slider = row.querySelector("input");
    if (document.activeElement !== slider) slider.value = String(p.vol);
    row.querySelector(".value").textContent = p.vol + "%";
    const mute = row.querySelector("button");
    mute.className = "pbtn ctl" + (p.muted ? " on" : "") + (row.dataset.id === me ? " hidden" : "");
    mute.textContent = p.muted ? "Off" : "On";
  }

  setInterval(() => {
    for (const row of $("players").children) row.classList.toggle("speaking", vr.isSpeaking(row.dataset.id));
  }, 120);

  // ------------------------------------------------------------------ audio controls

  function applyMaster() {
    const m = vr.getMaster();
    if (document.activeElement !== $("master")) $("master").value = String(m.volume);
    $("master-value").textContent = m.volume + "%";
    $("master-mute").classList.toggle("on", m.muted);
    $("master-mute").textContent = m.muted ? "Muted" : "Mute";
  }

  $("master").oninput = () => {
    $("master-value").textContent = $("master").value + "%";
    vr.setMaster({ volume: Number($("master").value) });
  };
  $("master-mute").onclick = () => vr.setMaster({ muted: !vr.getMaster().muted });
  applyMaster();

  vr.on("listening", (on) => {
    $("listen").textContent = on ? "Stop listening" : "Start listening";
    if (on) $("listen").dataset.listening = "true";
    else delete $("listen").dataset.listening;
    $("listen").classList.toggle("primary", !on);
    $("listen-hint").textContent = on
      ? "Listening. You can switch back to CS2; this tab keeps playing in the background."
      : "In-game voice comes back within a couple of seconds after you stop.";
    updateConn();
  });

  $("listen").onclick = () => (vr.listening ? vr.stop() : vr.start());
})();
