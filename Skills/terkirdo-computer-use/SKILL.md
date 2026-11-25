---
name: terkirdo-computer-use
description: Control the Windows desktop and 3D/2D games (apps, UI automation tree, client_only game canvas & coordinate_space, strict PID targeting, 120Hz wait_for pixel/hash/centroid signal reflex with <2ms @centroid reaction, full-duplex /ws/control WebSocket + 32B TKRF binary stream, zero-copy Local\ TKSM Seqlock shared-memory frame ring, biomechanical humanize Minimum-Jerk + Bezier input physics, 60Hz-120Hz QPC trajectories, clicks, stateful key/mouse holds, 3D camera mouse movement, macro batching, panic input release, ROI & multi-monitor DXGI screenshots, clipboard, window & process lifecycle) via TerkirdoComputer v2.0.0 24-tool Pure Real-Time I/O Engine REST/WebSocket daemon and MCP server.
---

# TerkirdoComputer Desktop Automation Skill (v2.0.0 — 24-Tool Pure Real-Time I/O Engine Suite)

This skill allows a single AI agent, Python vision loop (OpenCV / NumPy / PyTorch), or C# client to inspect and control any native Windows desktop application, DirectX/Unity/Unreal/RawInput 3D game, QTE/anti-macro tracking scene, visual novel, and OS subsystem via the **TerkirdoComputer Pure Real-Time I/O Daemon** (`http://127.0.0.1:5005` + `ws://127.0.0.1:5005/ws/control`), zero-copy `Local\TerkirdoFrame_*` shared memory, stdio MCP JSON-RPC, or direct CLI commands.

> **Pure Real-Time I/O Philosophy**: `TerkirdoComputer` is a zero-dependency high-speed **Pixel/Frame Output + Precision/Humanized Hardware Input + Low-Level Signal Reflex** engine. High-level perception (OCR, template matching, YOLO/ML) runs in your Python/C#/LLM caller over zero-copy shared memory (`manage_shared_frame`) or full-duplex WebSockets (`/ws/control`).

---

## 1. Prerequisites & Discovery / Handoff Architecture (원격/로컬 인도인접 체계)

TerkirdoComputer supports **both Local and Remote Agent deployments** with **dynamic port binding and seamless gateway handoff (인도인접)**.
* **Local Agent**: The agent runs on the same Windows desktop machine (supports HTTP REST, `WS /ws/control`, and `<0.05ms` zero-copy `Local\TerkirdoFrame_*` shared memory).
* **Remote Agent**: The agent runs on a remote server, Cloud VM, Docker container, WSL, or another PC (Mac/Linux), controlling the Windows desktop over HTTP REST or `WS /ws/control`.

When running the daemon on the Windows desktop for remote access, start it with `--allow-remote`:
```powershell
terkirdocomputer.exe serve --port 7836 --allow-remote --token my-secret-token
```

### 🔍 Unified Discovery Hierarchy (원격/로컬 통합 탐색 5계층):
```
[Discovery Priority]
  ├─ 1. TERKIRDO_URL env var (e.g. http://192.168.1.50:7836)
  ├─ 2. TERKIRDO_HOST env var (e.g. 192.168.1.50) -> GET http://$TERKIRDO_HOST:5005/discover
  ├─ 3. Local Metadata File -> ~/.terkirdo/serve.url (if agent runs locally)
  ├─ 4. Local Gateway -> GET http://127.0.0.1:5005/discover
  └─ 5. Default Fallback -> http://127.0.0.1:5005
```

### 📋 Universal Discovery Scripts (Copy-Paste Ready):

#### Bash / Linux / Cloud / Docker / WSL:
```bash
TARGET_HOST="${TERKIRDO_HOST:-127.0.0.1}"

if [ -n "$TERKIRDO_URL" ]; then
    BASE_URL="$TERKIRDO_URL"
else
    DISCOVERED=$(curl -s --connect-timeout 2 "http://$TARGET_HOST:5005/discover" 2>/dev/null | grep -o '"activeUrl":"[^"]*' | cut -d'"' -f4)
    if [ -n "$DISCOVERED" ]; then
        BASE_URL="$DISCOVERED"
    elif [ -f ~/.terkirdo/serve.url ]; then
        BASE_URL=$(cat ~/.terkirdo/serve.url)
    else
        BASE_URL="http://$TARGET_HOST:5005"
    fi
fi

TOKEN="${TERKIRDO_TOKEN:-$(cat ~/.terkirdo/serve.token 2>/dev/null)}"
curl -s "$BASE_URL/health"
```

#### PowerShell (Windows / Windows Terminal):
```powershell
$targetHost = if ($env:TERKIRDO_HOST) { $env:TERKIRDO_HOST } else { "127.0.0.1" }

$baseUrl = if ($env:TERKIRDO_URL) {
    $env:TERKIRDO_URL
} else {
    try {
        (Invoke-RestMethod "http://$targetHost:5005/discover" -TimeoutSec 2).activeUrl
    } catch {
        if (Test-Path "$env:USERPROFILE\.terkirdo\serve.url") {
            (Get-Content "$env:USERPROFILE\.terkirdo\serve.url").Trim()
        } else {
            "http://$targetHost:5005"
        }
    }
}

$token = if ($env:TERKIRDO_TOKEN) {
    $env:TERKIRDO_TOKEN
} elseif (Test-Path "$env:USERPROFILE\.terkirdo\serve.token") {
    (Get-Content "$env:USERPROFILE\.terkirdo\serve.token").Trim()
} else { "" }
```

#### Python (Agentic Frameworks & High-FPS Game / Vision Loops):
```python
import os, requests
from pathlib import Path

def get_terkirdo_session():
    if os.getenv("TERKIRDO_URL"):
        base_url, token = os.getenv("TERKIRDO_URL"), os.getenv("TERKIRDO_TOKEN", "")
    else:
        target_host = os.getenv("TERKIRDO_HOST", "127.0.0.1")
        try:
            res = requests.get(f"http://{target_host}:5005/discover", timeout=2).json()
            base_url = res.get("activeUrl", f"http://{target_host}:5005")
        except Exception:
            url_file = Path.home() / ".terkirdo" / "serve.url"
            base_url = url_file.read_text().strip() if url_file.exists() else f"http://{target_host}:5005"

        token = os.getenv("TERKIRDO_TOKEN")
        if not token:
            token_file = Path.home() / ".terkirdo" / "serve.token"
            token = token_file.read_text().strip() if token_file.exists() else ""

    session = requests.Session()
    session.headers.update({"Authorization": f"Bearer {token}"})
    return base_url, token, session
```

### 🔐 Authentication Token Header:
Always pass `Authorization: Bearer <TOKEN>` or `X-Terkirdo-Token: <TOKEN>` in HTTP/WebSocket headers. Never pass `?token=` in `/ws/control` query strings (`400 Bad Request` by security design).

---

## 2. 24-Tool Pure Real-Time I/O Engine Suite Overview

TerkirdoComputer v2.0.0 provides **24 atomic CUA, OS, and real-time game I/O tools** across REST, WebSocket (`/ws/control`), MCP, and CLI:

| Category | Tools | Description |
| :--- | :--- | :--- |
| **Inspection, Canvas, Vision Probe & 120Hz Signal Reflex (5)** | `list_apps`, `get_app_state`, `query`, `probe_screen`, `wait_for` | Enumerate windows, capture `client_only` game canvas + `clientBounds`/`clientOffset`, run UIA `query`, `<1ms` RGB/FNV-1a/centroid `probe_screen`, or **`120Hz` `wait_for` (`POST /api/vision/wait`)** with `"initial"` hash delta, `consecutive_hits`, and `<2ms` in-process `@centroid` `on_match` reaction! |
| **Zero-Copy Shared Memory Frame Ring (1)** | `manage_shared_frame` | Start/stop/inspect `Local\TerkirdoFrame_<32hex>` pagefile shared memory with a **64-byte `"TKSM"` (`<IHHQQQQIIHHBBHQ`) Per-Slot + Global Seqlock Double-Buffer** for `<0.05ms` zero-copy Python `mmap` + `numpy.frombuffer` BGRA32 vision loops (`owner_pid` watchdog included). |
| **Mouse, Trajectory, 3D Camera, Keyboard & `humanize` Physics (10)** | `click`, `move_mouse`, `follow_trajectory`, `drag`, `scroll`, `type_text`, `press_key`, `set_value`, `select_text`, `perform_secondary_action` | Explicit `coordinate_space: "client" \| "window" \| "screen"`, strict `"pid:12345"` / `pid` isolation, opt-in **`humanize` (`"none" \| "subtle" \| "natural" \| "fast"`)** Minimum-Jerk + monitor-clamped Cubic Bezier (`0.0px` final target error), 60Hz–120Hz QPC `follow_trajectory`, and 3D RawInput camera `delta_x`/`delta_y`. |
| **Game Combo & Safety Control (2)** | `batch_actions`, `release_inputs` | Execute up to 200 combo/macro/trajectory steps inside one `ActionLock` with zero IPC overhead (`batch_actions`) and emergency release of all held keys/buttons (`release_inputs`). |
| **OS Clipboard (2)** | `get_clipboard`, `set_clipboard` | Fast atomic UTF-16 text & PNG image transfer with optional instant paste (`paste: true`). |
| **Window Management (1)** | `manage_window` | 6 atomic actions (`focus`, `minimize`, `maximize`, `restore`, `move`, `resize`) backed by a 150ms PID-to-HWND cache. |
| **Multi-Monitor & ROI Screen (1)** | `capture_screen` | DXGI 1.2 GPU Desktop Duplication + GDI fallback with `< 3–5ms` sub-region ROI capture (`x, y, width, height`) and zero-Base64 `raw=true` binary streaming. |
| **Process Lifecycle (2)** | `launch_app`, `close_app` | Quote-safe fast launch (exits loop immediately once window handle is ready) and graceful shutdown via `WM_CLOSE` + force kill fallback. |

---

## 3. v2.0 Real-Time I/O Pillars & Copy-Paste Recipes

### 3.1 Pillar 1 — 120Hz Signal Reflex & `<2ms` `@centroid` Reaction (`wait_for` · `POST /api/vision/wait`)
* Polls **outside `ActionLock`** at `1..240 Hz` (`120 Hz` default, non-blocking DXGI `waitTimeoutMs=0`) so concurrent inputs are never starved.
* Supports `points` (`expected_hex`, `tolerance`, `negate: true`), `regions` (`target_hex`, `min_match_count`, `min_match_ratio`, `changed_from_hash: "initial" | "0x..."`), `match_mode: "all" | "any"`, and `consecutive_hits: 1..10` (anti-particle noise filter).
* When matched, `on_match` acquires `ActionLock` in-process (`<2ms`) and binds moving-target coordinates via `"target": "@centroid"` or `"target": "@centroid:<label>"` (+ optional `offset_x`, `offset_y`).
* Normal timeout returns **HTTP `200 OK` (`isError: false`)** with `{ "ok": true, "matched": false, "timedOut": true, "reactionExecuted": false }`.
```bash
curl -s -X POST -H "Content-Type: application/json" -H "Authorization: Bearer $TOKEN" \
  "$BASE_URL/api/vision/wait" -d '{
    "pid": 12345,
    "coordinate_space": "client",
    "poll_hz": 120,
    "timeout_ms": 3000,
    "consecutive_hits": 2,
    "regions": [
      { "label": "qte_orb", "x": 300, "y": 150, "width": 680, "height": 420, "target_hex": "#FF3355", "tolerance": 24, "min_match_count": 60, "changed_from_hash": "initial" }
    ],
    "on_match": {
      "type": "click",
      "target": "@centroid:qte_orb",
      "offset_x": 0,
      "offset_y": 0,
      "click_method": "hardware",
      "humanize": "fast"
    }
  }'
```

### 3.2 Pillar 2 — Full-Duplex WebSocket Control & 32-Byte `"TKRF"` Binary Stream (`WS /ws/control`)
* Single persistent socket carrying UTF-8 JSON commands (`ping`, `click`, `move_mouse`, `press_key`, `type_text`, `drag`, `scroll`, `follow_trajectory`, `batch_actions`, `release_inputs`, `probe_screen`, `wait_for`, `manage_shared_frame`, `subscribe_frames`, `unsubscribe_frames`) and 1-slot conflated binary frames (`bgra32`, `jpg`, `png`).
* **Single-Agent Takeover**: Connecting a new authenticated WebSocket client atomically cancels the prior session, drains its input worker, calls `ActiveInputTracker.ReleaseAll()`, and closes the old socket with code **`4001`** (`SessionSuperseded`).
* **32-Byte Little-Endian `"TKRF"` Binary Frame Header (`<IBBHIHHQii`)**:
  `magic(u32=0x46524B54), version(u8=1), format(u8: 0=bgra32, 1=jpg, 2=png), flags(u16), frame_seq(u32), width(u16), height(u16), timestamp_qpc_us(u64), origin_x(i32), origin_y(i32)` followed immediately by `payload = msg[32:]`.

```python
import asyncio, json, struct, websockets, numpy as np

TKRF_FMT = "<IBBHIHHQii"  # 32 bytes exact, magic = 0x46524B54 ('TKRF')

async def realtime_ws_loop(ws_url: str, token: str, game_pid: int):
    headers = {"Authorization": f"Bearer {token}"}
    async with websockets.connect(ws_url, additional_headers=headers, max_size=40 * 1024 * 1024) as ws:
        ready = json.loads(await ws.recv())  # {"event": "session_ready", "version": "2.0.0", ...}
        await ws.send(json.dumps({
            "id": "sub1",
            "op": "subscribe_frames",
            "args": {"pid": game_pid, "client_only": True, "format": "bgra32", "max_fps": 60, "only_on_change": True}
        }))
        while True:
            msg = await ws.recv()
            if isinstance(msg, bytes) and len(msg) >= 32:
                magic, ver, fmt, flags, seq, w, h, qpc_us, ox, oy = struct.unpack_from(TKRF_FMT, msg, 0)
                if magic == 0x46524B54 and fmt == 0:
                    frame_bgra = np.frombuffer(msg, dtype=np.uint8, offset=32).reshape((h, w, 4))
                    # Run OpenCV / YOLO / custom logic, then fire zero-overhead input on the same socket:
                    await ws.send(json.dumps({
                        "id": f"aim_{seq}",
                        "op": "move_mouse",
                        "args": {"pid": game_pid, "x": w // 2, "y": h // 2, "coordinate_space": "client", "humanize": "fast"}
                    }))
```

### 3.3 Pillar 3 — Zero-Copy Shared Memory Frame Ring (`manage_shared_frame` · 64-Byte `"TKSM"` Seqlock)
* Local-only (`127.0.0.1`) Windows pagefile `MemoryMappedFile` (`Local\TerkirdoFrame_<32hex>`) eliminating TCP socket copies for 60–240 FPS Python/C# CV loops (`<0.05ms` read). Pass `owner_pid: os.getpid()` so the stream auto-stops within `250ms` if your script exits.
* **64-Byte Cache-Line Aligned `"TKSM"` Header (`<IHHQQQQIIHHBBHQ`)**:
  `magic(u32=0x4D534B54), version(u16=1), header_size(u16=64), global_seq(u64), slot0_seq(u64), slot1_seq(u64), timestamp_qpc_us(u64), max_slot_bytes(u32), stride(u32), width(u16), height(u16), active_slot(u8), pixel_format(u8=0), flags(u16: 1=Active, 2=ClientOnly, 4=Closed), fnv1a_hash(u64)`.
  `Slot 0` starts at byte `64`; `Slot 1` starts at `64 + max_slot_bytes`.

```python
import mmap, os, struct, numpy as np

TKSM_FMT = "<IHHQQQQIIHHBBHQ"  # 64 bytes exact, magic = 0x4D534B54 ('TKSM')

def start_and_read_tksm(base_url: str, session, game_pid: int):
    info = session.post(f"{base_url}/api/vision/shared-frame/start", json={
        "pid": game_pid, "client_only": True, "max_fps": 120, "only_on_change": True, "owner_pid": os.getpid()
    }).json()
    shm = mmap.mmap(-1, info["totalBytes"], tagname=info["mapName"], access=mmap.ACCESS_READ)

    def read_latest_bgra():
        while True:
            magic, ver, hsz, g1, s0, s1, qpc_us, max_slot, stride, w, h, slot, fmt, flags, fnv = struct.unpack_from(TKSM_FMT, shm, 0)
            if magic != 0x4D534B54 or (flags & 0x04):
                return None  # Stream closed
            s_before = s0 if slot == 0 else s1
            if g1 == 0 or (g1 & 1) or s_before == 0 or (s_before & 1):
                continue
            offset = 64 + slot * max_slot
            nbytes = w * h * 4
            arr = np.frombuffer(shm, dtype=np.uint8, count=nbytes, offset=offset).copy().reshape((h, w, 4))
            _, _, _, g2, s0_2, s1_2, *_ = struct.unpack_from(TKSM_FMT, shm, 0)
            s_after = s0_2 if slot == 0 else s1_2
            if g1 == g2 and s_before == s_after and (s_after & 1) == 0:
                return arr, qpc_us, fnv
    return shm, read_latest_bgra
```

### 3.4 Pillar 4 — Biomechanical `humanize` Input Physics (`HumanizedPhysics` & `OcuConfig`)
* Available on `click`, `move_mouse`, `follow_trajectory`, `drag`, `type_text`, `press_key`, and `batch_actions` via `"humanize": "none" | "subtle" | "natural" | "fast" | true | false` (default `"none"`).
* Combines **Flash & Hogan (1985) 5th-order Minimum-Jerk bell-curve velocity** ($u(\tau) = 10\tau^3 - 15\tau^4 + 6\tau^5$), **monitor-clamped Cubic Bezier arcs** (never crosses adjacent monitors or hot corners, landing with **`0.0px` terminal coordinate error**), telescoping relative 3D camera steps ($\sum \delta x_i \equiv \text{round}(\Delta X)$), and truncated $[-2.5\sigma, +2.5\sigma]$ Gaussian key/click dwell jitter (`>= 16ms` hardware floor).
* Note: `follow_trajectory` always defaults to `"none"` even when global `input.humanize` is enabled, applying `humanize` only when explicitly passed on that call so caller-computed 60Hz paths are never double-warped.
* Persistent `OcuConfig` v2.0 keys (`terkirdocomputer.exe config set <key> <val>`):
  * `input.humanize` (`none | subtle | natural | fast`, default `none`)
  * `input.humanizeSeed` (`int | null`, default `null`)
  * `vision.waitPollHz` (`1..240`, default `120`)
  * `vision.waitTimeoutMs` (`10..60000`, default `5000`)
  * `stream.format` (`bgra32 | jpg | png`, default `jpg`)
  * `stream.maxFps` (`1..120`, default `30`)

---

## 4. Core REST API Endpoints & Examples

### 4.1 Application State, `client_only` Game Canvas & Sub-ms `probe_screen`
```bash
# Pure Unity/DirectX client canvas (excludes Windows 11 ~31px title bar & ~8px DWM shadow border):
curl -s -H "Authorization: Bearer $TOKEN" "$BASE_URL/api/apps/pid:12345/state?client_only=true&include_ax=false"

# Direct binary pure client canvas screenshot (zero Base64 roundtrip):
curl -s -H "Authorization: Bearer $TOKEN" "$BASE_URL/api/apps/pid:12345/screenshot?client_only=true&format=png" --output game_canvas.png

# Sub-ms single-shot RGB + FNV-1a hash + centroid probe:
curl -s -X POST -H "Content-Type: application/json" -H "Authorization: Bearer $TOKEN" \
  "$BASE_URL/api/vision/probe" -d '{
    "pid": 12345,
    "coordinate_space": "client",
    "points": [{ "label": "qte_ready", "x": 640, "y": 360, "expected_hex": "#00FF66", "tolerance": 20 }],
    "regions": [{ "label": "moving_orb", "x": 400, "y": 200, "width": 480, "height": 320, "target_hex": "#FF3355", "tolerance": 28 }]
  }'
```

### 4.2 Precision & Humanized Pointer, Trajectory, Keyboard & Batch Actions
```bash
# Humanized natural arc click landing with 0.0px error in pure client space:
curl -s -X POST -H "Content-Type: application/json" -H "Authorization: Bearer $TOKEN" \
  "$BASE_URL/api/actions/click" -d '{"pid": 12345, "x": 640, "y": 360, "coordinate_space": "client", "click_method": "hardware", "humanize": "natural", "skip_snapshot": true}'

# Smooth 3D FPS/TPS camera turn with Minimum-Jerk velocity bell curve:
curl -s -X POST -H "Content-Type: application/json" -H "Authorization: Bearer $TOKEN" \
  "$BASE_URL/api/actions/move-mouse" -d '{"delta_x": 360, "delta_y": -25, "duration_ms": 65, "method": "hardware", "humanize": "fast"}'

# 60Hz-120Hz monotonic QPC cursor trajectory (0.0ms cumulative drift):
curl -s -X POST -H "Content-Type: application/json" -H "Authorization: Bearer $TOKEN" \
  "$BASE_URL/api/actions/trajectory" -d '{
    "pid": 12345, "coordinate_space": "client", "interval_ms": 16.6667, "method": "hardware", "hold_button": true,
    "waypoints": [[640, 260], [665, 268], [684, 288], [692, 315], [684, 342]]
  }'

# In-process macro combo + panic input release:
curl -s -X POST -H "Content-Type: application/json" -H "Authorization: Bearer $TOKEN" \
  "$BASE_URL/api/actions/batch" -d '{"pid": 12345, "steps": [{"type": "press_key", "key": "shift", "key_state": "down", "key_method": "hardware"}, {"type": "wait", "ms": 40}, {"type": "release_inputs"}]}'
curl -s -X POST -H "Authorization: Bearer $TOKEN" "$BASE_URL/api/actions/release-inputs"
```

### 4.3 Multi-Monitor ROI Capture, Clipboard, Window & Process Lifecycle
```bash
curl -s -H "Authorization: Bearer $TOKEN" "$BASE_URL/api/screens/screenshot?display=1&x=1640&y=24&width=256&height=256&format=jpg&raw=true" --output minimap.jpg
curl -s -X POST -H "Content-Type: application/json" -H "Authorization: Bearer $TOKEN" "$BASE_URL/api/clipboard" -d '{"text": "Snippet", "paste": true}'
curl -s -X POST -H "Content-Type: application/json" -H "Authorization: Bearer $TOKEN" "$BASE_URL/api/windows/pid:12345/manage" -d '{"action": "resize", "x": 100, "y": 100, "width": 1280, "height": 720}'
curl -s -X POST -H "Content-Type: application/json" -H "Authorization: Bearer $TOKEN" "$BASE_URL/api/processes/launch" -d '{"path": "C:\\Games\\Farm\\ExpertHarvestingFarm.exe"}'
curl -s -X POST -H "Content-Type: application/json" -H "Authorization: Bearer $TOKEN" "$BASE_URL/api/processes/close" -d '{"pid": 12345, "force": false, "timeout_ms": 3000}'
```

---

## 5. CLI Commands

```powershell
# Diagnostics & Persistent Configuration
terkirdocomputer.exe version
terkirdocomputer.exe doctor [--json]
terkirdocomputer.exe config [list|path|get|set|reset]

# Inspection, Probe, 120Hz Wait-For Reflex, Zero-Copy Shared Frame & Trajectory
terkirdocomputer.exe list-apps
terkirdocomputer.exe snapshot <app|pid:N> [--client-only] [--include-ax <true|false>] [--include-screenshot <true|false>]
terkirdocomputer.exe probe-screen [--pid <N>] [--coordinate-space client] --regions '[{"x":0,"y":0,"width":320,"height":180,"target_hex":"#FF3355"}]'
terkirdocomputer.exe wait-for [--pid <N>] [--coordinate-space client] [--poll-hz 120] [--timeout-ms 3000] --regions '[{"label":"orb","x":0,"y":0,"width":320,"height":180,"target_hex":"#FF3355","min_match_count":20}]' --on-match '{"type":"click","target":"@centroid:orb","click_method":"hardware"}'
terkirdocomputer.exe shared-frame <start|stop|status> [--pid <N>] [--client-only <true|false>] [--max-fps 120] [--only-on-change <true|false>] [--owner-pid <N>]
terkirdocomputer.exe follow-trajectory [--pid <N>] [--coordinate-space client] [--interval-ms 16.6667] [--humanize none] [--hold-button] --waypoints '[[400,300],[450,320],[500,340]]'
terkirdocomputer.exe call <tool> [--args '<json>']
terkirdocomputer.exe release-inputs

# Screen, Clipboard, Window, Process & Server Deployment
terkirdocomputer.exe screen [--display <all|1|2>] [--x <n> --y <n> --width <n> --height <n>] [--format <png|jpg>] [--max-dimension <n>]
terkirdocomputer.exe clipboard <get|set> ...
terkirdocomputer.exe window pid:12345 resize --x 100 --y 100 --width 1280 --height 720
terkirdocomputer.exe launch "C:\Windows\System32\notepad.exe"
terkirdocomputer.exe close --pid 12345 [--force] [--timeout 3000]
terkirdocomputer.exe serve --port 5005
terkirdocomputer.exe mcp
terkirdocomputer.exe install-skill
```

* `install-skill` deploys `SKILL.md` globally to `~/.agents/skills`, `~/.gemini/config/skills`, and `~/.codex/skills` (or to an explicit custom directory / `.md` file path when passed), and `dotnet build` / `dotnet publish` also automatically emits `Skills\terkirdo-computer-use\SKILL.md` inside the build output folder.

---

## 6. Error Handling & 3-Stage Input Delivery (`app_post`, `stealth`, `hardware`)

All error responses adhere to RFC 7807 Problem Details:
* `400 Bad Request`: Invalid arguments, `?token=` query parameter on `/ws/control`, nested `wait_for` inside `batch_actions`, or invalid `humanize` / `coordinate_space` / `pid`.
* `401 Unauthorized`: Token missing or incorrect (`Authorization: Bearer <TOKEN>` or `X-Terkirdo-Token: <TOKEN>`).
* `403 Forbidden`: Non-loopback Host, untrusted or `"null"` `Origin` on `/ws/control` (CSWSH protection), non-loopback caller on `/api/vision/shared-frame/*`, or UIPI privilege mismatch.
* `404 Not Found`: Target application, strict `pid:<id>`, display, window, or element not found.
* `408 Request Timeout` / `423 Locked` / `429 Too Many Requests` (`>4` concurrent `wait_for` loops).

### 3-Stage Input Delivery Modes:
1. **`app_post`**: 100% non-intrusive background control via child `HWND` routing + Fake Activation Spoofing (`WM_ACTIVATE`, `WM_SETFOCUS`) without moving the physical cursor or stealing OS focus.
2. **`stealth`**: ~35ms micro-swap phantom hardware scan-code/mouse injection that backs up and restores the user's cursor coordinates and foreground `HWND`.
3. **`hardware` / `global`**: Dedicated foreground DirectX/Unity/Unreal game mode with `SendInput`, 60Hz–120Hz monotonic QPC trajectories, RawInput 3D camera deltas (`MOUSEEVENTF_MOVE`), `KEYEVENTF_SCANCODE`, and optional `humanize` Minimum-Jerk + Bezier kinematics.
