[CmdletBinding()]
param( [switch]$Check, [switch]$Full, [switch]$Build )

$ErrorActionPreference = 'Stop'

$ToolPath  = Join-Path $PSScriptRoot 'tools\CheckNInstall\CheckNInstall.csproj'
$Artifact  = Join-Path $PSScriptRoot 'tools\CheckNInstall\bin\Release\net8.0-windows\CheckNInstall-win-x64.exe'

if ($Build -or -not (Test-Path -LiteralPath $Artifact)) {
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
