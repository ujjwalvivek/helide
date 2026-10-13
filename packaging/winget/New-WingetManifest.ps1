# Writes the three winget manifest files for one Helide release, laid out the way the
# microsoft/winget-pkgs repository expects them:
#
#   manifests\u\ujjwalvivek\Helide\<version>\
#
# The installer hash is read from the SHA256SUMS.txt the release workflow publishes, so it
# is never typed or pasted by hand. Run it after a release exists:
#
#   .\packaging\winget\New-WingetManifest.ps1 -Version 1.7.5
#   winget validate --manifest .\packaging\winget\manifests\u\ujjwalvivek\Helide\1.7.5
#
# Then copy that folder into a fork of winget-pkgs and open a pull request.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'A maintainer tool run at a console: the report is for the person running it.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Version    = $Version.TrimStart('v', 'V')
$Tag        = "v$Version"
$Identifier = 'ujjwalvivek.Helide'
$Repo       = 'ujjwalvivek/helide'
$Archive    = 'helide-win-x64.zip'
$Base       = "https://github.com/$Repo/releases/download/$Tag"

# ---- the published checksum -------------------------------------------------------------
$hash = $null
$hashSource = 'SHA256SUMS.txt from the release'

try {
    $sums = (Invoke-WebRequest "$Base/SHA256SUMS.txt" -UseBasicParsing).Content
    if ($sums -is [byte[]]) { $sums = [Text.Encoding]::ASCII.GetString($sums) }

    $line = $sums -split "`n" | Where-Object { $_ -match [regex]::Escape($Archive) } | Select-Object -First 1
    if (-not $line) { throw "SHA256SUMS.txt for $Tag does not list $Archive." }

    $hash = ($line.Trim() -split '\s+')[0].ToUpper()
} catch {
    # A 404 is a WebException in Windows PowerShell and an HttpResponseException in 7, and
    # a checksum file that does not list the archive is as unusable as one that is absent.
    $hash = $null
}

if (-not $hash) {
    # Releases built before checksums were published have none. The archive is hashed
    # here instead: fine for trying a manifest locally, but the value then vouches for
    # nothing the release itself ever said, so it is flagged rather than passed off.
    Write-Warning "$Tag has no SHA256SUMS.txt. Hashing the download instead; use this for local testing only."
    $temp = Join-Path ([IO.Path]::GetTempPath()) "helide-manifest-$Version.zip"
    try {
        Invoke-WebRequest "$Base/$Archive" -OutFile $temp -UseBasicParsing
        $hash = (Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash.ToUpper()
        $hashSource = 'computed from the downloaded archive (no published checksum)'
    } finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

if ($hash -notmatch '^[0-9A-F]{64}$') { throw "Not a SHA256 value: $hash" }

# ---- the release date -------------------------------------------------------------------
$date = Get-Date -Format 'yyyy-MM-dd'
try {
    $release = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/tags/$Tag" `
        -Headers @{ 'User-Agent' = 'helide-manifest' }
    $date = ([datetime]$release.published_at).ToString('yyyy-MM-dd')
} catch {
    Write-Warning "Could not read the release date from GitHub; using today ($date)."
}

# ---- the files --------------------------------------------------------------------------
$target = Join-Path $PSScriptRoot "manifests\u\ujjwalvivek\Helide\$Version"
New-Item -ItemType Directory -Force $target | Out-Null

$schema = '1.6.0'

$versionManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json

PackageIdentifier: $Identifier
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@

# A zip holding one portable exe. winget extracts it, keeps it under its own package
# folder, and puts the `helide` alias on PATH -- so there is no installer to write, and
# uninstall and upgrade come with it.
$installerManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json

PackageIdentifier: $Identifier
PackageVersion: $Version
InstallerLocale: en-US
MinimumOSVersion: 10.0.17763.0
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: Helide.exe
  PortableCommandAlias: helide
Commands:
- helide
ReleaseDate: $date
Installers:
- Architecture: x64
  InstallerUrl: $Base/$Archive
  InstallerSha256: $hash
ManifestType: installer
ManifestVersion: $schema
"@

$localeManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json

PackageIdentifier: $Identifier
PackageVersion: $Version
PackageLocale: en-US
Publisher: Ujjwal Vivek
PublisherUrl: https://github.com/ujjwalvivek
PublisherSupportUrl: https://github.com/$Repo/issues
Author: Ujjwal Vivek
PackageName: Helide
PackageUrl: https://github.com/$Repo
License: MIT
LicenseUrl: https://github.com/$Repo/blob/main/LICENSE
Copyright: Copyright (c) 2025 Ujjwal Vivek
ShortDescription: A terminal workspace for Windows that puts the project tools you need in one window.
Description: Helide hosts Helix, lazygit, yazi and a command runner in one native window, each in its own pseudoconsole, with optional agent panes for opencode, codex and claude. Missing tools are found and installed from the app itself.
Moniker: helide
Tags:
- terminal
- editor
- git
- helix
- lazygit
- yazi
- developer-tools
ReleaseNotesUrl: https://github.com/$Repo/releases/tag/$Tag
ManifestType: defaultLocale
ManifestVersion: $schema
"@

# winget-pkgs requires LF line endings and no byte order mark.
function Write-Manifest([string]$name, [string]$text) {
    $path = Join-Path $target $name
    [IO.File]::WriteAllText($path, ($text -replace "`r`n", "`n") + "`n", (New-Object Text.UTF8Encoding $false))
    Write-Host "  wrote $path"
}

Write-Manifest "$Identifier.yaml"                 $versionManifest
Write-Manifest "$Identifier.installer.yaml"       $installerManifest
Write-Manifest "$Identifier.locale.en-US.yaml"    $localeManifest

Write-Host ''
Write-Host "  version  $Version"
Write-Host "  sha256   $hash"
Write-Host "  source   $hashSource"
Write-Host "  date     $date"
