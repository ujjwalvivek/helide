# check lazygit, helix, yazi, opencode, codex on PATH and suggest to install if missing.
# dotnet publish -c Release -r win-x64 --self-contained

<#
.SYNOPSIS
    Helide - check dependencies, then build and install Helide onto PATH.

.DESCRIPTION
    Runs in phases:

      1. Check     every required CLI, by its real executable name. Reported in
                    one pass. The Helix binary is `hx`, not `helix`.
      2. Install   winget-install anything missing.
      3. FixPath   winget portable packages are exposed through
                    %LOCALAPPDATA%\Microsoft\WinGet\Links, which can end up
                    empty even while the packages are installed correctly. Add
                    the install directory to the user PATH instead.
      4. Build     dotnet publish only. PublishSingleFile is a publish-time
                    property, so plain build cannot produce the single exe, and
                    publish runs the build as its first step anyway. The csproj
                    then flattens the result: the bundle moves up to
                    bin\Release\net8.0-windows\Helide-win-x64.exe and the loose
                    build output and publish folder are deleted. The updater project
                    publishes the same way, into its own bin folder.
      5. Install   copy that exe to %LOCALAPPDATA%\Programs\Helide as Helide.exe,
                    add it to the user PATH, and create a Start Menu entry, so it
                    is found everywhere an installed app is looked for. The updater
                    is copied beside it as AutoUpdater.exe -- no PATH entry and no
                    Start Menu entry, because it is an internal tool that locates
                    the app by looking next to itself.

    With no flags it is read-only: it checks and reports, then exits.

.PARAMETER Install
    Run every phase: install missing CLIs, repair PATH, build, install Helide.

.PARAMETER FixPath
    Only repair PATH for CLIs that are installed but not exposed.

.PARAMETER Build
    Only run the publish step.

.PARAMETER InstallApp
    Only copy the published exe and put it on PATH.

.PARAMETER NoSelfContained
    Framework-dependent publish instead of self-contained. Much smaller, but
    requires the .NET 8 Desktop Runtime on the machine.

#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$FixPath,
    [switch]$Build,
    [switch]$InstallApp,
    [switch]$NoSelfContained
)

$ErrorActionPreference = 'Stop'

# Full run implies everything.
if ($Install) {
    $FixPath   = $true
    $Build     = $true
    $InstallApp = $true
}

$ProjectRoot  = $PSScriptRoot
$ProjectFile  = Join-Path $ProjectRoot 'Helide.csproj'
$Artifact     = Join-Path $ProjectRoot 'bin\Release\net8.0-windows\Helide-win-x64.exe'
$InstallDir   = Join-Path $env:LOCALAPPDATA 'Programs\Helide'
$ExeName      = 'Helide.exe'

# The updater is a project of its own and is excluded from Helide.csproj, so it has
# to be published and installed separately. It lands beside Helide.exe because that
# is where it looks for the app.
$UpdaterProject  = Join-Path $ProjectRoot 'tools\AutoUpdater\AutoUpdater.csproj'
$UpdaterArtifact = Join-Path $ProjectRoot 'tools\AutoUpdater\bin\Release\net8.0-windows\AutoUpdater-win-x64.exe'
$UpdaterExeName  = 'AutoUpdater.exe'

# Label -> executable name on PATH, and winget package id.
$Requirements = [ordered]@{
    'lazygit'  = @{ Exe = 'lazygit';  Winget = 'JesseDuffield.lazygit' }
    'helix'    = @{ Exe = 'hx';       Winget = 'Helix.Helix' }
    'yazi'     = @{ Exe = 'yazi';     Winget = 'sxyazi.yazi' }
    'opencode' = @{ Exe = 'opencode'; Winget = 'SST.opencode' }
    'codex'    = @{ Exe = 'codex';    Winget = 'OpenAI.Codex' }
}

function Write-Phase {
    param([string]$Text)
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ('-' * $Text.Length)
}

function Test-Tool {
    param([string]$Exe)
    return $null -ne (Get-Command $Exe -ErrorAction SilentlyContinue)
}

function Get-WingetInstallDir {
    param([string]$Exe)

    $root = "$env:LOCALAPPDATA\Microsoft\WinGet\Packages"
    if (-not (Test-Path $root)) { return $null }

    $hit = Get-ChildItem -Path $root -Filter "$Exe.exe" -Recurse -File `
        -ErrorAction SilentlyContinue | Select-Object -First 1

    if ($hit) { return $hit.DirectoryName }
    return $null
}

function Add-ToUserPath {
    param([string]$Directory)

    if (-not $Directory) { return 'failed' }

    $current = [Environment]::GetEnvironmentVariable('Path', 'User')

    $entries = @()
    if ($current) {
        $entries = @($current -split ';' | Where-Object { $_ -and $_.Trim() })
    }

    if ($entries -contains $Directory) { return 'present' }

    # Guard the combined length before writing. A truncated PATH breaks every
    # shell on the machine, so refuse rather than write something partial.
    $combined = ($entries + $Directory) -join ';'
    if ($combined.Length -gt 2000) {
        Write-Warning "user PATH would reach $($combined.Length) chars; refusing to write"
        return 'failed'
    }

    [Environment]::SetEnvironmentVariable('Path', $combined, 'User')
    return 'added'
}

function Invoke-Check {
    $onPath = @()
    $offPath = @()

    foreach ($label in $Requirements.Keys) {
        $req = $Requirements[$label]
        if (Test-Tool $req.Exe) {
            Write-Host ("  [ok]      {0,-9} -> {1}" -f $label, $req.Exe) -ForegroundColor DarkGreen
            $onPath += $label
        } else {
            Write-Host ("  [missing] {0,-9} -> {1}" -f $label, $req.Exe) -ForegroundColor Yellow
            $offPath += $label
        }
    }

    return [pscustomobject]@{ OnPath = $onPath; OffPath = $offPath }
}

function Invoke-InstallTools {
    param([string[]]$Labels)

    foreach ($label in $Labels) {
        $id = $Requirements[$label].Winget
        Write-Host ("  winget install -e --id {0}" -f $id) -ForegroundColor DarkGray
        & winget install -e --id $id --accept-package-agreements --accept-source-agreements | Out-Null
    }
}

function Invoke-FixPath {
    param([string[]]$Labels)

    $stillMissing = @()
    $added = @()

    foreach ($label in $Labels) {
        $req = $Requirements[$label]

        # Something between the check and now may have resolved it.
        if (Test-Tool $req.Exe) { continue }

        $dir = Get-WingetInstallDir -Exe $req.Exe
        if (-not $dir) {
            $stillMissing += $label
            continue
        }

        $status = Add-ToUserPath -Directory $dir
        switch ($status) {
            'added' {
                Write-Host ("  [fixed]   {0,-9} + {1}" -f $label, $dir) -ForegroundColor DarkGreen
                $added += $label
            }
            'present' {
                Write-Host ("  [ok]      {0,-9} already on user PATH" -f $label) -ForegroundColor DarkGreen
            }
            default {
                Write-Host ("  [FAILED]  {0,-9} could not add {1}" -f $label, $dir) -ForegroundColor Red
                $stillMissing += $label
            }
        }
    }

    if ($added.Count) {
        Write-Host ''
        Write-Host '  Restart your shell for PATH changes to apply.' -ForegroundColor Yellow
    }

    return $stillMissing
}

function Invoke-Build {
    if (-not (Test-Path $ProjectFile)) {
        Write-Host "  no Helide.csproj at $ProjectRoot" -ForegroundColor Red
        return $false
    }

    $publishArgs = @('publish', '-c', 'Release', '-r', 'win-x64')
    if ($NoSelfContained) {
        $publishArgs += '--self-contained:false'
    } else {
        $publishArgs += '--self-contained'
    }

    Push-Location $ProjectRoot
    try {
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { return $false }

        # Helide.csproj excludes the updater from its own compile, so nothing above
        # built it. Same single-file flatten applies, so it lands in its own bin
        # folder rather than next to the app's.
        if (-not (Test-Path $UpdaterProject)) {
            Write-Host "  no AutoUpdater.csproj under tools\" -ForegroundColor DarkGray
            return $true
        }

        $updaterSelfContained = if ($NoSelfContained) { '--self-contained:false' } else { '--self-contained' }
        & dotnet publish $UpdaterProject -c Release -r win-x64 $updaterSelfContained
        if ($LASTEXITCODE -ne 0) {
            Write-Host '  updater publish failed' -ForegroundColor Red
            return $false
        }

        Write-Host '  updater publish succeeded' -ForegroundColor DarkGreen
        return $true
    } finally {
        Pop-Location
    }
}

function Invoke-InstallApp {
    if (-not (Test-Path $Artifact)) {
        Write-Host "  no published exe at $Artifact" -ForegroundColor Red
        Write-Host '  run the build phase first (or pass -Build).' -ForegroundColor DarkGray
        return $false
    }

    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

    # Renamed on install: the file on PATH is `helide`, and the RID belongs to the
    # build artifact's name, where win-x64 and arm64 side by side would collide.
    $exe = Join-Path $InstallDir $ExeName
    Copy-Item -Path $Artifact -Destination $exe -Force

    $exeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ("  copied {0} ({1} MB) -> {2}" -f $ExeName, $exeMb, $InstallDir) -ForegroundColor DarkGray

    $status = Add-ToUserPath -Directory $InstallDir
    switch ($status) {
        'added'   { Write-Host ("  [fixed]   added to user PATH: {0}" -f $InstallDir) -ForegroundColor DarkGreen }
        'present' { Write-Host ("  [ok]      already on user PATH: {0}" -f $InstallDir) -ForegroundColor DarkGreen }
        default   { Write-Host ("  [FAILED]  could not add {0} to user PATH" -f $InstallDir) -ForegroundColor Red; return $false }
    }

    # PATH only makes helide runnable from a shell. Without a Start Menu entry the
    # app is invisible everywhere Windows actually looks for installed apps, and
    # searching for it comes back with the project folder instead.
    $shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Helide.lnk'
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $InstallDir
    $link.Description = 'Helide terminal workspace'
    $link.Save()
    Write-Host ("  Start Menu entry -> {0}" -f $shortcut) -ForegroundColor DarkGray

    Write-Host ''
    Write-Host '  Restart your shell, then run: helide' -ForegroundColor Yellow
    return $true
}

function Invoke-InstallUpdater {
    if (-not (Test-Path $UpdaterArtifact)) {
        Write-Host "  no published updater at $UpdaterArtifact" -ForegroundColor Yellow
        Write-Host '  skipping - Helide itself is still installed' -ForegroundColor DarkGray
        return $false
    }

    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

    $exe = Join-Path $InstallDir $UpdaterExeName
    Copy-Item -Path $UpdaterArtifact -Destination $exe -Force

    $exeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ("  copied {0} ({1} MB) -> {2}" -f $UpdaterExeName, $exeMb, $InstallDir) -ForegroundColor DarkGray

    # Deliberately no PATH entry and no Start Menu shortcut. It is an internal tool:
    # nothing points the user at it, and it needs nothing but sitting next to
    # Helide.exe, which is exactly where it goes looking for the app.
    return $true
}

# ---------------------------------------------------------------- run

Write-Phase 'Check'

$state = Invoke-Check
$unresolved = @($state.OffPath)

if ($Install -and $unresolved.Count) {
    Write-Phase 'Install missing tools'
    Invoke-InstallTools -Labels $unresolved
}

if ($FixPath -and $unresolved.Count) {
    Write-Phase 'Repair PATH'
    $unresolved = @(Invoke-FixPath -Labels $unresolved | Where-Object { $_ })
}

if ($unresolved.Count) {
    Write-Host ''
    Write-Host 'Still missing' -ForegroundColor Red
    foreach ($label in $unresolved) {
        Write-Host ("  winget install -e --id {0}" -f $Requirements[$label].Winget) -ForegroundColor Yellow
    }
    Write-Host ''
    exit 1
}

$exitCode = 0

if ($Build) {
    Write-Phase 'Build'
    if (-not (Invoke-Build)) {
        Write-Host '  publish failed' -ForegroundColor Red
        exit 1
    }
    Write-Host '  publish succeeded' -ForegroundColor DarkGreen
}

if ($InstallApp) {
    Write-Phase 'Install Helide'
    if (-not (Invoke-InstallApp)) {
        $exitCode = 1
    }

    # Kept separate from Invoke-InstallApp so a missing updater is reported on its
    # own line instead of being hidden as "the install failed".
    Write-Phase 'Install updater'
    if (-not (Invoke-InstallUpdater)) {
        Write-Host '  helide will update itself through its own pill instead' -ForegroundColor DarkGray
    }
}

if ($exitCode -eq 0) {
    Write-Host ''
    Write-Host 'Done.' -ForegroundColor DarkGreen
    if (-not ($Build -or $InstallApp)) {
        Write-Host 'Re-run with -Install to build and install Helide.' -ForegroundColor DarkGray
    }
    Write-Host ''
}

exit $exitCode
