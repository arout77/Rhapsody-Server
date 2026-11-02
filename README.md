# Rhapsody Server

> A modern local development stack for PHP — with built-in profiling, AI-agent integration, and first-class support for the [Rhapsody framework](https://rhapsodyframework.com).
> #### Note: This software is currently in Alpha stage of development - expect bugs. Do not rely on it for mission-critical work. A stable, release candidate expected to arrive by 11/2026.

**Rhapsody Server** is a drop-in alternative to XAMPP / WAMP built for developers who want more than "Apache + MySQL + PHP in a folder." It ships with observability, AI-agent tooling, and a scaffolded Rhapsody framework workflow out of the box.

---

## ✨ Features

### 🔭 Built-in Profiling
- **SQL query profiler** — see every query, its duration, and its call site, per HTTP request.
- **HTTP request/response inspector** — status codes, headers, payloads, and timings.
- **N+1 query detection** for Rhapsody ORM patterns, with suggested fixes.
- **Request replay & diff** — re-run a profiled request with modified parameters and compare query profiles side-by-side.
- **"Copy AI Context"** — export stack traces, SQL, and config as a clean Markdown block ready to paste into an AI assistant.

### 🧱 Rhapsody Framework Scaffolding
- One-click project creation from Rhapsody templates.
- Guided configuration for databases, mail, queues, and environment variables.
- `rhapsody doctor` — validates dependencies, `.env` files, permissions, and runtime compatibility.

### 🤖 AI Agent Integration (MCP)
Rhapsody Server exposes its environment to external AI agents (Cursor, Claude Code, Cline) via the **Model Context Protocol**:
- **Tools**: `create_site`, `run_sql_query`, `read_php_error_logs`, `toggle_profiler`, and more.
- **Resources**: live stack state (CPU/memory, service status, active DB connections, recent HTTP 500s).
- Optional **local model** support for offline code explanation and Rhapsody scaffolding.

### 🧩 Per-Site Service Isolation
- Assign a different **PHP version** and **database engine** to each site.
- Run legacy projects and new Rhapsody projects side-by-side without conflict.

### 📬 Complete the Local Dev Loop
- **Built-in mail catcher** — inspect outgoing mail without installing MailHog.
- **One-click tunneling** — share a local site publicly via a bundled reverse proxy.

---

## 🚀 Getting Started

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download) or later
- Windows 10/11 or a modern Linux distribution

### Installation

Download a prebuilt release from the [Releases](https://github.com/arout77/Rhapsody-Server/releases) page. An unsigned installer will be available in the coming days, check back soon for the download link.

### Create Your First Site

1. Launch Rhapsody Server.
2. Click **New Site → Rhapsody Template**.
3. Choose a PHP version and database engine.
4. Rhapsody Server scaffolds the project and starts the required services.
5. Open `http://localhost:<port>` in your browser.

---

## 🧠 Using the MCP Server

Rhapsody Server runs an MCP server that AI agents can connect to.

**Example: configure Cursor**

Add the following to your MCP client configuration:

```json
{
  "mcpServers": {
    "Rhapsody Server": {
      "command": "RhapsodyServer-mcp",
      "args": ["--port", "7420"]
    }
  }
}
```

Once connected, an agent can inspect profiler output, run queries, and create sites directly.

---

## 🏗️ Architecture

| Layer | Technology |
|-------|------------|
| Desktop UI / Daemon | C# (.NET 8) |
| Service orchestration | Native Windows Services / systemd |
| PHP runtime | Per-site, multi-version |
| Database | MySQL / MariaDB (per-site) |
| Profiling hooks | Rhapsody middleware + PHP extension |
| AI integration | Model Context Protocol (MCP) |

---

## 🗺️ Roadmap

- [ ] macOS support
- [ ] Native Rhapsody test runner integration
- [ ] Redis / queue service management
- [ ] Profiler timeline view
- [ ] Plugin API for third-party service integrations

---

## 🤝 Contributing

Contributions are welcome! Please read [CONTRIBUTING.md](CONTRIBUTING.md) before submitting a pull request.

1. Fork the repository.
2. Create a feature branch (`git checkout -b feature/my-feature`).
3. Commit your changes.
4. Open a pull request.

---

## 📄 License

Rhapsody Server is released under the [MIT License](LICENSE).

---

## 💬 Community

- Issues: [GitHub Issues](https://github.com/your-org/Rhapsody Server/issues)
- Discussions: [GitHub Discussions](https://github.com/your-org/Rhapsody Server/discussions)
- Docs: [docs.Rhapsody Server.dev](https://docs.Rhapsody Server.dev)
