// CS2VoiceRelay standalone page, built on voice-engine.js (load that first).
//
// The page needs elements with these ids:
//   conn, identify-none, your-ip, identify-choose, choices, code-row, code, code-go,
//   code-hint, code-error, main, offline, host, map, me, listen, master, master-value,
//   master-mute, listen-hint, version, solo (tournament team solo, buttons with data-team), and for the scoreboard: scoreboard, status-line,
//   teams-line, board, board-rows, zeus-line
// It toggles the class "hidden" to show and hide sections, sets data-state="ok|warn|bad" on
// #conn and data-listening on #listen, and builds scoreboard rows from the classes team-N,
// dead, self, speaking, dot, pname, tag, vctl, value, on. #board gets "no-voice" until the
// page is linked to a player, which hides the Voice column.
(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const vr = window.CS2VoiceRelay.createEngine();
  let me = null;
  let lastMap = "";

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
    $("board").classList.toggle("no-voice", !me);
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

  // ------------------------------------------------------------------ scoreboard

  const TEAM = { 2: "T", 3: "CT", 1: "SPEC" };

  vr.on("state", (st) => {
    $("host").textContent = st.host || "CS2 server";
    $("map").textContent = st.map;
    lastMap = st.map;
    $("offline").classList.toggle("hidden", !me || st.online);
    $("solo").classList.toggle("hidden", !st.allVoice);
    renderBoard(st.board);
  });

  vr.on("prefs", () => {
    applyMaster();
    applySolo();
    for (const row of $("board-rows").children) if (row.dataset.id) syncRow(row);
  });

  function span(cls, text) {
    const el = document.createElement("span");
    if (cls) el.className = cls;
    el.textContent = text;
    return el;
  }

  // "---" markers that never break across lines
  const dash = () => span("nb", "---");

  // CompStats' status lines: "--- Status: Map: X | Warmup: No ---", "--- Humans: N | T Wins: a, CT Wins: b ---"
  function renderLines(board) {
    const map = document.createElement("b");
    map.textContent = lastMap || "-";
    const warmup = document.createElement("b");
    warmup.textContent = board.warmup ? "Yes" : "No";
    $("status-line").replaceChildren(dash(), " Status: Map: ", map, " | Warmup: ", warmup, " ", dash());

    const teams = $("teams-line");
    teams.replaceChildren(dash(), " Humans: ", document.createElement("b"), " | ", span("t", "T Wins: "), document.createElement("b"),
      ", ", span("ct", "CT Wins: "), document.createElement("b"), " ", dash());
    const bold = teams.querySelectorAll("b");
    bold[0].textContent = board.humans;
    bold[1].textContent = board.tWins;
    bold[2].textContent = board.ctWins;

    // "--- Zeus Leader: Name (n Kills) ---", humans only, when the server turns it on
    let leader = null;
    if (board.zeusLeader) for (const r of board.rows) if (!r.bot && r.z > 0 && (!leader || r.z > leader.z)) leader = r;
    const zeus = $("zeus-line");
    zeus.classList.toggle("hidden", !leader);
    if (leader) {
      zeus.replaceChildren(dash(), " Zeus Leader: ", document.createElement("b"), " (" + leader.z + (leader.z === 1 ? " Kill" : " Kills") + ") ", dash());
      zeus.querySelector("b").textContent = leader.name;
    }
  }

  function renderBoard(board) {
    $("scoreboard").classList.toggle("hidden", !board);
    if (!board) return;
    renderLines(board);

    // CT, then T, then spectators, each under its own heading and sorted by score
    const byScore = (a, b) => b.score - a.score || b.k - a.k || a.d - b.d || a.name.localeCompare(b.name);
    const groups = [
      { team: 3, title: "Counter-Terrorists", wins: board.ctWins, rows: board.rows.filter((r) => r.team === 3) },
      { team: 2, title: "Terrorists", wins: board.tWins, rows: board.rows.filter((r) => r.team === 2) },
      { team: 1, title: "Spectators", rows: board.rows.filter((r) => r.team !== 2 && r.team !== 3) },
    ].filter((g) => g.rows.length);
    const body = $("board-rows");
    const existing = new Map([...body.children].map((el) => [el.dataset.key, el]));
    const rounds = Math.max(board.round || 1, 1);
    let index = 0;
    const rows = [];

    const place = (row) => {
      if (body.children[index] !== row) body.insertBefore(row, body.children[index] || null);
      index++;
    };

    for (const g of groups) {
      const key = "head:" + g.team;
      let head = existing.get(key);
      if (!head) {
        head = document.createElement("tr");
        head.dataset.key = key;
        const td = document.createElement("td");
        td.colSpan = 19;
        head.appendChild(td);
      }
      existing.delete(key);
      head.className = "team-head head-" + g.team;
      const wins = g.wins === undefined ? null : span("wins", g.wins + (g.wins === 1 ? " win" : " wins"));
      head.cells[0].replaceChildren(g.title + " (" + g.rows.length + ")", ...(wins ? [wins] : []));
      place(head);
      for (const r of [...g.rows].sort(byScore)) {
        rows.push(r);
        renderRow(r);
      }
    }

    function renderRow(p) {
      const key = p.id || "bot:" + p.slot + ":" + p.name;
      let row = existing.get(key);
      if (!row) row = createRow(key, p.id);
      existing.delete(key);

      row.className = "team-" + p.team + (p.alive || p.team < 2 ? "" : " dead") + (p.id && p.id === me ? " self" : "");
      const c = row.cells;
      c[0].textContent = "[" + p.slot + "]";
      row.querySelector(".pname").textContent = p.name;
      row.querySelector(".tag").textContent = p.id && p.id === me ? "you" : p.bot ? "bot" : "";
      c[2].textContent = TEAM[p.team] || "None";
      c[3].textContent = p.k;
      c[4].textContent = p.d;
      c[5].textContent = p.a;
      c[6].textContent = p.k > 0 ? (p.hs * 100 / p.k).toFixed(1) + "%" : "0.0%";
      c[7].textContent = p.dmg;
      c[8].textContent = p.ud;
      c[9].textContent = p.ef;
      c[10].textContent = (p.d > 0 ? p.k / p.d : p.k).toFixed(2);
      c[11].textContent = (p.dmg / rounds).toFixed(1);
      c[12].textContent = p.z;
      c[13].textContent = p.mvp;
      c[14].textContent = p.score;
      c[15].textContent = "$" + p.money;
      c[16].textContent = p.alive ? "Yes" : "No";
      c[16].className = p.team >= 2 ? (p.alive ? "yes" : "no") : "";
      c[17].textContent = p.bot ? "BOT" : p.ping;

      const ctl = row.querySelector(".vctl");
      if (ctl) ctl.classList.toggle("hidden", p.id === me);
      place(row);
    }
    for (const el of existing.values()) el.remove();

    if (!rows.length) {
      const empty = document.createElement("tr");
      empty.className = "empty";
      empty.dataset.key = "empty";
      const td = document.createElement("td");
      td.colSpan = 19;
      td.textContent = "No players on the server.";
      empty.appendChild(td);
      body.appendChild(empty);
    }
  }

  function createRow(key, id) {
    const row = document.createElement("tr");
    row.dataset.key = key;
    if (id) row.dataset.id = id;
    for (let i = 0; i < 19; i++) row.appendChild(document.createElement("td"));
    const c = row.cells;
    c[0].className = "l slot";
    c[1].className = "l";
    c[2].className = "l team";
    c[18].className = "l voice-col";

    const cell = document.createElement("div");
    cell.className = "player-cell";
    cell.append(span("dot", ""), span("pname", ""), span("tag", ""));
    c[1].appendChild(cell);

    if (id) {
      // Voice: this listener's volume and mute for the player, remembered on the server
      const ctl = document.createElement("div");
      ctl.className = "vctl";
      const slider = document.createElement("input");
      slider.type = "range"; slider.min = "0"; slider.max = "200";
      slider.setAttribute("aria-label", "Volume");
      const value = span("value", "");
      slider.oninput = () => {
        value.textContent = slider.value + "%";
        vr.setPlayer(id, { vol: Number(slider.value) });
      };
      const mute = document.createElement("button");
      mute.title = "Mute this player";
      mute.onclick = () => vr.setPlayer(id, { muted: !vr.getPlayer(id).muted });
      ctl.append(slider, value, mute);
      c[18].appendChild(ctl);
      syncRow(row);
    }
    return row;
  }

  function syncRow(row) {
    const p = vr.getPlayer(row.dataset.id);
    const slider = row.querySelector("input");
    if (!slider) return;
    if (document.activeElement !== slider) slider.value = String(p.vol);
    row.querySelector(".vctl .value").textContent = p.vol + "%";
    const mute = row.querySelector(".vctl button");
    mute.classList.toggle("on", p.muted);
    mute.textContent = p.muted ? "Off" : "On";
  }

  setInterval(() => {
    for (const row of $("board-rows").children)
      if (row.dataset.id) row.classList.toggle("speaking", vr.isSpeaking(row.dataset.id));
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

  // Tournament mode: hear everyone, or solo one team
  function applySolo() {
    for (const b of $("solo").querySelectorAll("button[data-team]"))
      b.setAttribute("aria-pressed", String(Number(b.dataset.team) === vr.getSolo()));
  }
  for (const b of $("solo").querySelectorAll("button[data-team]")) b.onclick = () => vr.setSolo(Number(b.dataset.team));
  applySolo();

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

  vr.on("test", () => {
    $("listen-hint").textContent = "Test chime from !testvoice. If you heard it, browser voice is working.";
  });
})();
