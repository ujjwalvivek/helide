# Check & Install

Interactive TUI that checks the CLIs Helide shells out to, installs what is missing, repairs PATH, publishes the single-file builds, and installs them. The user PATH is guarded at 2000 characters and the write refused rather than risk truncating a PATH that every shell on the machine depends on.

## Run

```sh
dotnet run --project tools/CheckNInstall              # interactive menu
dotnet run --project tools/CheckNInstall -- --check   # report only, exit 0 when ready
dotnet run --project tools/CheckNInstall -- --full    # every phase, no prompts
```

Keyboard: arrows or `1`-`6` to move, `Enter` to select, `Esc` to quit.

## Phases

| #   | Phase       | What it does                                                                                                         |
| --- | ----------- | -------------------------------------------------------------------------------------------------------------------- |
| 1   | Check       | lazygit, hx, yazi, opencode, codex -- by the executable that is on PATH                                              |
| 2   | Install     | `winget install` each missing one                                                                                    |
| 3   | Repair PATH | `WinGet\Links` can be empty, so `WinGet\Packages` is added to the user PATH                                          |
| 4   | Build       | `-r win-x64 --self-contained`, flattens its own single-file bundle up to `bin\Release\net8.0-windows\*​-win-x64.exe` |
| 5   | Install     | `%LOCALAPPDATA%\Programs\Helide\`, on the user PATH, Start Menu entry, and `AutoUpdater.exe` beside it               |
