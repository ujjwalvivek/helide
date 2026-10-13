# winget packaging

Manifests for `ujjwalvivek.Helide`, so Helide can be installed with:

```powershell
winget install ujjwalvivek.Helide
```

The package is a zip holding one portable exe. winget unpacks it under its own package folder and adds a `helide` command to PATH, so uninstall and `winget upgrade` come with it. An install that lives there is updated by winget, and Helide's own updater steps aside.

## Publishing a release

1. Publish the GitHub release first. The manifest needs the `SHA256SUMS.txt` the release workflow attaches, and the hash is read from it rather than typed.
2. Generate `.\packaging\winget\New-WingetManifest.ps1 -Version 1.7.5` and validate `winget validate --manifest .\packaging\winget\manifests\u\ujjwalvivek\Helide\1.7.5` the manifests.
3. Copy `manifests\u\ujjwalvivek\Helide\<version>` into a fork of [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) at the same path, and open a pull request.

## Testing a manifest locally

Installing from a local manifest has to be enabled once, from an elevated shell:

```powershell
winget settings --enable LocalManifestFiles
winget install --manifest .\packaging\winget\manifests\u\ujjwalvivek\Helide\1.7.5
```
