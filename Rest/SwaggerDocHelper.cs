namespace TerkirdoComputer.Rest;

public static class SwaggerDocHelper
{
    public static string GetOpenApiJson(int port)
    {
        return $$"""
{
  "openapi": "3.0.3",
  "info": {
    "title": "TerkirdoComputer REST & WebSocket Real-Time I/O API",
    "description": "Zero-Dependency Windows Computer Use & Pure Real-Time I/O Engine (24-Tool Suite) REST + Full-Duplex WebSocket Daemon by Terukirdo.",
    "version": "2.0.0"
  },
  "servers": [
    {
      "url": "http://127.0.0.1:{{port}}",
      "description": "Local loopback daemon"
    }
  ],
  "components": {
    "securitySchemes": {
      "BearerAuth": {
        "type": "http",
        "scheme": "bearer",
        "description": "Provide the 256-bit Bearer token from ~/.terkirdo/serve.token"
      },
      "CustomToken": {
        "type": "apiKey",
        "in": "header",
        "name": "X-Terkirdo-Token",
        "description": "Custom token header for agent integration"
      }
    }
  },
  "security": [
    { "BearerAuth": [] },
    { "CustomToken": [] }
  ],
  "paths": {
    "/health": {
      "get": {
        "summary": "Daemon Health Check",
        "security": [],
        "responses": {
          "200": { "description": "Daemon is healthy" }
        }
      }
    },
    "/discover": {
      "get": {
        "summary": "Gateway Port Discovery & Handoff (인도인접)",
        "description": "Returns active CUA daemon URL and port for multi-port handoff.",
        "security": [],
        "responses": {
          "200": { "description": "Active daemon discovery info" }
        }
      }
    },
    "/api/discover": {
      "get": {
        "summary": "Gateway Discovery Redirect",
        "description": "Public discovery alias that redirects to /discover for active CUA daemon handoff.",
        "security": [],
        "responses": {
          "200": { "description": "Active daemon discovery info" },
          "302": { "description": "Redirect to /discover" }
        }
      }
    },
    "/api/doctor": {
      "get": {
        "summary": "Windows Runtime Environment Diagnostics",
        "responses": {
          "200": { "description": "Runtime diagnostics output" }
        }
      }
    },
    "/api/apps": {
      "get": {
        "summary": "List Running GUI Applications",
        "responses": {
          "200": { "description": "List of running top-level applications" }
        }
      }
    },
    "/api/apps/{query}/state": {
      "get": {
        "summary": "Get Application UI & Game Canvas Snapshot",
        "description": "Returns window bounds, client bounds/offset, window state flags, UI Automation tree, and Base64 screenshot (PNG or JPEG per OcuConfig, with optional client_only pure game canvas crop and ROI).",
        "parameters": [
          { "name": "query", "in": "path", "required": true, "schema": { "type": "string" }, "description": "Process name, window title, numeric PID, or strict 'pid:<id>'" },
          { "name": "text_limit", "in": "query", "required": false, "schema": { "type": "string" }, "description": "Max characters (integer or 'max')" },
          { "name": "max_tree_nodes", "in": "query", "required": false, "schema": { "type": "integer", "default": 1200 } },
          { "name": "max_tree_depth", "in": "query", "required": false, "schema": { "type": "integer", "default": 64 } },
          { "name": "include_ax", "in": "query", "required": false, "schema": { "type": "boolean", "default": true }, "description": "Set false to skip UI Automation tree traversal and return screenshot only" },
          { "name": "include_screenshot", "in": "query", "required": false, "schema": { "type": "boolean", "default": true }, "description": "Set false to skip screenshot capture for fast UIA-only queries" },
          { "name": "client_only", "in": "query", "required": false, "schema": { "type": "boolean", "default": false }, "description": "If true, captures pure client canvas excluding title bar and DWM shadow borders" },
          { "name": "window_placement", "in": "query", "required": false, "schema": { "type": "string", "enum": ["keep", "agent_display", "restore"], "default": "keep" }, "description": "Window placement policy for this observation" },
          { "name": "region_x", "in": "query", "required": false, "schema": { "type": "number" }, "description": "Optional window/client-relative ROI X coordinate in pixels" },
          { "name": "region_y", "in": "query", "required": false, "schema": { "type": "number" }, "description": "Optional window/client-relative ROI Y coordinate in pixels" },
          { "name": "region_width", "in": "query", "required": false, "schema": { "type": "number" }, "description": "Optional window/client-relative ROI width in pixels (> 0)" },
          { "name": "region_height", "in": "query", "required": false, "schema": { "type": "number" }, "description": "Optional window/client-relative ROI height in pixels (> 0)" }
        ],
        "responses": {
          "200": { "description": "Application snapshot JSON" },
          "400": { "description": "Invalid arguments" },
          "404": { "description": "Application not found" }
        }
      }
    },
    "/api/apps/{query}/screenshot": {
      "get": {
        "summary": "Get Raw Window, Client Canvas, or ROI Screenshot",
        "description": "Returns raw image/png or image/jpeg binary stream directly without Base64 roundtrip, supporting client_only pure game canvas crop and optional ROI.",
        "parameters": [
          { "name": "query", "in": "path", "required": true, "schema": { "type": "string" } },
          { "name": "client_only", "in": "query", "required": false, "schema": { "type": "boolean", "default": false } },
          { "name": "region_x", "in": "query", "required": false, "schema": { "type": "number" } },
          { "name": "region_y", "in": "query", "required": false, "schema": { "type": "number" } },
          { "name": "region_width", "in": "query", "required": false, "schema": { "type": "number" } },
          { "name": "region_height", "in": "query", "required": false, "schema": { "type": "number" } },
          { "name": "format", "in": "query", "required": false, "schema": { "type": "string", "enum": ["png", "jpg", "jpeg"] } },
          { "name": "max_dimension", "in": "query", "required": false, "schema": { "type": "integer", "minimum": 64 } }
        ],
        "responses": {
          "200": {
            "description": "Direct image stream (PNG or JPEG)",
            "content": {
              "image/png": {
                "schema": { "type": "string", "format": "binary" }
              },
              "image/jpeg": {
                "schema": { "type": "string", "format": "binary" }
              }
            }
          },
          "400": { "description": "Invalid ROI or format arguments" },
          "404": { "description": "Application not found or screenshot unavailable" }
        }
      }
    },
    "/api/actions/click": {
      "post": {
        "summary": "Perform Mouse Click or Stateful Hold/Release",
        "description": "Click, press down, or release a mouse button by element_index or pixel coordinates (x, y) in window, client, or screen space.",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "required": ["app"],
                "properties": {
                  "app": { "type": "string" },
                  "pid": { "type": "integer", "minimum": 1 },
                  "element_index": { "type": "string" },
                  "x": { "type": "number" },
                  "y": { "type": "number" },
                  "coordinate_space": { "type": "string", "enum": ["window", "client", "screen"] },
                  "click_count": { "type": "integer", "default": 1 },
                  "mouse_button": { "type": "string", "enum": ["left", "right", "middle"], "default": "left" },
                  "button_state": { "type": "string", "enum": ["click", "down", "up"], "default": "click" },
                  "hold_ms": { "type": "integer", "minimum": 0, "maximum": 60000 },
                  "click_method": { "type": "string", "enum": ["auto", "accessibility", "app_post", "stealth", "global", "hardware"], "default": "auto" },
                  "humanize": { "oneOf": [ { "type": "boolean" }, { "type": "string", "enum": ["none", "subtle", "natural", "fast"] } ] },
                  "skip_snapshot": { "type": "boolean", "default": false, "description": "Skip post-action UIA tree rebuild and screenshot capture" }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "Result snapshot or fast ack after click" },
          "400": { "description": "Invalid click parameters" },
          "404": { "description": "Target application/PID not found" }
        }
      }
    },
    "/api/actions/move-mouse": {
      "post": {
        "summary": "Move Mouse Cursor or Rotate 3D Game Camera",
        "description": "Move cursor to window/client/screen (x, y) coordinates or send pure RawInput relative motion (delta_x, delta_y) via SendInput(MOUSEEVENTF_MOVE) for 3D FPS/TPS camera rotation.",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "properties": {
                  "app": { "type": "string" },
                  "pid": { "type": "integer", "minimum": 1 },
                  "x": { "type": "number" },
                  "y": { "type": "number" },
                  "coordinate_space": { "type": "string", "enum": ["window", "client", "screen"] },
                  "delta_x": { "type": "number" },
                  "delta_y": { "type": "number" },
                  "steps": { "type": "integer", "minimum": 1, "maximum": 240, "default": 1 },
                  "duration_ms": { "type": "integer", "minimum": 0, "maximum": 5000, "default": 0 },
                  "method": { "type": "string", "enum": ["hardware", "global", "auto", "stealth", "app_post"] },
                  "humanize": { "oneOf": [ { "type": "boolean" }, { "type": "string", "enum": ["none", "subtle", "natural", "fast"] } ] },
                  "skip_snapshot": { "type": "boolean", "default": true }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "Mouse movement executed" },
          "400": { "description": "Invalid movement parameters" },
          "404": { "description": "Target application/PID not found" }
        }
      }
    },
    "/api/actions/trajectory": {
      "post": {
        "summary": "Play Continuous 60Hz-120Hz Monotonic QPC Cursor Trajectory",
        "description": "Executes 1..2400 timed waypoints scheduled against a monotonic QPC epoch with 0.0ms cumulative drift and automatic hold_button release safety.",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "required": ["waypoints"],
                "properties": {
                  "app": { "type": "string" },
                  "pid": { "type": "integer", "minimum": 1 },
                  "waypoints": {
                    "type": "array",
                    "minItems": 1,
                    "maxItems": 2400,
                    "items": { "type": "object" }
                  },
                  "interval_ms": { "type": "number", "minimum": 0, "maximum": 5000, "default": 16.6667 },
                  "coordinate_space": { "type": "string", "enum": ["window", "client", "screen"] },
                  "method": { "type": "string", "enum": ["hardware", "global", "stealth", "app_post", "auto"], "default": "hardware" },
                  "mouse_button": { "type": "string", "enum": ["left", "right", "middle"], "default": "left" },
                  "hold_button": { "type": "boolean", "default": false },
                  "humanize": { "oneOf": [ { "type": "boolean" }, { "type": "string", "enum": ["none", "subtle", "natural", "fast"] } ] },
                  "skip_snapshot": { "type": "boolean", "default": true }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "Trajectory execution metrics including maxDriftMs" },
          "400": { "description": "Invalid trajectory parameters" },
          "404": { "description": "Target application/PID not found" }
        }
      }
    },
    "/ws/control": {
      "get": {
        "summary": "Real-Time Full-Duplex WebSocket Control & Binary Frame Stream (TKRF)",
        "description": "RFC 6455 full-duplex WebSocket endpoint supporting JSON control commands (ping, click, move_mouse, press_key, type_text, drag, scroll, follow_trajectory, batch_actions, release_inputs, probe_screen, wait_for, manage_shared_frame, subscribe_frames, unsubscribe_frames), atomic single-agent takeover (Close 4001), CSWSH Origin protection, and 32-byte TKRF (<IBBHIHHQii) binary frame streaming.",
        "responses": {
          "101": { "description": "Switching Protocols to full-duplex WebSocket" },
          "400": { "description": "Query token forbidden or missing WebSocket upgrade header" },
          "401": { "description": "Missing or invalid Bearer / X-Terkirdo-Token header" },
          "403": { "description": "Untrusted or null Origin header (CSWSH defense)" }
        }
      }
    },
    "/api/vision/probe": {
      "post": {
        "summary": "Sub-Millisecond UIA-Less Pixel, FNV-1a Region Hash & Centroid Sensor",
        "description": "Captures a single tight bounding box without PNG/JPEG/Base64 encoding and computes RGB point matches, 64-bit FNV-1a region hashes, color match ratios, and matching-pixel center-of-mass (centroidX, centroidY).",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "properties": {
                  "app": { "type": "string" },
                  "pid": { "type": "integer", "minimum": 1 },
                  "display": { "type": "string", "default": "1" },
                  "coordinate_space": { "type": "string", "enum": ["client", "window", "screen"] },
                  "points": {
                    "type": "array",
                    "maxItems": 256,
                    "items": { "type": "object" }
                  },
                  "regions": {
                    "type": "array",
                    "maxItems": 32,
                    "items": { "type": "object" }
                  }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "VisionProbeResult with points, regions, fnv1aHash, and centroidX/centroidY" },
          "400": { "description": "Invalid probe parameters" },
          "404": { "description": "Target application/PID or display not found" }
        }
      }
    },
    "/api/vision/wait": {
      "post": {
        "summary": "120Hz Low-Level Pixel/Hash/Centroid Reflex & Sub-2ms In-Process Reaction",
        "description": "Polls low-level pixel/region signals outside ActionLock at up to 240Hz (120Hz default) with initial-hash delta detection, consecutive_hits noise rejection, and optional <2ms in-process on_match reaction supporting @centroid / @centroid:<label> moving-target coordinates.",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "properties": {
                  "app": { "type": "string" },
                  "pid": { "type": "integer", "minimum": 1 },
                  "display": { "type": "string", "default": "1" },
                  "coordinate_space": { "type": "string", "enum": ["client", "window", "screen"] },
                  "points": { "type": "array", "maxItems": 256, "items": { "type": "object" } },
                  "regions": { "type": "array", "maxItems": 32, "items": { "type": "object" } },
                  "poll_hz": { "type": "integer", "minimum": 1, "maximum": 240, "default": 120 },
                  "timeout_ms": { "type": "integer", "minimum": 10, "maximum": 60000, "default": 5000 },
                  "match_mode": { "type": "string", "enum": ["all", "any"], "default": "all" },
                  "consecutive_hits": { "type": "integer", "minimum": 1, "maximum": 10, "default": 1 },
                  "on_match": { "type": "object" }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "VisionWaitResult (both match and normal timeout return 200 OK)" },
          "400": { "description": "Invalid wait_for predicate or reaction parameters" },
          "404": { "description": "Target application/PID or display not found" },
          "429": { "description": "Maximum concurrent wait_for limit exceeded" }
        }
      }
    },
    "/api/vision/shared-frame": {
      "get": {
        "summary": "Get Zero-Copy Shared Memory Frame Ring Status (TKSM)",
        "description": "Returns current status, Local\\TerkirdoFrame_<hex> mapping name, dimensions, MaxSlotBytes, and published frame count for local Python mmap / C# consumers.",
        "responses": {
          "200": { "description": "SharedFrameStatusResult JSON" },
          "403": { "description": "Non-loopback caller rejected" }
        }
      }
    },
    "/api/vision/shared-frame/start": {
      "post": {
        "summary": "Start Zero-Copy Shared Memory Frame Publisher (64-Byte TKSM Seqlock Double-Buffer)",
        "description": "Allocates Local\\TerkirdoFrame_<32hex> pagefile shared memory with a 64-byte TKSM (<IHHQQQQIIHHBBHQ) Seqlock + Double-Buffer header for <0.05ms zero-copy BGRA32 reads.",
        "requestBody": {
          "required": false,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "properties": {
                  "app": { "type": "string" },
                  "pid": { "type": "integer", "minimum": 1 },
                  "display": { "type": "string" },
                  "client_only": { "type": "boolean", "default": true },
                  "region_x": { "type": "number" },
                  "region_y": { "type": "number" },
                  "region_width": { "type": "number" },
                  "region_height": { "type": "number" },
                  "max_fps": { "type": "integer", "minimum": 1, "maximum": 240, "default": 60 },
                  "only_on_change": { "type": "boolean", "default": true },
                  "owner_pid": { "type": "integer", "minimum": 1 }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "Started SharedFrameStatusResult with mapName" },
          "400": { "description": "Invalid parameters" },
          "403": { "description": "Non-loopback caller rejected" },
          "404": { "description": "Target application/PID or display not found" }
        }
      }
    },
    "/api/vision/shared-frame/stop": {
      "post": {
        "summary": "Stop Zero-Copy Shared Memory Frame Publisher",
        "description": "Drains the publisher task, marks TKSM Flags |= 0x04 (Closed), and safely unmaps and disposes the MemoryMappedFile.",
        "responses": {
          "200": { "description": "Stopped SharedFrameStatusResult" },
          "403": { "description": "Non-loopback caller rejected" }
        }
      }
    },
    "/api/actions/secondary": {
      "post": {
        "summary": "Perform Secondary Action",
        "description": "Invoke a secondary UI Automation pattern action exposed by an element (Invoke, Toggle, Select, Expand, Collapse, ScrollIntoView, SetFocus).",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "required": ["app", "element_index", "action"],
                "properties": {
                  "app": { "type": "string" },
                  "pid": { "type": "integer", "minimum": 1 },
                  "element_index": { "type": "string" },
                  "action": { "type": "string" },
                  "skip_snapshot": { "type": "boolean", "default": false }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "Action executed" }
        }
      }
    },
    "/api/actions/scroll": {
      "post": {
        "summary": "Scroll Element or Window/Client Coordinates",
        "description": "Scroll an accessibility element by element_index or wheel-scroll at coordinates (x, y) via UIA, app_post, or hardware SendInput.",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "required": ["app", "direction"],
                "properties": {
                  "app": { "type": "string" },
                  "pid": { "type": "integer", "minimum": 1 },
                  "direction": { "type": "string", "enum": ["up", "down", "left", "right"] },
                  "element_index": { "type": "string" },
                  "x": { "type": "number" },
                  "y": { "type": "number" },
                  "coordinate_space": { "type": "string", "enum": ["window", "client", "screen"] },
                  "pages": { "type": "number", "default": 1 },
                  "scroll_method": { "type": "string", "enum": ["auto", "app_post", "stealth", "global", "hardware"], "default": "auto" },
                  "skip_snapshot": { "type": "boolean", "default": false }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "Scroll executed" },
          "400": { "description": "Invalid scroll parameters" }
        }
      }
    },
    "/api/actions/drag": {
      "post": {
        "summary": "Drag and Drop",
        "description": "Perform background app_post or smooth physical hardware/stealth SendInput drag.",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "required": ["app", "from_x", "from_y", "to_x", "to_y"],
                "properties": {
                  "app": { "type": "string" },
                  "pid": { "type": "integer", "minimum": 1 },
                  "from_x": { "type": "number" },
                  "from_y": { "type": "number" },
                  "to_x": { "type": "number" },
                  "to_y": { "type": "number" },
                  "coordinate_space": { "type": "string", "enum": ["window", "client", "screen"] },
                  "duration_ms": { "type": "integer", "minimum": 0, "maximum": 10000, "default": 80 },
                  "drag_method": { "type": "string", "enum": ["auto", "app_post", "stealth", "global", "hardware"], "default": "auto" },
                  "humanize": { "oneOf": [ { "type": "boolean" }, { "type": "string", "enum": ["none", "subtle", "natural", "fast"] } ] },
                  "skip_snapshot": { "type": "boolean", "default": false }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "Drag executed" }
        }
      }
    },
    "/api/actions/type": {
      "post": {
        "summary": "Type Literal Text",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "required": ["app", "text"],
                "properties": {
                  "app": { "type": "string" },
                  "pid": { "type": "integer", "minimum": 1 },
                  "text": { "type": "string" },
                  "key_method": { "type": "string", "enum": ["auto", "app_post", "stealth", "global", "hardware"], "default": "auto" },
                  "humanize": { "oneOf": [ { "type": "boolean" }, { "type": "string", "enum": ["none", "subtle", "natural", "fast"] } ] },
                  "skip_snapshot": { "type": "boolean", "default": false }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "Text typed" }
        }
      }
    },
    "/api/actions/press-key": {
      "post": {
        "summary": "Press, Hold, or Release Keyboard Key",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "required": ["app", "key"],
                "properties": {
                  "app": { "type": "string" },
                  "pid": { "type": "integer", "minimum": 1 },
                  "key": { "type": "string" },
                  "key_method": { "type": "string", "enum": ["auto", "app_post", "stealth", "global", "hardware"], "default": "auto" },
                  "key_state": { "type": "string", "enum": ["tap", "down", "up"], "default": "tap" },
                  "action": { "type": "string", "enum": ["tap", "down", "up"] },
                  "hold_ms": { "type": "integer", "minimum": 0, "maximum": 60000 },
                  "repeat": { "type": "integer", "minimum": 1, "maximum": 100, "default": 1 },
                  "repeat_interval_ms": { "type": "integer", "minimum": 0, "maximum": 5000, "default": 15 },
                  "humanize": { "oneOf": [ { "type": "boolean" }, { "type": "string", "enum": ["none", "subtle", "natural", "fast"] } ] },
                  "skip_snapshot": { "type": "boolean", "default": false }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "Key action executed" },
          "400": { "description": "Invalid key parameters" }
        }
      }
    },
    "/api/actions/batch": {
      "post": {
        "summary": "Execute In-Process Macro / Combo Sequence",
        "description": "Execute 1..200 input/wait steps inside a single ActionLock with zero HTTP roundtrip overhead and automatic release-on-error safety.",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "required": ["steps"],
                "properties": {
                  "app": { "type": "string" },
                  "pid": { "type": "integer", "minimum": 1 },
                  "steps": {
                    "type": "array",
                    "minItems": 1,
                    "maxItems": 200,
                    "items": { "type": "object" }
                  },
                  "stop_on_error": { "type": "boolean", "default": true },
                  "release_on_error": { "type": "boolean", "default": true },
                  "humanize": { "oneOf": [ { "type": "boolean" }, { "type": "string", "enum": ["none", "subtle", "natural", "fast"] } ] },
                  "skip_snapshot": { "type": "boolean", "default": true }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "Batch execution summary" },
          "400": { "description": "Batch step validation or execution failure" }
        }
      }
    },
    "/api/actions/release-inputs": {
      "post": {
        "summary": "Panic-Release All Held Keys and Mouse Buttons",
        "description": "Immediately releases all tracked hardware/background keys, mouse buttons, and stuck modifiers.",
        "responses": {
          "200": { "description": "Summary of released keys and mouse buttons" }
        }
      }
    },
    "/api/actions/set-value": {
      "post": {
        "summary": "Set Element Value via UI Automation",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "required": ["app", "element_index", "value"],
                "properties": {
                  "app": { "type": "string" },
                  "pid": { "type": "integer", "minimum": 1 },
                  "element_index": { "type": "string" },
                  "value": { "type": "string" },
                  "skip_snapshot": { "type": "boolean", "default": false }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "Value set" }
        }
      }
    },
    "/api/actions/query": {
      "post": {
        "summary": "Targeted Accessibility Query",
        "description": "Search an application's accessibility tree by text and/or role without capturing a full screenshot.",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "required": ["app"],
                "properties": {
                  "app": { "type": "string" },
                  "pid": { "type": "integer", "minimum": 1 },
                  "text": { "type": "string" },
                  "role": { "type": "string" },
                  "exact": { "type": "boolean", "default": false },
                  "limit": { "type": "integer", "minimum": 1, "maximum": 100, "default": 20 },
                  "max_nodes": { "type": "integer", "minimum": 1, "maximum": 5000, "default": 500 },
                  "window_id": { "type": "integer", "minimum": 1 }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "Matching elements with session-scoped element_index" },
          "400": { "description": "Invalid query arguments" }
        }
      }
    },
    "/api/actions/select-text": {
      "post": {
        "summary": "Select Text or Position Cursor",
        "description": "Select text or place cursor before/after matched text with optional prefix/suffix disambiguation.",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "required": ["app", "element_index", "text"],
                "properties": {
                  "app": { "type": "string" },
                  "pid": { "type": "integer", "minimum": 1 },
                  "element_index": { "type": "string" },
                  "text": { "type": "string" },
                  "prefix": { "type": "string" },
                  "suffix": { "type": "string" },
                  "selection": { "type": "string", "enum": ["text", "cursor_before", "cursor_after"], "default": "text" },
                  "skip_snapshot": { "type": "boolean", "default": false }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "Text selected" }
        }
      }
    },
    "/api/clipboard": {
      "get": {
        "summary": "Get Clipboard Content",
        "description": "Read text or image data from the Windows clipboard.",
        "parameters": [
          { "name": "format", "in": "query", "required": false, "schema": { "type": "string", "enum": ["text", "image", "all"], "default": "text" } }
        ],
        "responses": {
          "200": { "description": "Clipboard content JSON" },
          "423": { "description": "Clipboard locked by another process" }
        }
      },
      "post": {
        "summary": "Set Clipboard Content",
        "description": "Write text or image data to clipboard, with optional automatic paste (Ctrl+V).",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "properties": {
                  "text": { "type": "string" },
                  "image_base64": { "type": "string" },
                  "paste": { "type": "boolean", "default": false }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "Write result" },
          "423": { "description": "Clipboard locked" }
        }
      }
    },
    "/api/windows/{app}/manage": {
      "post": {
        "summary": "Manage Application Window",
        "description": "Perform focus, minimize, maximize, restore, move, or resize on target window.",
        "parameters": [
          { "name": "app", "in": "path", "required": true, "schema": { "type": "string" }, "description": "Process name or window title" }
        ],
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "required": ["action"],
                "properties": {
                  "pid": { "type": "integer" },
                  "action": { "type": "string", "enum": ["focus", "minimize", "maximize", "restore", "move", "resize"] },
                  "x": { "type": "number" },
                  "y": { "type": "number" },
                  "width": { "type": "number" },
                  "height": { "type": "number" }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "Window bounds and state after manipulation" },
          "404": { "description": "Application not found" }
        }
      }
    },
    "/api/screens": {
      "get": {
        "summary": "List Displays & Monitors",
        "description": "Enumerate connected physical and virtual display monitors.",
        "responses": {
          "200": { "description": "List of displays" }
        }
      }
    },
    "/api/screens/screenshot": {
      "get": {
        "summary": "Multi-Monitor or ROI Screenshot",
        "description": "Capture the full virtual desktop, a specific display, or an ultra-fast ROI sub-rectangle (x, y, width, height) with zero-Base64 raw streaming.",
        "parameters": [
          { "name": "display", "in": "query", "required": false, "schema": { "type": "string", "default": "all" } },
          { "name": "raw", "in": "query", "required": false, "schema": { "type": "boolean", "default": false } },
          { "name": "x", "in": "query", "required": false, "schema": { "type": "number" } },
          { "name": "y", "in": "query", "required": false, "schema": { "type": "number" } },
          { "name": "width", "in": "query", "required": false, "schema": { "type": "number" } },
          { "name": "height", "in": "query", "required": false, "schema": { "type": "number" } },
          { "name": "format", "in": "query", "required": false, "schema": { "type": "string", "enum": ["png", "jpg", "jpeg"] } },
          { "name": "max_dimension", "in": "query", "required": false, "schema": { "type": "integer", "minimum": 64 } }
        ],
        "responses": {
          "200": { "description": "Screen snapshot JSON or raw image/png or image/jpeg binary stream" },
          "400": { "description": "Invalid ROI or format arguments" },
          "404": { "description": "Display not found" }
        }
      }
    },
    "/api/processes/launch": {
      "post": {
        "summary": "Launch Process Safely",
        "description": "Start an application without invoking shell command interpreters.",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "required": ["path"],
                "properties": {
                  "path": { "type": "string" },
                  "args": { "type": "string" },
                  "working_dir": { "type": "string" }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "Launched process info" }
        }
      }
    },
    "/api/processes/close": {
      "post": {
        "summary": "Close Process Gracefully",
        "description": "Send WM_CLOSE and wait for graceful termination, with timeout fallback to force kill.",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "properties": {
                  "app": { "type": "string" },
                  "pid": { "type": "integer" },
                  "force": { "type": "boolean", "default": false },
                  "timeout_ms": { "type": "integer", "default": 3000 }
                }
              }
            }
          }
        },
        "responses": {
          "200": { "description": "Termination status" },
          "404": { "description": "Process not found" }
        }
      }
    }
  }
}
""";
    }

    public static string GetSwaggerHtml()
    {
        return """
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>TerkirdoComputer REST API - Swagger UI</title>
  <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist@5.11.0/swagger-ui.css" />
  <style>
    body { margin: 0; background: #fafafa; font-family: sans-serif; }
    .topbar { background-color: #1b1b1b !important; }
    .swagger-ui .topbar .topbar-wrapper { padding: 8px 0; }
    .swagger-ui .topbar a { color: #f472b6 !important; font-weight: bold; font-size: 1.2rem; text-decoration: none; }
  </style>
</head>
<body>
  <div id="swagger-ui"></div>
  <script src="https://unpkg.com/swagger-ui-dist@5.11.0/swagger-ui-bundle.js"></script>
  <script src="https://unpkg.com/swagger-ui-dist@5.11.0/swagger-ui-standalone-preset.js"></script>
  <script>
    window.onload = () => {
      window.ui = SwaggerUIBundle({
        url: '/openapi.json',
        dom_id: '#swagger-ui',
        presets: [
          SwaggerUIBundle.presets.apis,
          SwaggerUIStandalonePreset
        ],
        layout: "StandaloneLayout",
        persistAuthorization: true
      });
    };
  </script>
</body>
</html>
""";
    }
}
