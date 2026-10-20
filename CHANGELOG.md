# Changelog

## 0.3.0

- **Installer.** One file `Radii-Setup-x.y.z.exe` (self-contained, no .NET needed). *Automatic* mode installs everything and
  just waits; *Manual* mode picks the folder, shortcuts and which conversion engines to download now. Per-user install, no admin
  rights. Listed in "Apps & features"; silent mode `--silent`.
- **Converter ring** (Shift + right click or F9): images, video, audio, documents, Office, PDF, data formats, archives, text encodings.
  Engines (FFmpeg, ImageMagick, Pandoc) are downloaded once, LibreOffice only when Office files are converted on a PC without Microsoft Office; notifications with progress and cancel.
- **Updates.** Checks GitHub Releases at start and once a day (setting "Check for updates automatically", button "Check for updates"),
  asks before updating, downloads the installer with progress in the corner notification, verifies SHA-256 and installs silently over the old version.
- Excel to PDF no longer depends on the default printer; the Office instance the user already has open keeps its window.
- App icon.

## 0.2.0

- Extensions flyout (real shell menu), settings window, tray icon, gesture mode, hover-to-open, usage sorting, custom commands, animations, crash log.

## 0.1.0

- Radial context menu for Explorer and the desktop.
