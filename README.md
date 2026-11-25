# 🖥️ TerkirdoComputer (테르키르도컴퓨터)

<div align="center">

### Zero-Dependency .NET 10 C# Windows Desktop & 2D/3D Game Pure Real-Time I/O Engine

[![Framework](https://img.shields.io/badge/Framework-.NET%2010.0-purple.svg)](https://dotnet.microsoft.com/)
[![Version](https://img.shields.io/badge/Version-v2.0.0-blue.svg)](#)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20(x64)-0078D6.svg)](#)
[![Tools](https://img.shields.io/badge/Tools-24%20Atomic%20Tools-success.svg)](#-24-tool-pure-real-time-io-suite)
[![Protocols](https://img.shields.io/badge/Protocols-MCP%20%7C%20REST%20%7C%20WebSocket%20%7C%20TKSM-orange.svg)](#-핵심-아키텍처--v200-4대-필러)

</div>

---

## 📖 프로젝트 개요

**TerkirdoComputer**는 AI 에이전트, Python 비전 루프(OpenCV / NumPy / PyTorch), 또는 C# 클라이언트가 **Windows 데스크톱 애플리케이션과 2D/3D 게임(DirectX / Unity / Unreal / RawInput)**을 저지연으로 관측하고 정밀 제어할 수 있도록 설계된 **순수 C# .NET 10 기반 단일 실행 바이너리(`terkirdocomputer.exe`) CUA(Computer Use Agent) 엔진**입니다.

Node.js나 외부 PowerShell 프로세스 스폰 없이 **FlaUI.UIA3**, **Win32 P/Invoke**, **DirectX 11 DXGI 1.2 GPU Desktop Duplication(GDI 자동 폴백)**을 단일 인프로세스로 통합하여, **MCP Stdio**, **Hot-Plug Skill + REST API (Swagger UI + `/discover`)**, **Full-Duplex WebSocket (`/ws/control`)**, **Zero-Copy 공유 메모리(`TKSM`)**, 그리고 **단독 CLI**를 모두 지원합니다.

> **Pure Real-Time I/O 철학**: `TerkirdoComputer`는 초고속 **픽셀/프레임 출력 + 정밀/생체모방 하드웨어 입력 + 저수준 신호 반사(Signal Reflex)**에 집중합니다. OCR·템플릿 매칭·YOLO 등 고수준 인식 로직은 호출자(Python/C#/LLM)가 공유 메모리(`TKSM`)나 WebSocket 바이너리 스트림(`TKRF`) 위에서 자유롭게 실행할 수 있습니다.

---

## ⚡ 핵심 아키텍처 & v2.0.0 4대 필러

### 1. 120Hz Signal Reflex & `<2ms` `@centroid` 즉각 반응 (`wait_for` · `probe_screen`)
- `ActionLock` 외부에서 `1~240 Hz`(기본 `120 Hz`, 비차단 DXGI `waitTimeoutMs=0`)로 폴링하여 다른 입력을 막지 않습니다.
- `points`(`expected_hex`, `tolerance`, `negate`), `regions`(`target_hex`, `min_match_count`, `min_match_ratio`, `changed_from_hash: "initial" | "0x..."`), `consecutive_hits`(노이즈 필터)를 지원합니다.
- 조건 충족 시 인프로세스(`<2ms`)로 `on_match`를 실행하며, `"target": "@centroid"` 또는 `"target": "@centroid:<label>"`로 이동 표적의 무게중심 좌표를 즉시 바인딩합니다.

### 2. Full-Duplex WebSocket & 32-Byte `TKRF` 바이너리 스트림 (`WS /ws/control`)
- 단일 지속 연결 위에서 UTF-8 JSON 제어 명령과 1-슬롯 통합(conflated) 바이너리 프레임(`bgra32`, `jpg`, `png`)을 양방향으로 송수신합니다.
- **단일 에이전트 인계(Single-Agent Takeover)**: 새 인증 클라이언트가 접속하면 기존 세션을 원자적으로 취소하고 눌려 있던 입력을 모두 해제(`ActiveInputTracker.ReleaseAll()`)한 뒤 기존 소켓을 코드 `4001`(`SessionSuperseded`)로 종료합니다.
- **32-Byte Little-Endian `TKRF` 헤더 (`<IBBHIHHQii`, magic `0x46524B54`)**: `frame_seq`, `width`, `height`, `timestamp_qpc_us`, `origin_x`, `origin_y` 메타데이터 직후에 원시 픽셀 페이로드가 이어집니다.

### 3. Zero-Copy 공유 메모리 프레임 링 (`manage_shared_frame` · 64-Byte `TKSM` Seqlock)
- 로컬(`127.0.0.1`) 전용 Windows 페이지파일 `MemoryMappedFile`(`Local\TerkirdoFrame_<32hex>`)을 통해 TCP 복사 없이 `<0.05ms`로 Python `mmap` + `numpy.frombuffer`에 BGRA32 프레임을 전달합니다.
- **64-Byte 캐시라인 정렬 `TKSM` 헤더 (`<IHHQQQQIIHHBBHQ`, magic `0x4D534B54`)**: 글로벌/슬롯별 Seqlock 더블 버퍼링을 제공하며, `owner_pid` 워치독으로 호출 프로세스 종료 시 `250ms` 이내에 자동 정리됩니다.

### 4. 3단계 입력 전달 & 생체모방 `humanize` 물리 엔진 (`HumanizedPhysics`)
- **3단계 입력 전달 모드**:
  1. `app_post`: 커서 이동 및 포커스 탈취 없이 자식 `HWND` 라우팅 + 가짜 활성화 스푸핑(`WM_ACTIVATE`, `WM_SETFOCUS`)으로 백그라운드 제어
  2. `stealth`: 약 35ms 마이크로 스왑으로 사용자 커서 위치와 전경 창을 백업·복원하는 팬텀 하드웨어 주입
  3. `hardware` / `global`: 전용 전경 게임용 `SendInput`, `KEYEVENTF_SCANCODE`, 3D RawInput 카메라 델타(`delta_x`/`delta_y`), `60Hz–120Hz` 단조 증가 QPC 궤적(`follow_trajectory`)
- **생체모방 `humanize` 옵션 (`none | subtle | natural | fast`)**: Flash & Hogan(1985) 5차 Minimum-Jerk 속도 곡선과 모니터 경계 클램핑 Cubic Bezier 궤적(`0.0px` 최종 좌표 오차), 텔레스코핑 3D 카메라 스텝, 절단 가우시안 키/클릭 체류 시간(`>= 16ms`)을 지원합니다.
- **`batch_actions` & `release_inputs`**: 단일 `ActionLock` 내부에서 최대 200스텝 콤보/매크로를 IPC 오버헤드 없이 실행하고, 비상 시 모든 고정 키·마우스 버튼을 즉시 해제합니다.

---

## 🧰 24-Tool Pure Real-Time I/O Suite

| 카테고리 (개수) | 도구명 | 주요 역할 |
| :--- | :--- | :--- |
| **관측·캔버스·비전 프로브 & 120Hz 반사 (5)** | `list_apps`, `get_app_state`, `query`, `probe_screen`, `wait_for` | 실행 중 창 열거, 순수 게임 캔버스(`client_only`) 및 UIA 트리 조회, `<1ms` RGB/FNV-1a/무게중심 프로브, `120Hz` 신호 대기 및 `<2ms` `@centroid` 자동 반응 |
| **Zero-Copy 공유 메모리 링 (1)** | `manage_shared_frame` | `Local\TerkirdoFrame_*` 64B `TKSM` Seqlock 더블 버퍼 시작/중지/상태 조회 (`owner_pid` 워치독 지원) |
| **포인터·궤적·3D 카메라·키보드 & `humanize` (10)** | `click`, `move_mouse`, `follow_trajectory`, `drag`, `scroll`, `type_text`, `press_key`, `set_value`, `select_text`, `perform_secondary_action` | `client`/`window`/`screen` 좌표계, 엄격한 `pid:<id>` 격리, `humanize` 궤적, 60–120Hz QPC 웨이포인트, RawInput 3D 카메라 회전, UIA 값 주입 |
| **게임 콤보 & 안전 제어 (2)** | `batch_actions`, `release_inputs` | 단일 `ActionLock` 내 최대 200스텝 매크로 배치 실행 및 모든 홀드 입력 긴급 해제 |
| **OS 클립보드 (2)** | `get_clipboard`, `set_clipboard` | UTF-16 텍스트 및 PNG 이미지 원자적 읽기/쓰기 (`paste: true` 즉시 붙여넣기 지원) |
| **창 관리 (1)** | `manage_window` | `focus`, `minimize`, `maximize`, `restore`, `move`, `resize` 6대 원자적 창 제어 (150ms PID-HWND 캐시) |
| **멀티모니터 & ROI 캡처 (1)** | `capture_screen` | DXGI 1.2 GPU 캡처 + GDI 폴백, `<3~5ms` ROI 부분 영역 캡처, `raw=true` 무손실/저지연 바이너리 스트리밍 |
| **프로세스 수명주기 (2)** | `launch_app`, `close_app` | 인자/따옴표 안전 고속 실행 및 `WM_CLOSE` 정상 종료(+ 강제 종료 폴백) |

---

## 🚀 빌드 및 빠른 시작

### 요구 사항
- **OS**: Windows 10 / 11 (x64)
- **SDK**: [.NET 10 SDK](https://dotnet.microsoft.com/)

### 소스 빌드 및 단일 실행 파일 패키징
```powershell
# 디버그/릴리스 빌드
dotnet build TerkirdoComputer.csproj -c Release

# 단일 독립 실행 파일 생성 (release/terkirdocomputer.exe)
dotnet publish TerkirdoComputer.csproj -c Release -r win-x64 -o release
```

---

### [트랙 1] MCP Stdio 연동
원클릭 자동 등록 명령으로 각 AI 클라이언트에 MCP 서버를 바로 연결할 수 있습니다:
```powershell
.\terkirdocomputer.exe install-codex-mcp
.\terkirdocomputer.exe install-claude-mcp
.\terkirdocomputer.exe install-gemini-mcp
.\terkirdocomputer.exe install-opencode-mcp
```

수동 JSON 설정이 필요한 클라이언트(Cursor, Windsurf, Claude Desktop 등)는 아래와 같이 등록합니다:
```json
{
  "mcpServers": {
    "terkirdo-computer": {
      "command": "C:\\path\\to\\terkirdocomputer.exe",
      "args": ["mcp"]
    }
  }
}
```

---

### [트랙 2] Hot-Plug 스킬 + REST API & WebSocket 데몬
에이전트 설정 재부팅 없이 데몬을 띄워 REST API, WebSocket(`/ws/control`), Swagger UI를 즉시 사용할 수 있습니다.

```powershell
# 1. 로컬 데몬 기동 (기본 포트 5005, Swagger UI: http://127.0.0.1:5005/swagger)
.\terkirdocomputer.exe serve --port 5005

# (선택) 원격 에이전트/Docker/WSL 접속 허용 시
.\terkirdocomputer.exe serve --port 7836 --allow-remote --token my-secret-token

# 2. 에이전트 스킬 디렉터리에 SKILL.md 배포
.\terkirdocomputer.exe install-skill
```

#### 탐색(Discovery) 및 호출 예시
데몬 기동 시 `~/.terkirdo/serve.url`, `~/.terkirdo/serve.port`, `~/.terkirdo/serve.token` 파일이 생성되며, `GET /discover` 엔드포인트를 통해 활성 URL을 조회할 수 있습니다.

```bash
TOKEN=$(cat ~/.terkirdo/serve.token)
BASE_URL=$(curl -s http://127.0.0.1:5005/discover | grep -o '"activeUrl":"[^"]*' | cut -d'"' -f4)

# 실행 중인 앱 목록 조회
curl -s -H "Authorization: Bearer $TOKEN" "$BASE_URL/api/apps"

# 순수 클라이언트 게임 캔버스 스냅샷 조회
curl -s -H "Authorization: Bearer $TOKEN" "$BASE_URL/api/apps/pid:12345/state?client_only=true&include_ax=false"

# 생체모방 자연스러운 궤적 클릭
curl -s -X POST -H "Content-Type: application/json" -H "Authorization: Bearer $TOKEN" \
  "$BASE_URL/api/actions/click" \
  -d '{"pid": 12345, "x": 640, "y": 360, "coordinate_space": "client", "click_method": "hardware", "humanize": "natural"}'
```

---

### [트랙 3] 단독 CLI 실행
```powershell
# 환경 진단 (DPI 인식, 권한, DXGI 지원 여부)
.\terkirdocomputer.exe doctor --json

# 영구 설정 관리 (input.humanize, vision.waitPollHz 등)
.\terkirdocomputer.exe config list

# 앱 목록 및 UI/클라이언트 스냅샷
.\terkirdocomputer.exe list-apps
.\terkirdocomputer.exe snapshot notepad --client-only

# 120Hz 픽셀/영역 신호 대기 후 @centroid 자동 클릭
.\terkirdocomputer.exe wait-for --pid 12345 --coordinate-space client --poll-hz 120 `
  --regions '[{"label":"orb","x":0,"y":0,"width":320,"height":180,"target_hex":"#FF3355","min_match_count":20}]' `
  --on-match '{"type":"click","target":"@centroid:orb","click_method":"hardware"}'

# 모든 홀드 입력 긴급 해제
.\terkirdocomputer.exe release-inputs
```

---

## 🛡️ 보안 설계
- **기본 루프백 격리 (`127.0.0.1`)**: 명시적으로 `--allow-remote`를 지정하지 않는 한 외부 네트워크 접근을 차단합니다.
- **Host 헤더 및 Origin 검증**: 비루프백 Host 헤더(DNS Rebinding) 및 `/ws/control`의 비인가/`null` Origin 요청(CSWSH)을 `403 Forbidden`으로 차단합니다.
- **256비트 암호학적 Bearer 토큰**: `Authorization: Bearer <TOKEN>` 또는 `X-Terkirdo-Token: <TOKEN>` 헤더로만 인증하며, URL 쿼리 문자열(`?token=`) 전달은 보안상 `400 Bad Request`로 거부합니다.
- **동시성 직렬화 (`ActionLock`)**: `SemaphoreSlim` 기반으로 하드웨어 입력 경합을 방지하며, `wait_for` 루프는 락 외부에서 비차단으로 동작합니다.
- **공유 메모리 로컬 한정**: `manage_shared_frame`(`/api/vision/shared-frame/*`)은 `127.0.0.1` 로컬 호출만 허용합니다.

---

## 🏗️ 디렉터리 구조

```text
TerkirdoComputer/
├── Program.cs                        # 진입점 (UTF-8 스트림 및 CLI 실행)
├── TerkirdoComputer.csproj           # .NET 10 단일 파일 프로젝트 정의
├── Cli/
│   ├── CommandLineParser.cs          # CLI 서브커맨드 라우터 (mcp, serve, doctor, config 등)
│   └── Installers.cs                 # MCP 클라이언트 및 SKILL.md 원클릭 인스톨러
├── Core/
│   ├── Models.cs                     # 스냅샷, 요소, 도구, 비전/궤적 도메인 모델
│   ├── OcuConfig.cs                  # 영구 설정 관리 (~/.terkirdo/config.json)
│   ├── ProblemDetails.cs             # RFC 7807 호환 구조화 오류 응답 모델
│   └── TextSelectionResolver.cs      # 텍스트 선택 범위 해석기
├── Mcp/
│   ├── McpServer.cs                  # JSON-RPC 2.0 Stdio MCP 서버
│   ├── ProtocolModels.cs             # MCP 프로토콜 모델
│   └── ToolHandlers.cs               # 24개 원자적 도구 정의 및 실행 핸들러
├── Rest/
│   ├── ActionLock.cs                 # SemaphoreSlim 기반 하드웨어 액션 직렬화 잠금
│   ├── RestEndpoints.cs              # REST + /ws/control + /discover 라우팅 및 보안 미들웨어
│   ├── RestServer.cs                 # Kestrel 인프로세스 데몬 및 인도인접 게이트웨이 호스트
│   ├── SwaggerDocHelper.cs           # OpenAPI 3.0 스펙 및 Swagger UI 생성기
│   ├── TokenManager.cs               # 256비트 Bearer 토큰 및 메타데이터 파일 관리
│   └── WebSocketControlHub.cs        # Full-Duplex WS 제어 + 32B TKRF 바이너리 스트리머
├── Windows/
│   ├── ActiveInputTracker.cs         # 홀드 중인 키/마우스 버튼 추적 및 긴급 해제
│   ├── AutomationService.cs          # FlaUI.UIA3 UI 트리 순회 및 캔버스 스냅샷 서비스
│   ├── ClipboardService.cs           # UTF-16 텍스트 / PNG 이미지 클립보드 제어
│   ├── DxgiCapture.cs                # DirectX 11 DXGI 1.2 GPU Desktop Duplication 엔진
│   ├── FramePool.cs                  # 고빈도 캡처용 메모리 버퍼 풀
│   ├── HumanizedPhysics.cs           # Minimum-Jerk + Bezier 생체모방 물리 엔진
│   ├── InputInjector.cs              # 3단계 입력 주입 (app_post / stealth / hardware)
│   ├── NativeMethods.cs              # Win32 API P/Invoke 및 DPI/UIPI 유틸리티
│   ├── PrecisionTimer.cs             # 60Hz–120Hz 단조 증가 QPC 정밀 타이머
│   ├── ProcessManager.cs             # 프로세스 실행 및 정상/강제 종료 관리
│   ├── ScreenCapture.cs              # DXGI + GDI 폴백 멀티모니터/ROI 캡처
│   ├── ScreenProbe.cs                # <1ms RGB / FNV-1a 해시 / 무게중심 비전 프로브
│   ├── SharedFrameStreamService.cs   # 64B TKSM Seqlock 공유 메모리 프레임 스트리머
│   ├── VisionWaitService.cs          # 120Hz 비차단 신호 대기 및 <2ms @centroid 반사
│   └── WindowManager.cs              # PID-HWND 캐시 및 창 상태/위치/크기 제어
└── Skills/
    └── terkirdo-computer-use/
        └── SKILL.md                  # AI 에이전트용 전체 API·프로토콜·레시피 상세 가이드
```

---

## 📚 상세 문서
모든 REST/WebSocket/TKSM 바이너리 스펙, Python 비전 루프 레시피, 전체 CLI 옵션 및 오류 코드는 [Skills/terkirdo-computer-use/SKILL.md](Skills/terkirdo-computer-use/SKILL.md)에서 확인할 수 있습니다.
