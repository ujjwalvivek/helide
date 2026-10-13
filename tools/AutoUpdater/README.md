# Helide AutoUpdater

> **Deprecated.** Helide updates itself: Releases no longer ship `AutoUpdater.exe`, and neither the release workflow nor CheckNInstall builds or installs it. The code still builds though.

TUI (terminal) CLI updater for Helide. Checks GitHub releases, downloads updates (zip or exe), installs beside Helide, and restarts.

## Usage

Standalone:

```sh
dotnet run
.\AutoUpdater.exe
```

It checks GitHub, downloads and installs the update if there is one, then restarts Helide. When there is nothing to install, or something fails, it holds the result on screen until a key is pressed.

In the full-screen view, `T` switches between the mocha and oled palettes and `Ctrl+C` quits.

From Helide app (About page):

- Click "Check for Updates" in About window
- Prompts to download/install via MessageBox
- Updates Helide.exe in `%LOCALAPPDATA%\Programs\Helide`

Auto-check on restart:

- `App.xaml.cs` calls `CheckForUpdatesSilently()`
- Shows popup notification if update available

## Integration

Files:

- `Program.cs` - standalone TUI updater
- `src/Services/HelideUpdater.cs` - library used by Helide app
- `AboutWindow.xaml.cs` - wired to `HelideUpdater`
- `App.xaml.cs` - startup auto-check with popup

Update source:

- `https://api.github.com/repos/ujjwalvivek/helide/releases/latest`
- Downloads `.zip` or `.exe` asset from GitHub releases
- Extracts `.exe` from zip, installs beside `Helide.exe`

Build:

```sh
dotnet build
dotnet build -c Release
```
