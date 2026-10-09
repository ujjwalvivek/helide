# Helide

Start at a calm welcome screen, choose a project folder, and Helide opens four working surfaces right where you think they should be:

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

Not viable in the current architecture, and worth writing down why before someone tries it again.

The trouble is where Helix sits. It runs as a child process inside a ConPTY, and Helide only ever sees its terminal output. The LSP client lives inside that process, and there's no IPC, no socket, and no exported API to ask it about server status or diagnostics. Neovim can show `:LspInfo` for exactly the reason it can't help us here: its client is also inside the process, which is where the information lives.

The obvious workaround, scanning for running language servers, doesn't hold up. Most of them run as `node.exe`, `dotnet`, or `pwsh`, so the process name tells you the runtime and not the server. Matching on arguments breaks the first time they're refactored, and even a perfect match says nothing about state - starting, ready, and failed all look identical. Helix does log LSP events under `%LOCALAPPDATA%\helix\`, but the format isn't stable or documented, so parsing it is guesswork, and there are no structured diagnostic events to hook into regardless.

That leaves the PATH shim approach Nucleotide used: wrapper executables named after each LSP server that intercept `textDocument/publishDiagnostics` on the way past. It's the only externally viable route, but it means maintaining a Rust or C# wrapper that gets LSP stdio framing right for every server we care about. High maintenance, and brittle in the way that only announces itself when someone's toolchain moves.

So there's fork Helix and add it natively like Nucleotide did, which is the biggest commitment and means tracking upstream, or the shim, which works and has the worst fragility-to-value ratio of anything I've looked at. Neither is worth doing now.

## Remote Server (SSH)

Not viable in the current architecture either, though this one is closer to being a good idea later.

The remote server shape is sound. The problem is that this codebase can't carry it yet, and a few specifics make that concrete.

Start with the bridge, since that part is already done. Every pane is a local ConPTY feeding a local Win32 renderer, so the plumbing a remote server would need is sitting there already. Adding a separate proxy process to pipe remote output into that ConPTY would insert a hop, a resize relay, and a new way for panes to fail, in exchange for nothing.

The agent is the harder constraint. The design ships `helide-remote` to the host and replaces it whenever the version doesn't match, and self-contained .NET means publishing per RID - linux-x64, linux-arm64, musl, osx-arm64 - at roughly 70-90MB each, pushed to every machine we connect to. Rust cross-compiled to static musl is 3-10MB per architecture. At that size difference the deployment stops being a feature and starts being the reason not to try.

Session persistence turned out to be the part that doesn't need solving. Reattaching after a dropped connection is what tmux already does, and tmux is already installed on every remote host we'd care about. What's genuinely missing is structured filesystem and git RPC, and that's a much larger build than terminals are.

Tooling is also still Windows-shaped today. Panes hardcode `.exe` when they launch `lazygit`, `yazi`, or `hx`, and pass a local Windows path as the working directory. Remote panes need a session abstraction that separates local executables from remote commands, and there isn't one yet. That abstraction has to come first regardless of which direction this goes.

One more thing worth flagging: doing LSP remotely would be two projects, not one. We don't have external LSP access locally either, per the section above, so remoteizing diagnostics means building the local story first.

In the meantime `ssh -tt host tmux new -A -s` covers most of the value at close to no cost, and the full remote server remains the right end state once the session abstraction and a Rust agent are in place.

## Codex session ids, and why the capture layer is shaped the way it is

Worth writing down, because the bug was one line and it took four wrong fixes to find. Codex holds its rollout file open for as long as its TUI is up, so a default `new StreamReader(path)` throws. Every id was swallowed by an empty `catch` and returned as null, so every pane came back from a restart with no id and launched bare. What settled it was a counter in a throwaway log showing `enumerated=137 skippedBeforeSnapshot=0 unreadable=1`: the file was found, passed both filters, and died on open.

Four constraints this code depends on:

- `FileShare.ReadWrite | FileShare.Delete`, or nothing is readable at all.
- Resuming forks the rollout name, so the file carries the resumed id alongside a new one. Take the id from `payload.session_id` on the first line; parsing the filename yields fragments `codex resume` rejects, and those fragments get persisted and re-fail on every launch.
- The cutoff marks rollout files created after app start. Taken afterwards, terminal startup is enough for panes to have written theirs, and they get discarded for the whole session.
- Codex writes no rollout until a conversation starts, so a pane you never message reopens empty every time and never accumulates history.

## Multiple Processes

Two projects can sit side by side, and a second launch of an already-open project asks that window to come forward instead of opening a **duplicate**.

**`SwitchToThisWindow` is undocumented.** It is the reliable way to force a window to the foreground when the request comes from a different process, which is exactly this case. `SetForegroundWindow` is refused in that situation. The usual alternative, toggling `Topmost`, works but visibly flashes/jumps the window. Undocumented does not mean unstable, so if activation ever misbehaves this is the first thing to look at.

## License

Helide is released under the MIT License. See [LICENSE](LICENSE).
