# Helide

Start at a calm welcome screen, choose a project folder, and Helide opens four working surfaces:

- lazygit or yazi on the left (swappable from the status bar)
- Helix in the dominant center pane
- Terminal below it
- OpenCode on the right

The panes are backed by native Windows Terminal rendering through `EasyWindowsTerminalControl`. Each panel starts a PowerShell process inside its own Windows pseudoconsole. The native control is GPU-backed and auto-resizes its pseudoconsole as each WPF pane changes size. Full-screen apps such as Helix, lazygit, and yazi retain their native alternate-screen semantics. Recent projects, the last workspace, restored window bounds, pane ratios, which left tool is selected, and which panels are collapsed are persisted under `%LOCALAPPDATA%\Helide`.

## Prerequisites

- Windows 10 1809 or later
- .NET 8 SDK
- `pwsh`, `hx`, `lazygit`, `yazi`, and `opencode` on PATH

### Yazi sidebar layout

The left panel is narrow, and yazi by default gives half its width to a preview pane. Point `[mgr] ratio` at the preview slot to make yazi behave like a sidebar. Set the value to `0` to hide a panel; the three entries are parent, current file list, and preview:

```toml
# %APPDATA%\yazi\config\yazi.toml
[mgr]
ratio = [1, 6, 0]
```

`[preview] max_width` and `max_height` cap image preview pixels rather than pane width; run `ya cache clear` after changing either.

## Build and run

```powershell
dotnet build
dotnet run

# a project directory can be passed directly for smoke tests
dotnet run -- E:\path\to\project
```

## License

Helide is released under the MIT License. See [LICENSE](LICENSE).
