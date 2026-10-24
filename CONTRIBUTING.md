# Contributing

Thanks for looking! Radii is a small Windows-only WPF project.

## Build

Needs the .NET 10 SDK and Windows 10/11 x64.

```powershell
dotnet build -c Release
bin\x64\Release\net10.0-windows10.0.19041.0\Radii.exe
```

Kill the running exe before rebuilding, it locks the output (`taskkill /IM Radii.exe /F`).

## Checking a change

There are no unit tests yet; UI is checked with offscreen renders that need no hook:

```powershell
Radii.exe --snapshot out.png                 # all ring variants
Radii.exe --snapshot-flyout out.png <path>   # extensions flyout
Radii.exe --snapshot-settings out.png        # settings window
Radii.exe --snapshot-convert out.png <path>  # converter ring
Radii.exe --snapshot-ring-editor out.png [file|folder|picture|...] [sector]   # ring editor
Radii.exe --snapshot-editor out.png crop|color|video|rename|... <path>...     # a mini editor window
Radii.exe --setup-snapshot out.png mode|options|progress|done [en|ru]   # installer pages
Radii.exe --convert out.txt <formatId> <path>...   # headless conversion
```

Please attach a screenshot for UI changes and say what you tried live in Explorer.

## Release build

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-release.ps1
```

produces `dist\Radii-Setup-<version>.exe`. The same file is the program **and** its installer: under the name
`Radii-Setup*.exe` (or with `--setup`) it shows the wizard and copies itself to the install folder as `Radii.exe`.
Installer code is in `Setup\`.

- silent install: `Radii-Setup.exe --silent [--dir <path>] [--no-engines] [--no-launch]`
- silent uninstall: `Radii.exe --uninstall --silent [--purge]` (`--purge` also deletes settings and downloaded engines)

To cut a release: bump `Version` in `Radii.csproj`, update `CHANGELOG.md`, push a tag `vX.Y.Z`.
The **Release** workflow builds the installer and attaches it to a GitHub Release.

## Testing the updater

The release check URL can be replaced for a test: set `RADII_UPDATE_URL` to a local JSON in the shape of the GitHub "latest release" response
(`tag_name`, `html_url`, `body`, `assets[]` with `name`, `size`, `browser_download_url`; assets `Radii-Setup-x.y.z.exe` and `SHA256SUMS.txt`),
serve it with `python -m http.server`, install an older build with `--silent`, and start the installed exe. `--update-snapshot out.png` renders the dialog.

## Style

Match the surrounding code (naming, comment density). Code, commits and docs in English.
