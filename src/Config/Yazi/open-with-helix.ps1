# Opens a file the way Helide wants it.
#
# Called by yazi's [opener] in yazi.toml, for both the `edit` name (text types named
# in [open] prepend_rules) and the `open` name (yazi's fallback for everything else).
# Kept as a script rather than an inline command because nesting PowerShell quotes
# inside a TOML literal string breaks the parse.
#
# When Helide is running it exports HELIDE_OPEN_REQUEST, a file it watches. We append
# the path there so the file opens in Helide's own editor tab instead of spawning a
# stray hx window. Standalone (no Helide), we just call hx.
#
# This script is also the last stop before Windows, so the text/binary decision lives
# here rather than in a list of extensions: helix renders text only, and handing a
# binary to Start-Process is what produced the "how do you want to open this?" dialog.

param(
	[Parameter(Position = 0)]
	[string]$Path,

	[Parameter(ValueFromRemainingArguments = $true)]
	[string[]]$More
)

# Helix renders text; a NUL byte in the head of the file is the cheapest reliable
# signal that the contents are not. Only the head is read, so a huge file or a locked
# one still resolves quickly.
#
# Defined before the code that calls it: a script executes top to bottom, so a
# function placed below the loop is simply not found when the loop reaches it, and
# every file gets skipped as if it were binary.
function Test-IsTextFile {
	param([string]$File)

	try {
		$stream = [System.IO.File]::Open($File, 'Open', 'Read', 'ReadWrite')
		try {
			$length = [Math]::Min(8192, $stream.Length)
			if ($length -le 0) {
				return $true
			}

			$buffer = New-Object byte[] $length
			$read = $stream.Read($buffer, 0, $length)
			for ($i = 0; $i -lt $read; $i++) {
				if ($buffer[$i] -eq 0) {
					return $false
				}
			}

			return $true
		}
		finally {
			$stream.Dispose()
		}
	}
	catch {
		# Unreadable files are not the editor's problem.
		return $false
	}
}

# A directory reaching an opener means yazi was asked to open it rather than descend
# into it, which is what stopped folders from expanding: taking over the `open`
# opener means nothing is left for yazi to handle by itself. Hand the descent back to
# yazi instead. YAZI_ID is inherited from yazi, which is what `ya emit` uses to find
# the instance to talk to.
foreach ($item in @($Path) + @($More)) {
	if (-not $item) {
		continue
	}

	if (Test-Path -LiteralPath $item -PathType Container) {
		try {
			& ya emit cd $item 2>$null
		}
		catch {
			# No ya on PATH, or no instance to talk to. Nothing else to try.
		}

		continue
	}

	if (-not (Test-Path -LiteralPath $item -PathType Leaf)) {
		continue
	}

	if (-not (Test-IsTextFile $item)) {
		# Binary. Helix would show it as mojibake (a PNG reads back as "%PNG"), so let
		# Windows pick a handler instead. Anything with no registered handler raises the
		# system app picker, which is a dead end but at least it is an honest one.
		try {
			Start-Process -FilePath $item
		}
		catch {
			Write-Error "Could not open $item"
		}

		continue
	}

	$request = $env:HELIDE_OPEN_REQUEST
	if ($request) {
		try {
			# One line per request. Helide consumes this as a byte-offset log and only
			# retires the file once it is fully drained, so a line written while another
			# is being read cannot be lost.
			[System.IO.File]::AppendAllText($request, $item + "`n")
			continue
		}
		catch {
			# Fall through to launching the editor directly.
		}
	}

	try {
		# Standalone yazi: no Helide to hand off to, so open it ourselves.
		Start-Process -FilePath 'hx.exe' -ArgumentList @($item) -WorkingDirectory (Split-Path -Parent $item)
	}
	catch {
		Write-Error "Could not open $item"
	}
}

exit 0