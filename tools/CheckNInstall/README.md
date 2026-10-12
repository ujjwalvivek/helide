# Check & Install

Interactive TUI that checks the CLIs Helide shells out to, installs what is missing, repairs PATH, publishes the single-file builds, and installs them. The user PATH is guarded at 2000 characters and the write refused rather than risk truncating a PATH that every shell on the machine depends on.

## Run

```sh
dotnet run --project tools/CheckNInstall              # interactive menu
dotnet run --project tools/CheckNInstall -- --check   # report only, exit 0 when ready
dotnet run --project tools/CheckNInstall -- --full    # every phase, no prompts
```

It opens on a status screen (required tools, optional agents, what is installed) and offers three workflows. While a workflow runs, the result stays on screen until a key is pressed.

| # | Workflow               | Does                                                     |
| - | ---------------------- | -------------------------------------------------------- |
| 1 | Set up everything      | Fix tools, then build and install Helide and AutoUpdater |
| 2 | Fix tools              | Check, `winget install` what is missing, repair PATH     |
| 3 | Build & install Helide | Publish Helide and AutoUpdater, then install both        |

## Phases

| #   | Phase       | What it does                                                                                                        |
| --- | ----------- | ------------------------------------------------------------------------------------------------------------------- |
| 1   | Check       | lazygit, hx, yazi -- by the executable that is on PATH. opencode, codex and claude are optional and only reported   |
| 2   | Install     | `winget install` each missing one                                                                                   |
| 3   | Repair PATH | `WinGet\Links` can be empty, so `WinGet\Packages` is added to the user PATH                                         |
| 4   | Build       | `-r win-x64 --self-contained`, flattens its own single-file bundle up to `bin\Release\net8.0-windows\*-win-x64.exe` |
| 5   | Install     | `%LOCALAPPDATA%\Programs\Helide\`, on the user PATH, Start Menu entry, and `AutoUpdater.exe` beside it              |
