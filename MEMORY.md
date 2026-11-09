# ABB RobotStudio MCP Server — Project Memory & DevSecOps Architecture

## 📌 Project Overview
- **Repository:** `LiskinLabs/abb-robotstudio-mcp`
- **Tech Stack:** TypeScript (Node.js 20+), Model Context Protocol (MCP SDK), C# (.NET 4.8 RobotStudio Add-In).
- **Purpose:** Full-featured MCP server providing 66 tools for controlling and inspecting ABB RobotStudio via Robot Web Services (RWS), RobotStudio SDK, and TcpListener Add-In.
- **Current Version:** v3.1.0

---

## 🛡️ DevSecOps & CI/CD Pipeline Matrix (All-Green Standard)
| Workflow | File | Triggers | Description |
| :--- | :--- | :--- | :--- |
| **CI Verification** | `.github/workflows/ci.yml` | `push`, `pull_request` | TypeScript compilation, MCP Server build & automated unit tests |
| **Dependency Security** | `.github/workflows/dependency-review.yml` | `push`, `pull_request`, weekly | Supply chain vulnerability audit with npm audit |
| **CodeQL Advanced SAST** | `.github/workflows/codeql.yml` | `push`, `pull_request`, weekly | Static code security analysis for TypeScript/JavaScript, SARIF artifact persistence |

---
*Updated: 2026-10-23 | Liskin Labs Engineering*
