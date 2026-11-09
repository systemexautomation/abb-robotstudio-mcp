# ABB RobotStudio MCP Server — LiskinLabs Edition

> **v3.1.0 (Safety-First & Hardened Edition)** — Advanced MCP server for ABB RobotStudio and ABB robot controllers with TcpListener transport, 72 MCP tools, AutoLoad, RAPID syntax validator, pre-simulation safety guards, and path-traversal protection.

Model Context Protocol (MCP) server for ABB RobotStudio (desktop application) and ABB robot controllers (OmniCore / IRC5 / Virtual Controllers).

Exposes **72 tools** across two transports:

- **`rs_*`** (42 tools) — RobotStudio SDK via TcpListener in-process Add-In. Inspect/edit stations, targets, paths, modules, variables, I/O signals, configs. Drive simulation with safety guards. Capture screenshots.
- **`rws_*`** (30 tools) — Robot Web Services REST API direct to controller. Motors, program execution, I/O signals, filesystem, mastership, event log, runtime target controller switching.

Tested with: **ABB RobotStudio 2026 (26.2) + Virtual Controller (RobotWare 7)** on Windows 11 x64.

> **Installation guide (French): [INSTALLATION.md](INSTALLATION.md)** — prerequisites, build, add-in install, Claude Desktop / Claude Code configuration, troubleshooting.

---

## ✨ Features & Architecture Highlights

| Feature | Original | LiskinLabs v3.1.0 |
|---|---|---|
| HTTP Transport | `HttpListener` — requires `netsh urlacl` (Admin) | **`TcpListener`** — zero admin rights required |
| Tool Count | 50 | **63** (+13 new tools) |
| Add-In Installation | Manual per-session "Load Add-In" | **AutoLoad** (`Bin\Addins` of the RobotStudio install, administrator rights needed once) |
| RAPID Validation | None (silent syntax failures) | ✅ **`rs_validate_rapid`** (structural AST block check) |
| Safety Guard | None (sim starts blindly) | ✅ **`rs_check_safety`** + pre-simulation error block |
| Path Traversal Defense | None | ✅ **`validateControllerPath`** ($SYSTEM protection, `..` filter) |
| RWS Target Switching | Static env var | ✅ **`rws_set_controller_url`** (runtime virtual ↔ real switch) |
| 3D Screenshot | ❌ | ✅ `rs_get_screenshot` (base64 PNG) |
| Controller Event Log | ❌ | ✅ `rs_get_execution_errors` & `rws_event_log` |
| Variable Inspection | ❌ | ✅ `rs_list_variables` (type & module filters) |
| Individual I/O Control | Stubs | ✅ `rs_read_io_signal` & `rs_write_io_signal` |
| Automated Testing | None | ✅ **`npm test`** (`node:test` suite in CI) |

---

## 🚀 Quick Start

### 1. Build

```bash
git clone https://github.com/LiskinLabs/abb-robotstudio-mcp.git
cd abb-robotstudio-mcp

# 1. Build TypeScript MCP Server
npm install
npm run build
npm test

# 2. Build C# Add-In
dotnet build addin/ClaudeBridge.csproj -c Release
```

### 2. Install Add-In to RobotStudio

Close RobotStudio completely first (it locks the DLL), then, in an **administrator** PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-addin.ps1 -RobotStudioVersion "2026"
```

The script copies `ClaudeBridge.dll` and `ClaudeBridge.rsaddin` to `C:\Program Files (x86)\ABB\RobotStudio 2026\Bin\Addins\ClaudeBridge` and `C:\Program Files (x86)\Common Files\ABB\RobotStudio\Addins\ClaudeBridge`.

### 3. Configure MCP Client

Add to your MCP client config (`.mcp.json` or `claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "abb-robotstudio": {
      "command": "node",
      "args": ["C:/Projects/01_Robotics/abb-robotstudio-mcp/dist/index.js"],
      "env": {
        "ABB_BRIDGE_URL": "http://localhost:58080",
        "ABB_RWS_URL": "https://127.0.0.1:80",
        "ABB_RWS_USER": "Default User",
        "ABB_RWS_PASS": "robotics"
      }
    }
  }
}
```

The RobotStudio 2026 virtual controller serves Robot Web Services over **HTTPS** (self-signed certificate, Basic authentication). The self-signed certificate is accepted only for `127.0.0.1` / `localhost` / `::1`.

### 4. Launch & Verify

1. Start **RobotStudio** and open your station with a Virtual Controller.
2. The Add-In loads automatically on startup (`AutoLoad: true`).
3. Verify connection:
```bash
curl http://localhost:58080/ping
```

---

## 🛠️ Complete Tool Index (72 Tools)

### SDK Tools (`rs_*`) — 42 tools
- **Connection & Status:** `rs_ping`, `rs_controller_status`, `rs_check_safety`
- **Station Management:** `rs_get_station`, `rs_get_station_logic`, `rs_get_station_objects`, `rs_get_object_parts`, `rs_create_part_from_body`, `rs_remove_part_bodies`, `rs_set_part_frame`, `rs_edit_station_logic`, `rs_get_smartcomponent_library`, `rs_list_config_signals`, `rs_create_config_signals`, `rs_save_station`, `rs_get_screenshot`
- **RAPID Management:** `rs_get_tasks`, `rs_get_modules`, `rs_read_module`, `rs_write_module`, `rs_validate_rapid`
- **Variables & Data:** `rs_read_variable`, `rs_write_variable`, `rs_list_variables`
- **I/O Signals:** `rs_get_io_signals`, `rs_read_io_signal`, `rs_write_io_signal`
- **Simulation Control:** `rs_start_simulation`, `rs_stop_simulation`, `rs_pause_simulation`, `rs_reset_simulation`, `rs_simulation_status`, `rs_set_sim_speed`, `rs_check_collisions`
- **Motion & Targets:** `rs_get_position`, `rs_get_paths`, `rs_get_path_targets`, `rs_create_path`, `rs_create_target`
- **Diagnostics & Config:** `rs_get_execution_errors`, `rs_read_config`, `rs_write_config`

### RWS Tools (`rws_*`) — 30 tools
- **Controller & Target:** `rws_set_controller_url`, `rws_controller_status`, `rws_restart_controller`
- **Execution & Safety:** `rws_set_motors`, `rws_start_program`, `rws_stop_program`, `rws_reset_pp`, `rws_execution_state`, `rws_get_speed_override`, `rws_set_speed_override`
- **RAPID & Modules:** `rws_get_tasks`, `rws_get_modules`, `rws_read_module`, `rws_write_module`, `rws_load_module_from_file`, `rws_unload_module`, `rws_save_program`, `rws_load_program`
- **Variables & I/O:** `rws_read_variable`, `rws_write_variable`, `rws_get_position`, `rws_get_io_signals`, `rws_read_io`, `rws_write_io`
- **Filesystem & Logs:** `rws_list_files`, `rws_read_file`, `rws_write_file`, `rws_event_log`
- **Mastership:** `rws_request_mastership`, `rws_release_mastership`

---

## 🔒 Safety Guardrails

- **Zero-Accident Policy:** `rs_start_simulation` and `rws_start_program` automatically scan the controller event log for active RAPID and syntax errors before allowing execution.
- **Path Sanitization:** File operations via RWS enforce `validateControllerPath`, rejecting any `..` directory traversal and forbidding access to `$SYSTEM` critical controller partitions.
- **BOM-Free UTF-8:** Code writes enforce CRLF line-endings and strip UTF-8 Byte Order Marks to guarantee error-free RAPID compiler parsing.

---

## 📜 License & Credits

- **Original Foundation:** [Elias Bitsch](https://github.com/eliasbitsch) — [abb-robotstudio-mcp](https://github.com/eliasbitsch/abb-robotstudio-mcp) (MIT License).
- **Architecture & Hardening:** [LiskinLabs](https://github.com/LiskinLabs) — Silvestr Liskin.
- License: [ISC](LICENSE)
