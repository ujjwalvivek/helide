# Helide

Start at a calm welcome screen, choose a project folder, and Helide opens four working surfaces:

- lazygit on the left
- Helix in the dominant center pane
- Terminal below it
- OpenCode on the right

The panes are backed by native Windows Terminal rendering through `EasyWindowsTerminalControl`. Each panel starts a PowerShell process inside its own Windows pseudoconsole. The native control is GPU-backed and auto-resizes its pseudoconsole as each WPF pane changes size. Full-screen apps such as Helix and lazygit retain their native alternate-screen semantics. Recent projects, the last workspace, restored window bounds, and pane ratios are persisted under `%LOCALAPPDATA%\Helide`.

## Prerequisites

- Windows 10 1809 or later
- .NET 8 SDK
- `pwsh`, `hx`, `lazygit`, and `opencode` on PATH

## Build and run

```powershell
dotnet build
dotnet run

# a project directory can be passed directly for smoke tests
dotnet run -- E:\path\to\project
```

## License

Helide is released under the MIT License. See [LICENSE](LICENSE).
