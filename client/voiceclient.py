#!/usr/bin/env python3
"""
CS2VoiceRelay - voice client

Out-of-game voice for CS2 community servers. CS2's own voice playback stops working after a
workshop map unloads (Valve issue #3980) and only a game restart brings it back, so the
CS2VoiceRelay server plugin relays audio between these clients instead. The game is used only
for identity, team/alive state and the server's voice rules; you hear exactly what the server
would normally let you hear.

    python voiceclient.py                      # GUI
    python voiceclient.py --nogui              # terminal, server and token detected from CS2
    python voiceclient.py --server host:port --token XXXXXXXX --nogui

Auto-detect needs CS2 launched with  -netconport 2121  (Steam > CS2 > Properties > Launch Options).

Your SteamID and push-to-talk key are read from the local Steam install.
"""

import argparse
import collections
import json
import os
import queue
import re
import socket
import struct
import sys
import threading
import time

import numpy as np
import sounddevice as sd
from pynput import keyboard, mouse

APP_NAME = "CS2VoiceRelay"
APP_VERSION = "1.1.0"

MAGIC = b"CVR1"
T_HELLO, T_AUDIO, T_PING, T_PONG, T_BYE, T_WELCOME, T_SPEAK, T_STATE = 1, 2, 3, 4, 5, 6, 7, 8
TOKEN_LEN = 8
SAMPLE_RATE = 16000
FRAME = 320  # 20 ms
STEAMID64_BASE = 76561197960265728
DEFAULT_NETCON_PORT = 2121

RULE_ALLTALK, RULE_DEADTALK, RULE_FULL_ALLTALK, RULE_ENEMY_DEAD, RULE_ENEMY_LIVING, RULE_VOICE_DISABLED = 1, 2, 4, 8, 16, 32
FLAG_ALIVE, FLAG_LINKED, FLAG_SPEAKING, FLAG_CAN_HEAR, FLAG_SELF, FLAG_MUTED = 1, 2, 4, 8, 16, 32
TEAM_NAMES = {0: "Unassigned", 1: "Spectator", 2: "Terrorists", 3: "Counter-Terrorists"}


# ----------------------------------------------------------------------------- settings

def config_dir():
    base = os.environ.get("APPDATA") or os.path.expanduser("~/.config")
    path = os.path.join(base, APP_NAME)
    os.makedirs(path, exist_ok=True)
    return path


def load_settings():
    try:
        with open(os.path.join(config_dir(), "settings.json"), encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def save_settings(data):
    try:
        with open(os.path.join(config_dir(), "settings.json"), "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
    except OSError:
        pass


# ----------------------------------------------------------------------------- Steam lookup

def steam_path():
    if sys.platform == "win32":
        try:
            import winreg
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as k:
                return winreg.QueryValueEx(k, "SteamPath")[0]
        except OSError:
            pass
        for p in (r"C:\Program Files (x86)\Steam", r"C:\Program Files\Steam"):
            if os.path.isdir(p):
                return p
    for p in (os.path.expanduser("~/.steam/steam"), os.path.expanduser("~/.local/share/Steam")):
        if os.path.isdir(p):
            return p
    return None


def detect_steamid(steam):
    """Most recently used account from loginusers.vdf."""
    if not steam:
        return None
    try:
        text = open(os.path.join(steam, "config", "loginusers.vdf"), encoding="utf-8", errors="replace").read()
    except OSError:
        return None
    best, best_ts = None, -1
    for m in re.finditer(r'"(\d{17})"\s*\{(.*?)\}', text, re.S):
        sid, body = int(m.group(1)), m.group(2)
        if re.search(r'"MostRecent"\s*"1"', body):
            return sid
        ts = re.search(r'"timestamp"\s*"(\d+)"', body)
        ts = int(ts.group(1)) if ts else 0
        if ts > best_ts:
            best, best_ts = sid, ts
    return best


def detect_ptt_key(steam, steamid):
    """The key bound to +voicerecord in CS2's per-user binds."""
    if not steam or not steamid:
        return None
    account = steamid - STEAMID64_BASE
    path = os.path.join(steam, "userdata", str(account), "730", "local", "cfg", "cs2_user_keys_0_slot0.vcfg")
    try:
        text = open(path, encoding="utf-8", errors="replace").read()
    except OSError:
        return None
    m = re.search(r'"([^"]+)"\s+"\+voicerecord"', text)
    return m.group(1) if m else None


# ----------------------------------------------------------------------------- push-to-talk

NAMED_KEYS = {
    "SPACE": keyboard.Key.space, "ENTER": keyboard.Key.enter, "TAB": keyboard.Key.tab,
    "BACKSPACE": keyboard.Key.backspace, "ESCAPE": keyboard.Key.esc, "CAPSLOCK": keyboard.Key.caps_lock,
    "SHIFT": keyboard.Key.shift, "RSHIFT": keyboard.Key.shift_r, "CTRL": keyboard.Key.ctrl,
    "RCTRL": keyboard.Key.ctrl_r, "ALT": keyboard.Key.alt, "RALT": keyboard.Key.alt_r,
    "INS": keyboard.Key.insert, "DEL": keyboard.Key.delete, "HOME": keyboard.Key.home, "END": keyboard.Key.end,
    "PGUP": keyboard.Key.page_up, "PGDN": keyboard.Key.page_down,
    "UPARROW": keyboard.Key.up, "DOWNARROW": keyboard.Key.down, "LEFTARROW": keyboard.Key.left,
    "RIGHTARROW": keyboard.Key.right, "SCROLLLOCK": keyboard.Key.scroll_lock, "NUMLOCK": keyboard.Key.num_lock,
    "PAUSE": keyboard.Key.pause, "KP_ENTER": keyboard.Key.enter,
}
MOUSE_BUTTONS = {
    "MOUSE1": mouse.Button.left, "MOUSE2": mouse.Button.right, "MOUSE3": mouse.Button.middle,
    "MOUSE4": getattr(mouse.Button, "x1", None), "MOUSE5": getattr(mouse.Button, "x2", None),
}
KEYPAD_VK = {  # Windows virtual-key codes for the numpad names CS2 uses
    "KP_INS": 0x60, "KP_END": 0x61, "KP_DOWNARROW": 0x62, "KP_PGDN": 0x63, "KP_LEFTARROW": 0x64,
    "KP_5": 0x65, "KP_RIGHTARROW": 0x66, "KP_HOME": 0x67, "KP_UPARROW": 0x68, "KP_PGUP": 0x69,
    "KP_MULTIPLY": 0x6A, "KP_PLUS": 0x6B, "KP_MINUS": 0x6D, "KP_DEL": 0x6E, "KP_SLASH": 0x6F,
}


class PushToTalk:
    """Global hook for one key or mouse button (CS2 key names). `active` is True while transmitting."""

    def __init__(self, name, toggle=False, on_change=None):
        self.name = (name or "").upper()
        self.toggle = toggle
        self.active = False
        self.on_change = on_change
        self._kb = None
        self._mouse = None

    @staticmethod
    def supported(name):
        n = (name or "").upper()
        if n in MOUSE_BUTTONS:
            return MOUSE_BUTTONS[n] is not None
        return n in NAMED_KEYS or n in KEYPAD_VK or bool(re.fullmatch(r"F\d{1,2}", n)) or len(n) == 1

    def start(self):
        n = self.name
        if n in MOUSE_BUTTONS and MOUSE_BUTTONS[n] is not None:
            button = MOUSE_BUTTONS[n]
            self._mouse = mouse.Listener(on_click=lambda x, y, b, pressed: self._edge(b == button, pressed))
            self._mouse.start()
            return
        if not self.supported(n):
            raise ValueError(f"Push-to-talk key '{n}' is not supported; choose another key")

        if n in NAMED_KEYS:
            target = NAMED_KEYS[n]
        elif re.fullmatch(r"F\d{1,2}", n):
            target = getattr(keyboard.Key, n.lower())
        elif n in KEYPAD_VK:
            target = keyboard.KeyCode.from_vk(KEYPAD_VK[n])
        else:
            target = keyboard.KeyCode.from_char(n.lower())

        def matches(key):
            if isinstance(target, keyboard.Key):
                if key == target:
                    return True
                if target in (keyboard.Key.shift, keyboard.Key.ctrl, keyboard.Key.alt):
                    return key in (getattr(keyboard.Key, target.name + "_l", None),
                                   getattr(keyboard.Key, target.name + "_r", None))
                return False
            if isinstance(key, keyboard.KeyCode):
                if target.vk is not None and key.vk is not None:
                    return key.vk == target.vk
                return key.char is not None and target.char is not None and key.char.lower() == target.char
            return False

        self._kb = keyboard.Listener(on_press=lambda k: self._edge(matches(k), True),
                                     on_release=lambda k: self._edge(matches(k), False))
        self._kb.start()

    def stop(self):
        for l in (self._kb, self._mouse):
            if l:
                l.stop()
        self._kb = self._mouse = None
        self._set(False)

    def _edge(self, is_target, pressed):
        if not is_target:
            return
        if self.toggle:
            if pressed:
                self._set(not self.active)
        else:
            self._set(pressed)

    def _set(self, value):
        if value != self.active:
            self.active = value
            if self.on_change:
                self.on_change(value)


# ----------------------------------------------------------------------------- client core

class Player:
    __slots__ = ("steamid", "name", "team", "alive", "linked", "speaking", "can_hear", "is_self", "muted")

    def __init__(self, steamid, name, team, flags):
        self.steamid = steamid
        self.name = name
        self.team = team
        self.alive = bool(flags & FLAG_ALIVE)
        self.linked = bool(flags & FLAG_LINKED)
        self.speaking = bool(flags & FLAG_SPEAKING)
        self.can_hear = bool(flags & FLAG_CAN_HEAR)
        self.is_self = bool(flags & FLAG_SELF)
        self.muted = bool(flags & FLAG_MUTED)


class GameConsole:
    """
    Reads CS2's console through its -netconport TCP console to find the server you're on and
    your relay token, so nothing has to be copied by hand. It runs `status` (the client prints
    "Client:  Connected [ip:port] ...") and the plugin's `css_voicerelay` command, whose reply is
    one line: "CS2VoiceRelay relay=HOST:PORT token=XXXXXXXX steamid=... status=... proto=1".

    Both patterns are anchored to the start of a line and the SteamID must be yours, so a
    player can't redirect your voice by typing a look-alike line in chat.
    """

    RE_CLIENT = re.compile(r"^\s*Client:\s+(.*)$")
    RE_ADDR = re.compile(r"(\d{1,3}(?:\.\d{1,3}){3}):(\d{1,5})")
    RE_RELAY = re.compile(r"^CS2VoiceRelay relay=(\S*):(\d{1,5}) token=([A-Z0-9]{8}) steamid=(\d{17}) status=(\w+)")

    def __init__(self, port, my_steamid=None):
        self.port = port
        self.my_steamid = my_steamid
        self.lock = threading.Lock()
        self.state = "searching"     # searching | connected
        self.in_game = None          # True/False once `status` has answered
        self.game_server = None      # "ip:port" from `status`
        self.relay_host = None       # from the plugin; "" means "same host as the game server"
        self.relay_port = None
        self.token = None
        self.steamid = None
        self.relay_status = None
        self.relay_for_server = None # game_server the relay line arrived for
        self._poll_now = threading.Event()
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()

    def close(self):
        self._stop.set()
        self._poll_now.set()

    def poll_now(self):
        self._poll_now.set()

    def snapshot(self):
        """(state, in_game, game_server, relay "host:port" or None, token, steamid, relay_status)"""
        with self.lock:
            relay = None
            fresh = self.relay_for_server == self.game_server or self.game_server is None
            if self.token and self.relay_port and fresh:
                host = self.relay_host or (self.game_server.split(":")[0] if self.game_server else "")
                if host:
                    relay = f"{host}:{self.relay_port}"
            return (self.state, self.in_game, self.game_server, relay,
                    self.token if fresh else None, self.steamid, self.relay_status)

    def _run(self):
        while not self._stop.is_set():
            try:
                sock = socket.create_connection(("127.0.0.1", self.port), timeout=2)
            except OSError:
                with self.lock:
                    self.state = "searching"
                self._poll_now.wait(3)
                self._poll_now.clear()
                continue
            with self.lock:
                self.state = "connected"
            sock.settimeout(0.5)
            buf = b""
            next_poll = 0.0
            try:
                while not self._stop.is_set():
                    now = time.monotonic()
                    if now >= next_poll or self._poll_now.is_set():
                        self._poll_now.clear()
                        sock.sendall(b"status\ncss_voicerelay\n")
                        with self.lock:
                            have_info = bool(self.token) and self.relay_for_server == self.game_server
                        next_poll = now + (20.0 if have_info else 5.0)
                    try:
                        data = sock.recv(65536)
                    except socket.timeout:
                        continue
                    if not data:
                        break
                    buf += data
                    *lines, buf = buf.split(b"\n")
                    for raw in lines:
                        self._parse(raw.decode("utf-8", "replace").strip("\r\x00"))
            except OSError:
                pass
            finally:
                try:
                    sock.close()
                except OSError:
                    pass
            with self.lock:
                self.state = "searching"

    def _parse(self, line):
        m = self.RE_CLIENT.match(line)
        if m:
            text = m.group(1)
            with self.lock:
                if text.startswith("Connected"):
                    addr = self.RE_ADDR.search(text)
                    self.in_game = True
                    self.game_server = f"{addr.group(1)}:{addr.group(2)}" if addr else None
                elif "connected" in text.lower():  # "Not Connected" / "Disconnected"
                    self.in_game = False
            return
        m = self.RE_RELAY.match(line)
        if m:
            steamid = int(m.group(4))
            if self.my_steamid and steamid != self.my_steamid:
                return
            with self.lock:
                self.relay_host, self.relay_port = m.group(1), int(m.group(2))
                self.token, self.steamid, self.relay_status = m.group(3), steamid, m.group(5)
                self.relay_for_server = self.game_server


def open_stream(stream_cls, device, callback):
    """
    Open a 16 kHz mono stream. WASAPI shared mode only accepts the device's own mixer rate
    (usually 48 kHz), so for WASAPI devices ask Windows to convert; MME/DirectSound convert
    on their own.
    """
    is_input = stream_cls is sd.RawInputStream
    info = sd.query_devices(device, kind="input" if is_input else "output")
    api = sd.query_hostapis(info["hostapi"])["name"]
    extra = sd.WasapiSettings(auto_convert=True) if "WASAPI" in api else None
    try:
        return stream_cls(samplerate=SAMPLE_RATE, channels=1, dtype="int16", blocksize=FRAME,
                          device=device, callback=callback, extra_settings=extra)
    except sd.PortAudioError as e:
        kind = "microphone" if is_input else "speakers"
        raise RuntimeError(f"Your {kind} '{info['name']}' ({api}) can't be opened for voice: {e}. "
                           f"Pick a different device in the list.") from e


class VoiceClient:
    """Network + audio engine. Thread-safe; the GUI/CLI polls `events` and the public state fields."""

    def __init__(self, server, steamid, token, ptt, input_device=None, output_device=None, open_mic=False):
        self.server = server
        self.steamid = steamid
        self.token = token.encode("ascii")
        self.ptt = ptt
        self.open_mic = open_mic
        self.input_device = input_device
        self.output_device = output_device
        self.volume = 1.0
        self.local_mutes = set()

        self.events = queue.Queue()
        self.linked = False
        self.my_name = ""
        self.hostname = ""
        self.map = ""
        self.rules = 0
        self.players = {}
        self.last_state = 0.0
        self.mic_level = 0.0
        self.last_pong = 0.0
        self.seq = 0
        self.queues = {}
        self.last_heard = {}
        self.lock = threading.Lock()
        self.stop = threading.Event()
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.settimeout(1.0)
        self._streams = []

    def log(self, msg):
        self.events.put(msg)

    # -- packets
    def send(self, kind, payload=b""):
        try:
            self.sock.sendto(MAGIC + bytes([kind]) + payload, self.server)
        except OSError:
            pass

    def hello(self):
        self.send(T_HELLO, struct.pack("<Q", self.steamid) + self.token)

    # -- audio callbacks (PortAudio thread)
    def on_input(self, indata, frames, time_info, status):
        samples = np.frombuffer(indata, dtype=np.int16)
        self.mic_level = float(np.abs(samples).max()) / 32768.0 if len(samples) else 0.0
        if not self.linked or not (self.open_mic or self.ptt.active):
            return
        self.seq = (self.seq + 1) & 0xFFFFFFFF
        self.send(T_AUDIO, struct.pack("<I", self.seq) + bytes(indata))

    def on_output(self, outdata, frames, time_info, status):
        mix = np.zeros(frames, dtype=np.int32)
        with self.lock:
            for sid, q in list(self.queues.items()):
                if not q or sid in self.local_mutes:
                    if q and sid in self.local_mutes:
                        q.popleft()
                    continue
                frame = q.popleft()
                if len(frame) == frames:
                    mix += frame
        if self.volume != 1.0:
            mix = (mix * self.volume).astype(np.int32)
        np.clip(mix, -32768, 32767, out=mix)
        outdata[:] = mix.astype(np.int16).tobytes()

    # -- receive loop
    def receive_loop(self):
        while not self.stop.is_set():
            try:
                data, _ = self.sock.recvfrom(8192)
            except socket.timeout:
                continue
            except OSError:
                break
            if len(data) < 5 or data[:4] != MAGIC:
                continue
            kind, body = data[4], data[5:]
            try:
                self.handle(kind, body)
            except (struct.error, IndexError, UnicodeDecodeError):
                continue

    @staticmethod
    def read_str(body, pos):
        n = body[pos]
        return body[pos + 1:pos + 1 + n].decode("utf-8", "replace"), pos + 1 + n

    def handle(self, kind, body):
        if kind == T_WELCOME:
            status = body[0]
            if status == 0:
                self.my_name, _ = self.read_str(body, 1)
                if not self.linked:
                    self.log(f"Linked to relay as {self.my_name}")
                self.linked = True
                self.last_pong = time.monotonic()
            elif status == 1:
                self.log("Relay rejected the token. Type !voice in game for a fresh one.")
                self.stop.set()
            elif not self.linked:
                self.log("You're not on the game server yet (or it's changing map); waiting...")
        elif kind == T_PONG:
            self.last_pong = time.monotonic()
        elif kind == T_SPEAK:
            sid = struct.unpack_from("<Q", body)[0]
            name, pos = self.read_str(body, 8)
            pcm = body[pos + 4:]
            if len(pcm) % 2:
                return
            frame = np.frombuffer(pcm, dtype=np.int16).astype(np.int32)
            now = time.monotonic()
            with self.lock:
                self.queues.setdefault(sid, collections.deque(maxlen=15)).append(frame)
                if now - self.last_heard.get(sid, 0) > 1.5:
                    self.log(f"{name} is talking")
                self.last_heard[sid] = now
        elif kind == T_STATE:
            self.hostname, pos = self.read_str(body, 0)
            self.map, pos = self.read_str(body, pos)
            self.rules = body[pos]
            count = body[pos + 1]
            pos += 2
            players = {}
            for _ in range(count):
                sid = struct.unpack_from("<Q", body, pos)[0]
                team, flags = body[pos + 8], body[pos + 9]
                name, pos = self.read_str(body, pos + 10)
                players[sid] = Player(sid, name, team, flags)
            self.players = players
            self.last_state = time.monotonic()

    # -- housekeeping
    def keepalive_loop(self):
        next_hello = 0.0
        while not self.stop.is_set():
            now = time.monotonic()
            if self.linked and now - self.last_pong > 10:
                self.log("Lost the relay; re-linking...")
                self.linked = False
                self.players = {}
            if not self.linked:
                if now >= next_hello:
                    self.hello()
                    next_hello = now + 2.0
            else:
                self.send(T_PING)
            with self.lock:
                for sid in [s for s, t in self.last_heard.items() if now - t > 5]:
                    self.queues.pop(sid, None)
                    self.last_heard.pop(sid, None)
            time.sleep(1.0)

    def rules_text(self):
        r = self.rules
        if r & RULE_VOICE_DISABLED:
            return "voice disabled on server"
        if r & RULE_FULL_ALLTALK:
            return "everyone can talk to everyone"
        parts = ["all talk" if r & RULE_ALLTALK else "team only"]
        if not r & RULE_ALLTALK:
            parts.append("dead can talk to living" if r & RULE_DEADTALK else "dead can't talk to living")
            if r & RULE_ENEMY_LIVING:
                parts.append("living hear enemies")
            if r & RULE_ENEMY_DEAD:
                parts.append("dead hear enemies")
        return ", ".join(parts)

    def start(self):
        threading.Thread(target=self.receive_loop, daemon=True).start()
        threading.Thread(target=self.keepalive_loop, daemon=True).start()
        self.ptt.start()
        self._streams = [
            open_stream(sd.RawInputStream, self.input_device, self.on_input),
            open_stream(sd.RawOutputStream, self.output_device, self.on_output),
        ]
        for s in self._streams:
            s.start()
        self.log(f"Connecting to {self.server[0]}:{self.server[1]} as SteamID {self.steamid}")

    def close(self):
        self.stop.set()
        self.send(T_BYE)
        self.ptt.stop()
        for s in self._streams:
            try:
                s.stop()
                s.close()
            except Exception:
                pass
        self._streams = []
        try:
            self.sock.close()
        except OSError:
            pass


# ----------------------------------------------------------------------------- GUI

def run_gui(args, steam, steamid, detected_key):
    import tkinter as tk
    from tkinter import ttk, messagebox

    settings = load_settings()
    client = {"obj": None}

    root = tk.Tk()
    root.title(f"{APP_NAME} {APP_VERSION}")
    root.minsize(760, 560)
    root.configure(bg="#1b1e24")

    style = ttk.Style(root)
    style.theme_use("clam")
    bg, panel, fg, dim, accent = "#1b1e24", "#242830", "#e6e6e6", "#9aa0a6", "#e0a020"
    style.configure(".", background=bg, foreground=fg, fieldbackground=panel, bordercolor="#333842", font=("Segoe UI", 10))
    style.configure("TFrame", background=bg)
    style.configure("Panel.TFrame", background=panel)
    style.configure("TLabel", background=bg, foreground=fg)
    style.configure("Panel.TLabel", background=panel, foreground=fg)
    style.configure("Dim.TLabel", background=panel, foreground=dim)
    style.configure("Title.TLabel", background=bg, foreground=accent, font=("Segoe UI Semibold", 18))
    style.configure("Sub.TLabel", background=bg, foreground=dim, font=("Segoe UI", 9))
    style.configure("Status.TLabel", background=bg, foreground=fg, font=("Segoe UI Semibold", 11))
    style.configure("TButton", background="#2f3540", foreground=fg, padding=(12, 5), borderwidth=0)
    style.map("TButton", background=[("active", "#3a414e"), ("disabled", "#2a2e36")], foreground=[("disabled", dim)])
    style.configure("Accent.TButton", background=accent, foreground="#1b1e24")
    style.map("Accent.TButton", background=[("active", "#f0b030"), ("disabled", "#5a4a20")])
    style.configure("TEntry", foreground=fg, insertcolor=fg)
    style.configure("TCombobox", foreground=fg, arrowcolor=fg)
    style.configure("TCheckbutton", background=panel, foreground=fg)
    style.map("TCheckbutton", background=[("active", panel)])
    style.configure("Treeview", background=panel, fieldbackground=panel, foreground=fg, rowheight=24, borderwidth=0)
    style.configure("Treeview.Heading", background="#2f3540", foreground=fg, relief="flat")
    style.map("Treeview", background=[("selected", "#3a414e")])
    style.configure("Horizontal.TProgressbar", troughcolor=panel, background="#6cc644", borderwidth=0)
    style.configure("Horizontal.TScale", troughcolor=panel, background=accent)

    outer = ttk.Frame(root, padding=14)
    outer.pack(fill="both", expand=True)

    # header
    head = ttk.Frame(outer)
    head.pack(fill="x")
    ttk.Label(head, text=APP_NAME, style="Title.TLabel").pack(side="left")
    ttk.Label(head, text="  out-of-game voice for CS2 community servers", style="Sub.TLabel").pack(side="left", pady=(10, 0))
    status_var = tk.StringVar(value="● Disconnected")
    status_lbl = ttk.Label(head, textvariable=status_var, style="Status.TLabel")
    status_lbl.pack(side="right")

    server_var = tk.StringVar(value="")
    ttk.Label(outer, textvariable=server_var, style="Sub.TLabel").pack(anchor="w", pady=(2, 10))

    # connection panel
    conn = ttk.Frame(outer, style="Panel.TFrame", padding=12)
    conn.pack(fill="x")
    ttk.Label(conn, text="Server", style="Dim.TLabel").grid(row=0, column=0, sticky="w")
    ttk.Label(conn, text="Token", style="Dim.TLabel").grid(row=0, column=1, sticky="w", padx=(12, 0))
    ttk.Label(conn, text="SteamID", style="Dim.TLabel").grid(row=0, column=2, sticky="w", padx=(12, 0))
    e_server = ttk.Entry(conn, width=26)
    e_server.insert(0, args.server or settings.get("server", ""))
    e_server.grid(row=1, column=0, sticky="we")
    e_token = ttk.Entry(conn, width=14, font=("Consolas", 11))
    e_token.insert(0, args.token or "")
    e_token.grid(row=1, column=1, sticky="we", padx=(12, 0))
    e_steam = ttk.Entry(conn, width=20)
    e_steam.insert(0, str(args.steamid or steamid or settings.get("steamid", "")))
    e_steam.grid(row=1, column=2, sticky="we", padx=(12, 0))
    btn_connect = ttk.Button(conn, text="Connect", style="Accent.TButton")
    btn_connect.grid(row=1, column=3, padx=(16, 0))
    conn.columnconfigure(0, weight=1)

    auto_row = ttk.Frame(conn, style="Panel.TFrame")
    auto_row.grid(row=2, column=0, columnspan=4, sticky="we", pady=(10, 0))
    auto_var = tk.BooleanVar(value=args.netconport is not None or settings.get("auto", True))
    ttk.Checkbutton(auto_row, text="Detect server and token from CS2", variable=auto_var).pack(side="left")
    ttk.Label(auto_row, text="   console port", style="Dim.TLabel").pack(side="left")
    e_netcon = ttk.Entry(auto_row, width=7)
    e_netcon.insert(0, str(args.netconport or settings.get("netcon_port", DEFAULT_NETCON_PORT)))
    e_netcon.pack(side="left", padx=(6, 0))
    console_var = tk.StringVar(value="")
    console_lbl = ttk.Label(conn, textvariable=console_var, style="Dim.TLabel", wraplength=700, justify="left")
    console_lbl.grid(row=3, column=0, columnspan=4, sticky="w", pady=(6, 0))

    # audio panel
    audio = ttk.Frame(outer, style="Panel.TFrame", padding=12)
    audio.pack(fill="x", pady=(10, 0))
    devices = sd.query_devices()
    hostapis = sd.query_hostapis()
    preferred_api = None
    for i, api in enumerate(hostapis):
        if "WASAPI" in api["name"]:
            preferred_api = i
    def dev_list(kind):
        key = "max_input_channels" if kind == "in" else "max_output_channels"
        items = []
        for i, d in enumerate(devices):
            if d[key] > 0 and (preferred_api is None or d["hostapi"] == preferred_api):
                items.append(f"{i}: {d['name']}")
        return items
    ttk.Label(audio, text="Microphone", style="Dim.TLabel").grid(row=0, column=0, sticky="w")
    ttk.Label(audio, text="Speakers", style="Dim.TLabel").grid(row=0, column=1, sticky="w", padx=(12, 0))
    cb_in = ttk.Combobox(audio, values=dev_list("in"), state="readonly", width=34)
    cb_out = ttk.Combobox(audio, values=dev_list("out"), state="readonly", width=34)
    def preselect(cb, saved, default_index):
        vals = list(cb["values"])
        for v in vals:
            if saved and v == saved:
                cb.set(v); return
        for v in vals:
            if v.startswith(f"{default_index}:"):
                cb.set(v); return
        if vals:
            cb.current(0)
    try:
        preselect(cb_in, settings.get("input"), sd.default.device[0])
        preselect(cb_out, settings.get("output"), sd.default.device[1])
    except Exception:
        pass
    cb_in.grid(row=1, column=0, sticky="we")
    cb_out.grid(row=1, column=1, sticky="we", padx=(12, 0))
    audio.columnconfigure(0, weight=1)
    audio.columnconfigure(1, weight=1)

    ttk.Label(audio, text="Push-to-talk key", style="Dim.TLabel").grid(row=2, column=0, sticky="w", pady=(10, 0))
    key_row = ttk.Frame(audio, style="Panel.TFrame")
    key_row.grid(row=3, column=0, sticky="we")
    e_key = ttk.Entry(key_row, width=12)
    e_key.insert(0, (args.key or settings.get("key") or detected_key or "").upper())
    e_key.pack(side="left")
    key_hint = "detected from your CS2 +voicerecord bind" if detected_key and not args.key else "CS2 key name, e.g. V, MOUSE4, SPACE"
    ttk.Label(key_row, text="  " + key_hint, style="Dim.TLabel").pack(side="left")
    toggle_var = tk.BooleanVar(value=settings.get("toggle", False))
    openmic_var = tk.BooleanVar(value=args.open_mic or settings.get("open_mic", False))
    opts = ttk.Frame(audio, style="Panel.TFrame")
    opts.grid(row=3, column=1, sticky="w", padx=(12, 0))
    ttk.Checkbutton(opts, text="Toggle instead of hold", variable=toggle_var).pack(side="left")
    ttk.Checkbutton(opts, text="Open mic", variable=openmic_var).pack(side="left", padx=(12, 0))

    ttk.Label(audio, text="Mic level", style="Dim.TLabel").grid(row=4, column=0, sticky="w", pady=(10, 0))
    ttk.Label(audio, text="Incoming volume", style="Dim.TLabel").grid(row=4, column=1, sticky="w", padx=(12, 0), pady=(10, 0))
    mic_bar = ttk.Progressbar(audio, maximum=100, length=200)
    mic_bar.grid(row=5, column=0, sticky="we")
    vol_var = tk.DoubleVar(value=settings.get("volume", 100))
    ttk.Scale(audio, from_=0, to=200, variable=vol_var).grid(row=5, column=1, sticky="we", padx=(12, 0))
    tx_var = tk.StringVar(value="")
    ttk.Label(audio, textvariable=tx_var, style="Panel.TLabel", font=("Segoe UI Semibold", 10)).grid(row=6, column=0, columnspan=2, sticky="w", pady=(8, 0))

    # players panel
    ttk.Label(outer, text="Players  (double-click a row to mute/unmute locally)", style="Sub.TLabel").pack(anchor="w", pady=(12, 4))
    cols = ("speaking", "name", "team", "state", "relay", "hearing")
    tree = ttk.Treeview(outer, columns=cols, show="headings", height=9, selectmode="browse")
    for c, text, w in (("speaking", "", 28), ("name", "Player", 220), ("team", "Team", 150),
                       ("state", "State", 80), ("relay", "Relay", 90), ("hearing", "You hear them", 140)):
        tree.heading(c, text=text)
        tree.column(c, width=w, anchor="w", stretch=(c == "name"))
    tree.tag_configure("ct", foreground="#8ab4f8")
    tree.tag_configure("t", foreground="#f4b26b")
    tree.tag_configure("spec", foreground=dim)
    tree.tag_configure("self", font=("Segoe UI Semibold", 10))
    tree.pack(fill="both", expand=True)
    rules_var = tk.StringVar(value="")
    ttk.Label(outer, textvariable=rules_var, style="Sub.TLabel").pack(anchor="w", pady=(4, 0))

    # log
    log = tk.Text(outer, height=5, bg=panel, fg=dim, relief="flat", font=("Consolas", 9), state="disabled")
    log.pack(fill="x", pady=(8, 0))

    def write_log(msg):
        log.configure(state="normal")
        log.insert("end", f"[{time.strftime('%H:%M:%S')}] {msg}\n")
        log.see("end")
        log.configure(state="disabled")

    def dev_index(cb):
        v = cb.get()
        return int(v.split(":", 1)[0]) if v and v[0].isdigit() else None

    def set_status(text, color):
        status_var.set(text)
        status_lbl.configure(foreground=color)

    console = {"obj": None, "port": None}
    # (relay, token) the user disconnected from, or that failed: don't auto-connect to it again
    auto_hold = {"key": None}

    def connect(auto=False):
        if client["obj"] and not auto:
            disconnect(manual=True)
            return

        def fail(msg):
            if auto:
                write_log(msg)
                auto_hold["key"] = (e_server.get().strip(), e_token.get().strip().upper())
            else:
                messagebox.showerror(APP_NAME, msg)

        server = e_server.get().strip()
        token = e_token.get().strip().upper()
        host, _, port = server.rpartition(":")
        if not host or not port.isdigit():
            return fail("Server must be host:port, e.g. 203.0.113.5:27030")
        if len(token) != TOKEN_LEN:
            return fail(f"The token is {TOKEN_LEN} characters; copy it from game chat (!voice).")
        try:
            sid = int(e_steam.get().strip())
        except ValueError:
            return fail("SteamID must be a 17-digit SteamID64.")
        key = e_key.get().strip().upper()
        if not openmic_var.get() and not PushToTalk.supported(key):
            return fail(f"'{key}' is not a usable push-to-talk key. Try a letter, F-key, SPACE or MOUSE4/5.")
        try:
            addr = (socket.gethostbyname(host), int(port))
        except OSError as e:
            return fail(f"Can't resolve {host}: {e}")

        ptt = PushToTalk(key or "V", toggle_var.get(), on_change=lambda on: root.after(0, tx_var.set, "● TRANSMITTING" if on else ""))
        c = VoiceClient(addr, sid, token, ptt, dev_index(cb_in), dev_index(cb_out), openmic_var.get())
        c.volume = vol_var.get() / 100.0
        try:
            c.start()
        except Exception as e:
            c.close()
            return fail(f"Couldn't start audio: {e}")
        client["obj"] = c
        client["key"] = (server, token)
        btn_connect.configure(text="Disconnect")
        set_status("● Linking…", accent)
        save_current(server=server, steamid=sid, key=key)

    def save_current(**extra):
        data = load_settings()
        data.update({"toggle": toggle_var.get(), "open_mic": openmic_var.get(), "volume": vol_var.get(),
                     "input": cb_in.get(), "output": cb_out.get(), "auto": auto_var.get(),
                     "netcon_port": e_netcon.get().strip()})
        data.update(extra)
        save_settings(data)

    def disconnect(manual=False):
        c = client["obj"]
        if manual and c:
            auto_hold["key"] = client.get("key")
        if c:
            c.close()
            client["obj"] = None
        btn_connect.configure(text="Connect")
        set_status("● Disconnected", dim)
        server_var.set("")
        tx_var.set("")
        rules_var.set("")
        tree.delete(*tree.get_children())
        write_log("Disconnected")

    def on_double_click(_event):
        c = client["obj"]
        sel = tree.selection()
        if not c or not sel:
            return
        sid = int(sel[0])
        if sid in c.local_mutes:
            c.local_mutes.discard(sid); write_log(f"Unmuted {c.players.get(sid).name if sid in c.players else sid}")
        else:
            c.local_mutes.add(sid); write_log(f"Muted {c.players.get(sid).name if sid in c.players else sid} (locally)")

    def set_entry(entry, value):
        if entry.get() != value:
            entry.delete(0, "end")
            entry.insert(0, value)

    def auto_detect():
        """Keep a GameConsole running while auto-detect is on and act on what it reports."""
        port_text = e_netcon.get().strip()
        port = int(port_text) if port_text.isdigit() and 0 < int(port_text) < 65536 else None
        gc = console["obj"]
        if not auto_var.get() or port is None:
            if gc:
                gc.close()
                console["obj"] = None
            console_var.set("" if auto_var.get() is False else "Enter the console port you set with -netconport.")
            return
        if gc is None or console["port"] != port:
            if gc:
                gc.close()
            try:
                my_sid = int(e_steam.get().strip())
            except ValueError:
                my_sid = None
            gc = console["obj"] = GameConsole(port, my_sid)
            console["port"] = port

        state, in_game, game_server, relay, token, sid, relay_status = gc.snapshot()
        if state != "connected":
            console_var.set(f"Waiting for CS2's console on port {port}. Add  -netconport {port}  to CS2's launch "
                            f"options in Steam (one-time), then start CS2.")
            return
        if not in_game:
            console_var.set("CS2 found. Join a server and the server address and token will be filled in automatically.")
            return
        if not relay:
            where = game_server or "this server"
            console_var.set(f"CS2 is on {where}. Waiting for the CS2VoiceRelay plugin to answer "
                            "(the server may not run it).")
            return
        console_var.set(f"CS2 is on {game_server or 'a server'}, relay {relay}. Server and token filled in from CS2.")

        set_entry(e_server, relay)
        set_entry(e_token, token)
        if sid:
            set_entry(e_steam, str(sid))
        detected = (relay, token)
        c = client["obj"]
        if c is None:
            if auto_hold["key"] != detected:
                auto_hold["key"] = None
                write_log(f"Detected {relay} from CS2; connecting")
                connect(auto=True)
        elif client.get("key") != detected:
            write_log(f"CS2 moved to another server ({relay}); reconnecting")
            disconnect()
            connect(auto=True)

    def refresh():
        try:
            auto_detect()
        except Exception as e:  # never let detection kill the UI loop
            console_var.set(f"Auto-detect error: {e}")
        c = client["obj"]
        if c:
            while not c.events.empty():
                write_log(c.events.get())
            if c.stop.is_set():
                disconnect()
            else:
                c.volume = vol_var.get() / 100.0
                mic_bar["value"] = min(100, int(c.mic_level * 140))
                if c.linked:
                    set_status(f"● Linked as {c.my_name}", "#6cc644")
                    if c.hostname or c.map:
                        server_var.set(f"{c.hostname}   •   {c.map}")
                    rules_var.set("Server voice rules: " + c.rules_text())
                else:
                    set_status("● Linking…", accent)
                existing = set(tree.get_children())
                wanted = set()
                order = sorted(c.players.values(), key=lambda p: (not p.is_self, -p.team, p.name.lower()))
                for idx, p in enumerate(order):
                    iid = str(p.steamid)
                    wanted.add(iid)
                    muted_local = p.steamid in c.local_mutes
                    hearing = "you" if p.is_self else ("muted by you" if muted_local else ("yes" if p.can_hear else "no"))
                    if p.muted and not p.is_self:
                        hearing = "server-muted"
                    values = ("●" if p.speaking else "", p.name, TEAM_NAMES.get(p.team, str(p.team)),
                              "alive" if p.alive else ("dead" if p.team >= 2 else ""),
                              "linked" if p.linked else "no client", hearing)
                    tags = ("self",) if p.is_self else (("ct",) if p.team == 3 else ("t",) if p.team == 2 else ("spec",))
                    if iid in existing:
                        tree.item(iid, values=values, tags=tags)
                        tree.move(iid, "", idx)
                    else:
                        tree.insert("", idx, iid=iid, values=values, tags=tags)
                for iid in existing - wanted:
                    tree.delete(iid)
        root.after(100, refresh)

    def on_close():
        save_current()
        disconnect()
        if console["obj"]:
            console["obj"].close()
        root.destroy()

    btn_connect.configure(command=connect)
    tree.bind("<Double-1>", on_double_click)
    root.protocol("WM_DELETE_WINDOW", on_close)
    write_log(f"{APP_NAME} {APP_VERSION} ready. " + ("SteamID detected from Steam." if steamid else "Enter your SteamID64."))
    if args.server and args.token:
        root.after(200, connect)
    refresh()
    root.mainloop()


# ----------------------------------------------------------------------------- CLI

def run_cli(args, steam, steamid, key):
    if not key and not args.open_mic:
        sys.exit("No +voicerecord bind found; pass --key (e.g. --key v) or --open-mic.")

    def dev(v):
        return int(v) if v is not None and v.isdigit() else v

    def log(msg):
        print(f"[{time.strftime('%H:%M:%S')}] {msg}")

    def start(server, token, sid):
        host, _, port = server.rpartition(":")
        ptt = PushToTalk(key or "V", args.toggle, on_change=lambda on: print("mic", "ON" if on else "off"))
        c = VoiceClient((socket.gethostbyname(host), int(port)), sid, token, ptt,
                        dev(args.input_device), dev(args.output_device), args.open_mic)
        c.start()
        return c

    gc = None
    if not (args.server and args.token):
        port = args.netconport or DEFAULT_NETCON_PORT
        gc = GameConsole(port, steamid)
        log(f"Waiting for CS2's console on port {port} (launch CS2 with  -netconport {port}) and a server running CS2VoiceRelay...")
    else:
        if not args.server.rpartition(":")[2].isdigit():
            sys.exit("--server must be host:port")
        if not steamid:
            sys.exit("Couldn't find your SteamID in the Steam install; pass --steamid <steamid64>.")
        if len(args.token.strip()) != TOKEN_LEN:
            sys.exit(f"Token must be {TOKEN_LEN} characters.")

    c, current = None, None
    print(f"{APP_NAME} {APP_VERSION}: push-to-talk {key or '(open mic)'}. Ctrl+C to quit.")
    try:
        while True:
            if gc:
                _, in_game, game_server, relay, token, sid, _ = gc.snapshot()
                detected = (relay, token) if relay and token else None
                if detected and detected != current:
                    if c:
                        log(f"CS2 moved to another server ({relay}); reconnecting")
                        c.close()
                    else:
                        log(f"Detected {relay} from CS2 ({game_server})")
                    c, current = start(relay, token, sid or steamid), detected
            elif c is None:
                c = start(args.server, args.token.strip().upper(), steamid)
            if c:
                while not c.events.empty():
                    log(c.events.get())
                if c.stop.is_set() and not gc:
                    break
            time.sleep(0.2)
    except KeyboardInterrupt:
        pass
    finally:
        if c:
            c.close()
        if gc:
            gc.close()


def main():
    ap = argparse.ArgumentParser(prog=APP_NAME, description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--server", help="relay host:port, as shown in game chat")
    ap.add_argument("--token", help="token from game chat")
    ap.add_argument("--steamid", type=int, help="your SteamID64 (auto-detected from Steam)")
    ap.add_argument("--key", help="push-to-talk key, CS2 names (auto-detected from your +voicerecord bind)")
    ap.add_argument("--toggle", action="store_true", help="press the key to toggle the mic instead of holding it")
    ap.add_argument("--open-mic", action="store_true", help="always transmit")
    ap.add_argument("--input-device", help="sounddevice input name or index")
    ap.add_argument("--output-device", help="sounddevice output name or index")
    ap.add_argument("--list-devices", action="store_true")
    ap.add_argument("--netconport", type=int, help=f"CS2 console port for auto-detect (default {DEFAULT_NETCON_PORT}; "
                                                    "launch CS2 with -netconport PORT)")
    ap.add_argument("--nogui", action="store_true", help="run in the terminal")
    args = ap.parse_args()

    if args.list_devices:
        print(sd.query_devices())
        return

    steam = steam_path()
    steamid = args.steamid or detect_steamid(steam)
    key = args.key or detect_ptt_key(steam, steamid)

    if args.nogui:
        run_cli(args, steam, steamid, key)
        return
    try:
        run_gui(args, steam, steamid, key)
    except ImportError:
        print("tkinter is not available; falling back to the terminal client.")
        run_cli(args, steam, steamid, key)


if __name__ == "__main__":
    main()
