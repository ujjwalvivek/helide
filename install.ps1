# Helide installer.
#
#   Invoke-RestMethod https://raw.githubusercontent.com/ujjwalvivek/helide/main/install.ps1 | Invoke-Expression
#
# Downloads the latest release, checks it against the checksum the release publishes,
# installs it to %LOCALAPPDATA%\Programs\Helide, puts `helide` on the user PATH and adds a
# Start Menu entry. Running it again updates or repairs the install.
#
# Options (a downloaded copy can be run directly; for the one-liner, wrap it):
#
#   .\install.ps1 -Force                   reinstall even when this version is already here
#   .\install.ps1 -Uninstall               remove Helide, its PATH entry and its shortcut
#   .\install.ps1 -InstallDir D:\Helide    install somewhere else; PATH and Start Menu are
#                                          left alone, so it is a plain portable copy
#
#   & ([scriptblock]::Create((Invoke-RestMethod https://raw.githubusercontent.com/ujjwalvivek/helide/main/install.ps1))) -Uninstall
#
# The tools Helide runs (yazi, lazygit, helix) are not installed here: the app checks for
# them on first launch and offers to install whatever is missing.
#
# Kept ASCII on purpose. Windows PowerShell 5.1 reads a script without a byte order mark
# as ANSI, so any non-ASCII character in this file would arrive as something else.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'An interactive console installer: its output is for the person at the console, in colour, and is not meant to be piped.')]
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$Force,
    [string]$InstallDir
)

# Everything runs inside this block. Piped into Invoke-Expression the script executes in the caller's
# own session, so a function or a changed preference defined at the top level would still be
# there after it finished.
$exitCode = & {
    param([switch]$Uninstall, [switch]$Force, [string]$InstallDir)

    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'

    try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { Write-Verbose $_.Exception.Message }
    Add-Type -AssemblyName System.Net.Http

    $repo = 'ujjwalvivek/helide'
    $archive = 'helide-win-x64.zip'
    $defaultDir = Join-Path $env:LOCALAPPDATA 'Programs\Helide'
    $portable = -not [string]::IsNullOrWhiteSpace($InstallDir)
    $dir = if ($portable) { [IO.Path]::GetFullPath($InstallDir) } else { $defaultDir }

    # ---------------------------------------------------------------- look

    # The theme the app is on, so this opens matching it. Same palettes as the other tools.
    $oled = $false
    try {
        $state = Join-Path $env:LOCALAPPDATA 'Helide\state.json'
        if (Test-Path -LiteralPath $state) {
            $oled = ((Get-Content -LiteralPath $state -Raw | ConvertFrom-Json).Theme -eq 'oled')
        }
    } catch { Write-Verbose $_.Exception.Message }

    $esc = [char]27
    $color = $false
    try {
        $color = (-not $env:NO_COLOR) -and (
            $env:WT_SESSION -or $PSVersionTable.PSVersion.Major -ge 7 -or $Host.UI.SupportsVirtualTerminal)
    } catch { Write-Verbose $_.Exception.Message }

    function Rgb([int[]]$rgb) { if ($color) { "$esc[38;2;$($rgb[0]);$($rgb[1]);$($rgb[2])m" } else { '' } }
    $reset = if ($color) { "$esc[0m" } else { '' }

    if ($oled) {
        $c = @{ brand = (Rgb 183,154,203); text = (Rgb 242,242,242); muted = (Rgb 176,176,176)
                faint = (Rgb 110,110,110); ok = (Rgb 143,207,143); bad = (Rgb 208,108,108)
                accent = (Rgb 255,255,255); rule = (Rgb 52,52,52) }
    } else {
        $c = @{ brand = (Rgb 203,166,247); text = (Rgb 205,214,244); muted = (Rgb 166,173,200)
                faint = (Rgb 108,112,134); ok = (Rgb 166,227,161); bad = (Rgb 243,139,168)
                accent = (Rgb 137,180,250); rule = (Rgb 69,71,90) }
    }

    # Glyphs the other tools use, built from code points so this file stays ASCII.
    $g = @{ ok = [string][char]0x221A; bad = [string][char]0x00D7; dot = [string][char]0x00B7
            skip = '-'; rule = [string][char]0x2500; full = [string][char]0x2588; track = [string][char]0x2591 }

    $interactive = $true
    try { $interactive = -not [Console]::IsOutputRedirected } catch { Write-Verbose $_.Exception.Message }

    $width = 60
    function Write-Rule { Write-Host ("  $($c.rule)" + ($g.rule * $width) + $reset) }

    function Write-Header($title) {
        $left = "  $($c.brand)helide$reset  $($c.muted)$title$reset"
        Write-Host ''
        Write-Host $left
        Write-Rule
    }

    # One checklist row. With a console it is rewritten in place as the step moves from
    # working to done; without one, only the finished row is written.
    function Write-Row($glyph, $glyphColor, $label, $detail, $detailColor, [bool]$inPlace) {
        $text = "  $glyphColor$glyph$reset  $($c.text)$($label.PadRight(20))$reset$detailColor$detail$reset"

        # A row is rewritten over the one before it, and the progress row is the longest
        # thing on screen. Padding by a fixed few spaces left the end of the bar and the
        # byte counts sitting after a shorter "done" row; it is padded to the window width
        # instead, measured without the colour codes, which take no room.
        $columns = 100
        try { $columns = $Host.UI.RawUI.WindowSize.Width } catch { Write-Verbose $_.Exception.Message }
        $visible = ($text -replace "$esc\[[0-9;]*m", '').Length
        $pad = ' ' * [Math]::Max(0, $columns - 1 - $visible)

        if ($interactive -and $inPlace) {
            [Console]::Write("`r" + $text + $pad)
        } elseif ($interactive) {
            [Console]::Write("`r" + $text + $pad + "`n")
        } else {
            Write-Host $text
        }
    }

    function Enter-Step($label) {
        if ($interactive) { Write-Row -Glyph $g.dot -GlyphColor $c.faint -Label $label -Detail '' -DetailColor $c.faint -InPlace $true }
    }
    function Complete-Step($label, $detail) { Write-Row -Glyph $g.ok -GlyphColor $c.ok -Label $label -Detail $detail -DetailColor $c.faint -InPlace $false }
    function Skip-Step($label, $detail) { Write-Row -Glyph $g.skip -GlyphColor $c.faint -Label $label -Detail $detail -DetailColor $c.faint -InPlace $false }
    function Show-StepFailure($label, $detail) { Write-Row -Glyph $g.bad -GlyphColor $c.bad -Label $label -Detail $detail -DetailColor $c.bad -InPlace $false }

    function Format-Size($bytes) {
        if ($bytes -ge 1MB) { return ('{0:N1} MB' -f ($bytes / 1MB)) }
        return ('{0:N0} KB' -f ($bytes / 1KB))
    }

    # ---------------------------------------------------------------- environment

    # Whether a user PATH entry names this directory, by what it expands to.
    function Format-PathEntry($entry) {
        [Environment]::ExpandEnvironmentVariables($entry.Trim().Trim('"')).TrimEnd('\', '/')
    }

    # The value as it is stored, not as it expands. The user PATH holds references such as
    # %USERPROFILE%, and reading it expanded and writing it back would bake every one in.
    function Get-StoredUserPath {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')
        try {
            if ($null -eq $key) { return '' }
            $value = $key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if ($null -eq $value) { return '' }
            return [string]$value
        } finally { if ($key) { $key.Dispose() } }
    }

    function Write-StoredUserPath($value) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment')
        try { $key.SetValue('Path', $value, [Microsoft.Win32.RegistryValueKind]::ExpandString) }
        finally { $key.Dispose() }

        # Written to the registry directly, so the notification the Environment class would
        # have sent has to be sent here, or Explorer keeps handing out the old PATH until
        # the next sign-in.
        if (-not ('HelideInstall.Native' -as [type])) {
            Add-Type -Namespace HelideInstall -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
public static extern System.IntPtr SendMessageTimeout(System.IntPtr hWnd, uint Msg, System.UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out System.UIntPtr lpdwResult);
'@
        }
        try {
            $result = [UIntPtr]::Zero
            [HelideInstall.Native]::SendMessageTimeout([IntPtr]0xFFFF, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 2000, [ref]$result) | Out-Null
        } catch { Write-Verbose $_.Exception.Message }
    }

    # Returns 'added', 'present' or 'refused'.
    function Add-ToUserPath($directory) {
        $stored = Get-StoredUserPath
        $entries = @($stored.Split(';') | Where-Object { $_.Trim().Length -gt 0 })
        $want = Format-PathEntry $directory

        foreach ($entry in $entries) {
            if ((Format-PathEntry $entry) -ieq $want) { return 'present' }
        }

        $combined = (@($entries) + $directory) -join ';'

        # A truncated PATH breaks every shell on the machine, so refuse rather than write
        # something partial.
        if ($combined.Length -gt 2000) { return 'refused' }

        Write-StoredUserPath $combined
        return 'added'
    }

    function Clear-UserPathEntry($directory) {
        $stored = Get-StoredUserPath
        $want = Format-PathEntry $directory
        $kept = @($stored.Split(';') | Where-Object {
            $_.Trim().Length -gt 0 -and (Format-PathEntry $_) -ine $want })

        if ($kept.Count -eq @($stored.Split(';') | Where-Object { $_.Trim().Length -gt 0 }).Count) { return $false }

        Write-StoredUserPath ($kept -join ';')
        return $true
    }

    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Helide.lnk'

    # ---------------------------------------------------------------- uninstall

    if ($Uninstall) {
        Write-Header 'uninstall'
        try {
            $removed = 0
            foreach ($name in 'Helide.exe', 'AutoUpdater.exe') {
                foreach ($file in @(Get-ChildItem -LiteralPath $dir -Filter "$name*" -ErrorAction SilentlyContinue)) {
                    Remove-Item -LiteralPath $file.FullName -Force
                    $removed++
                }
            }
            if ($removed -gt 0) { Complete-Step 'Remove files' $dir } else { Skip-Step 'Remove files' 'nothing installed here' }

            if ((Test-Path -LiteralPath $dir) -and -not (Get-ChildItem -LiteralPath $dir -Force)) {
                Remove-Item -LiteralPath $dir -Force
            }

            if ($portable) {
                Skip-Step 'PATH' 'custom folder, not touched'
                Skip-Step 'Start Menu' 'custom folder, not touched'
            } else {
                if (Clear-UserPathEntry $dir) { Complete-Step 'PATH' 'entry removed' } else { Skip-Step 'PATH' 'no entry' }
                if (Test-Path -LiteralPath $shortcut) {
                    Remove-Item -LiteralPath $shortcut -Force
                    Complete-Step 'Start Menu' 'shortcut removed'
                } else { Skip-Step 'Start Menu' 'no shortcut' }
            }

            Write-Rule
            Write-Host "  $($c.text)Helide removed.$reset $($c.faint)Settings under %LOCALAPPDATA%\Helide were kept.$reset"
            Write-Host ''
            return 0
        } catch {
            Show-StepFailure 'Uninstall' $_.Exception.Message
            Write-Host "  $($c.faint)Close every Helide window and run this again.$reset"
            Write-Host ''
            return 1
        }
    }

    # ---------------------------------------------------------------- install

    Write-Header 'installer'
    $temp = Join-Path ([IO.Path]::GetTempPath()) ('helide-install-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $step = ''

    try {
        # A copy winget owns is updated by winget; putting a second one beside it would
        # leave two Helides and two answers to "which one is on PATH".
        $step = 'Check install'
        Enter-Step $step
        $wingetRoot = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
        if ((-not $portable) -and (Test-Path -LiteralPath $wingetRoot) -and
            (Get-ChildItem -LiteralPath $wingetRoot -Directory -Filter 'ujjwalvivek.Helide*' -ErrorAction SilentlyContinue)) {
            Show-StepFailure $step 'installed with winget'
            Write-Host "  $($c.faint)Update it with: winget upgrade ujjwalvivek.Helide$reset"
            Write-Host ''
            return 1
        }
        Complete-Step $step $dir

        # ---- which release
        $step = 'Find release'
        Enter-Step $step
        $handler = New-Object System.Net.Http.HttpClientHandler
        $handler.AllowAutoRedirect = $false
        $probe = New-Object System.Net.Http.HttpClient($handler)
        $probe.DefaultRequestHeaders.UserAgent.ParseAdd('Helide-Installer/1.0')
        try {
            $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Head, "https://github.com/$repo/releases/latest")
            $response = $probe.SendAsync($request).GetAwaiter().GetResult()
            $location = if ($response.Headers.Location) { $response.Headers.Location.ToString() } else { '' }
        } finally { $probe.Dispose() }

        $marker = $location.LastIndexOf('/tag/')
        if ($marker -lt 0) { throw 'could not read the latest release from GitHub' }
        $tag = $location.Substring($marker + 5).Trim('/')
        $base = "https://github.com/$repo/releases/download/$tag"
        Complete-Step $step $tag

        # ---- is it already here
        # Compared by version, so running this again costs a request and not a 70 MB
        # download. A copy that is newer than the release (a local build) is left alone
        # too: an installer that quietly downgrades is worse than one that does nothing.
        # -Force is how to reinstall the same version, to repair a damaged file.
        $target = Join-Path $dir 'Helide.exe'
        $upToDate = $false
        $installedLabel = ''
        if ((-not $Force) -and (Test-Path -LiteralPath $target)) {
            try {
                $raw = (Get-Item -LiteralPath $target).VersionInfo.FileVersion
                if ($raw) {
                    $raw = ($raw -split '\+')[0]
                    $have = [Version]$raw
                    $want = [Version]($tag.TrimStart('v', 'V'))

                    # 1.7.5 and 1.7.5.0 are the same release; an unset part reads as -1.
                    $have = New-Object Version $have.Major, $have.Minor, ([Math]::Max($have.Build, 0)), ([Math]::Max($have.Revision, 0))
                    $want = New-Object Version $want.Major, $want.Minor, ([Math]::Max($want.Build, 0)), ([Math]::Max($want.Revision, 0))

                    if ($have -ge $want) {
                        $upToDate = $true
                        $installedLabel = 'v' + $have.ToString(3)
                    }
                }
            } catch { Write-Verbose $_.Exception.Message }
        }

        if ($upToDate) {
            Skip-Step 'Download' "$installedLabel is already installed"
            Skip-Step 'Verify checksum' 'not needed'
            Skip-Step 'Install' 'already up to date'
        } else {

        # ---- download
        New-Item -ItemType Directory -Force $temp | Out-Null
        $zip = Join-Path $temp $archive

        $step = 'Download'
        Enter-Step $step
        $client = New-Object System.Net.Http.HttpClient
        $client.DefaultRequestHeaders.UserAgent.ParseAdd('Helide-Installer/1.0')
        $client.Timeout = [TimeSpan]::FromMinutes(15)
        try {
            $response = $client.GetAsync("$base/$archive", [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            [void]$response.EnsureSuccessStatusCode()
            $total = $response.Content.Headers.ContentLength
            $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $output = [IO.File]::Create($zip)
            try {
                $buffer = New-Object byte[] 81920
                $done = 0L
                $clock = [Diagnostics.Stopwatch]::StartNew()
                while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $output.Write($buffer, 0, $read)
                    $done += $read
                    if ($interactive -and $total -and $clock.ElapsedMilliseconds -ge 100) {
                        $clock.Restart()
                        $cells = 24
                        $filled = [int][Math]::Floor($done / $total * $cells)
                        $bar = "$($c.accent)$($g.full * $filled)$($c.faint)$($g.track * ($cells - $filled))$reset"
                        Write-Row -Glyph $g.dot -GlyphColor $c.accent -Label $step -Detail "$bar  $(Format-Size $done) / $(Format-Size $total)" -DetailColor $c.muted -InPlace $true
                    }
                }
            } finally { $output.Dispose(); $source.Dispose() }
        } finally { $client.Dispose() }
        Complete-Step $step (Format-Size (Get-Item -LiteralPath $zip).Length)

        # ---- verify
        $step = 'Verify checksum'
        Enter-Step $step
        $expected = $null
        try {
            $web = New-Object System.Net.WebClient
            $web.Headers.Add('User-Agent', 'Helide-Installer/1.0')
            $sums = $web.DownloadString("$base/SHA256SUMS.txt")
            foreach ($line in $sums -split "`n") {
                $parts = $line.Trim() -split '\s+', 2
                if ($parts.Count -eq 2 -and $parts[1].TrimStart('*') -ieq $archive) { $expected = $parts[0]; break }
            }
        } catch { Write-Verbose $_.Exception.Message }

        if ($null -eq $expected) {
            # Releases from before checksums were published have nothing to compare to.
            Skip-Step $step 'none published for this release'
        } else {
            $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
            if ($actual -ine $expected) { throw 'the download does not match its published checksum' }
            Complete-Step $step 'sha256 matches'
        }

        # ---- install
        $step = 'Install'
        Enter-Step $step
        $extract = Join-Path $temp 'extract'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $extract)
        $exe = Get-ChildItem -LiteralPath $extract -Recurse -Filter 'Helide.exe' | Select-Object -First 1
        if (-not $exe) { throw 'the archive does not contain Helide.exe' }

        New-Item -ItemType Directory -Force $dir | Out-Null

        # A running exe cannot be overwritten, but it can be renamed: the open handle stays
        # on the old name and the new file lands cleanly. The same step the updater takes.
        if (Test-Path -LiteralPath $target) {
            Move-Item -LiteralPath $target -Destination ($target + '.old.' + [Guid]::NewGuid().ToString('N').Substring(0, 6)) -Force
        }
        Copy-Item -LiteralPath $exe.FullName -Destination $target -Force

        # Whatever earlier installs displaced and nothing holds open any more.
        Get-ChildItem -LiteralPath $dir -Filter 'Helide.exe.old.*' -ErrorAction SilentlyContinue |
            ForEach-Object { try { Remove-Item -LiteralPath $_.FullName -Force } catch { Write-Verbose $_.Exception.Message } }
        Complete-Step $step $dir

        }

        # ---- PATH and Start Menu
        $restartShell = $false
        if ($portable) {
            Skip-Step 'PATH' 'custom folder, not touched'
            Skip-Step 'Start Menu' 'custom folder, not touched'
        } else {
            $step = 'PATH'
            Enter-Step $step
            switch (Add-ToUserPath $dir) {
                'added'   { Complete-Step $step 'added to the user PATH'; $restartShell = $true }
                'present' { Complete-Step $step 'already on the user PATH' }
                default   { Show-StepFailure $step 'user PATH is too long to extend safely' }
            }

            $step = 'Start Menu'
            Enter-Step $step
            try {
                $shell = New-Object -ComObject WScript.Shell
                $link = $shell.CreateShortcut($shortcut)
                $link.TargetPath = $target
                $link.WorkingDirectory = $dir
                $link.Description = 'Helide terminal workspace'
                $link.Save()
                Complete-Step $step 'Helide.lnk'
            } catch {
                Show-StepFailure $step $_.Exception.Message
            }
        }

        Write-Rule
        if ($upToDate) {
            Write-Host "  $($c.text)Helide $installedLabel is already up to date.$reset"
        } else {
            Write-Host "  $($c.text)Helide $tag installed.$reset"
        }
        if ($portable) {
            Write-Host "  $($c.faint)Run: $target$reset"
        } elseif ($restartShell) {
            Write-Host "  $($c.faint)Open a new shell, then run: helide$reset"
        } else {
            Write-Host "  $($c.faint)Run: helide$reset"
        }
        Write-Host "  $($c.faint)First launch checks yazi, lazygit and helix and offers to install what is missing.$reset"
        Write-Host ''
        return 0
    } catch {
        Show-StepFailure $(if ($step) { $step } else { 'Install' }) $_.Exception.Message
        Write-Host "  $($c.faint)Nothing was changed past the step above. Close Helide and run this again.$reset"
        Write-Host ''
        return 1
    } finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
    }
} -Uninstall:$Uninstall -Force:$Force -InstallDir $InstallDir

# `exit` would close the terminal when piped into Invoke-Expression, so it is only used for a file run.
if ($PSCommandPath) { exit $exitCode }
