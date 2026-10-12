[CmdletBinding()]
param( [switch]$Check, [switch]$Full, [switch]$Build )

$ErrorActionPreference = 'Stop'

$ToolPath  = Join-Path $PSScriptRoot 'tools\CheckNInstall\CheckNInstall.csproj'
$Artifact  = Join-Path $PSScriptRoot 'tools\CheckNInstall\bin\Release\net8.0-windows\CheckNInstall-win-x64.exe'

# The tool is run from a published exe, so an edit to its source does nothing until it is
# published again -- and forgetting -Build meant running the old one and seeing a bug
# that had already been fixed. So it is republished whenever a source file is newer.
$Stale = $false
if (Test-Path -LiteralPath $Artifact) {
    $built  = (Get-Item -LiteralPath $Artifact).LastWriteTimeUtc
    $newest = Get-ChildItem -LiteralPath (Split-Path $ToolPath) -File |
        Where-Object { $_.Extension -in '.cs', '.csproj' } |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    $Stale = $newest -and $newest.LastWriteTimeUtc -gt $built
}

if ($Build -or $Stale -or -not (Test-Path -LiteralPath $Artifact)) {
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
    Write-Host 'run with -Build to publish it first.' -ForegroundColor DarkGray
    exit 1
}

$arguments = @()
if ($Check) { $arguments += '--check' }
if ($Full)  { $arguments += '--full' }

& $Artifact @arguments
exit $LASTEXITCODE
