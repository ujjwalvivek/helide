# Launcher for tools\CheckNInstall. With no switch it opens the interactive tool.
#   -Check   report only, exit 0 when ready                           (CheckNInstall --check)
#   -Full    republish the tool, then set up everything silently      (CheckNInstall --full)
[CmdletBinding()]
param( [switch]$Check, [switch]$Full )

$ErrorActionPreference = 'Stop'

$ToolPath  = Join-Path $PSScriptRoot 'tools\CheckNInstall\CheckNInstall.csproj'
$Artifact  = Join-Path $PSScriptRoot 'tools\CheckNInstall\bin\Release\net8.0-windows\CheckNInstall-win-x64.exe'

$Stale = $false
if (Test-Path -LiteralPath $Artifact) {
    $built  = (Get-Item -LiteralPath $Artifact).LastWriteTimeUtc
    # The mark is embedded from src\Assets, so it counts as a source file too.
    $sources = @(Get-ChildItem -LiteralPath (Split-Path $ToolPath) -File |
        Where-Object { $_.Extension -in '.cs', '.csproj' })
    $mark = Join-Path $PSScriptRoot 'src\Assets\helide.txt'
    if (Test-Path -LiteralPath $mark) { $sources += Get-Item -LiteralPath $mark }
    $newest = $sources | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    $Stale = $newest -and $newest.LastWriteTimeUtc -gt $built
}

if ($Full -or $Stale -or -not (Test-Path -LiteralPath $Artifact)) {
    Write-Host "publishing CheckNInstall..." -ForegroundColor DarkGray
    Push-Location $PSScriptRoot
    try {
        & dotnet publish $ToolPath -c Release -r win-x64 --self-contained
        if ($LASTEXITCODE -ne 0) {
            Write-Host "publish failed" -ForegroundColor Red
            exit 1
        }
    } finally { Pop-Location }
}

if (-not (Test-Path -LiteralPath $Artifact)) {
    Write-Host "no build at $Artifact" -ForegroundColor Red
    Write-Host 'the publish did not produce the exe; check the build output above.' -ForegroundColor DarkGray
    exit 1
}

$arguments = @()
if ($Check) { $arguments += '--check' }
if ($Full)  { $arguments += '--full' }

& $Artifact @arguments
exit $LASTEXITCODE
