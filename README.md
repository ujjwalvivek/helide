# Helide

Start at a calm welcome screen, choose a project folder, and Helide opens four working surfaces:

- lazygit or yazi on the left (swappable from the status bar)
- Helix in the dominant center pane
- Terminal below it
- OpenCode or Codex on the right

The panes are backed by native Windows Terminal rendering through `EasyWindowsTerminalControl`. Each GPU-backed panel starts a PowerShell process inside its own Windows pseudoconsole `ConPTY`. Full-screen apps such as Helix, lazygit, and yazi retain their native alternate-screen semantics. Settings and configs are persisted under `%LOCALAPPDATA%\Helide`.

## Prerequisites

- Windows 10 1809 or later
- .NET 8 SDK
- `pwsh`, `hx`, `lazygit`, `yazi`, `opencode`, and `codex` on PATH

## Build and run

```powershell
dotnet build
dotnet run

# a project directory can be passed directly for smoke tests
dotnet run -- E:\path\to\project
```

## LSP Status & Diagnostics Panel

**Not viable in current architecture.**

Helide wraps Helix externally via ConPTY. Helix runs as a child process inside a terminal; Helide never touches its internals. Helix's LSP client lives inside Helix's process, no IPC, no socket, no exported API to query LSP server status or diagnostics from outside.

**Process scanning** Doesn't work. Most LSP servers run as `node.exe`, `dotnet`, or `pwsh` with arguments. The process name is the runtime and not the server. Matching on arguments breaks when args are refactored. Even if it worked, it tells us nothing about state (starting/ready/failed). Moreover, Helix logs LSP events to `%LOCALAPPDATA%\helix\` but has no stable, documented format. Fragile to parsing. No structured events for diagnostics. 

In contrast, Neovim shows `:LspInfo` because its LSP client also lives inside Neovim's process.

**PATH shim** (nucleotide-lsp-proxy approach): Create wrapper executables named like each LSP server, intercept `textDocument/publishDiagnostics` JSON-RPC messages. This is the only externally-viable approach but requires maintaining a Rust/C# wrapper that correctly implements LSP stdio framing for every server. High maintenance, very brittle.

Only two real paths forward:

1. **Fork Helix** (Rust): like what Nucleotide did. Add LSP status/diagnostics as native features. Largest commitment. Requires Rust, tracking upstream.
2. **PATH shim** (C#/Rust wrapper): intercept LSP traffic at the process boundary. Works but every server needs a correct wrapper. Highest fragility-to-value ratio.

Neither is worth doing now.

## License

Helide is released under the MIT License. See [LICENSE](LICENSE).
