# Changelog

## 0.4.3

- **New logo.** The mark is now the ring itself: six graphite sectors with the one you are about to pick lit white. New app and tray icon, README banner and social preview to match the graphite skin.
- Installer: the mode descriptions wrap instead of being cut off (the same fix applies to every radio button in the app).
- README rewritten in English and Russian, with demo GIFs of the ring, the converter and the editors, and fresh screenshots.

## 0.4.2

- **New look.** Radii no longer follows the Windows accent colour: it has its own graphite palette (mono by default) and its own modern controls. Six colour schemes in Settings (graphite, amber, sage, terracotta, lime, slate), light and dark.
- Editor windows use native Windows 11 acrylic instead of a screenshot of the screen behind them (the blur no longer shifts or breaks).
- **Wider formats** in the editors: photos (WebP, HEIC, AVIF, JXL, TGA, PSD and more, opened through ImageMagick when Windows has no codec), audio and video lists extended.
- Sound editor: the wave of the track, and volume, speed, loudness and silence changes are heard while the preview plays.
- Compress photo: clear size presets and a separate field for your own size. Crop: clean rotate and flip icons. Time fields of the trim windows are no longer cut off.
- Mode pills under the ring: highlighting works after switching the ring, pairs are exactly centred.
- Removed "Split PDF" (merge, pages and compress stay).

## 0.4.1

- Fixed: right click on files and folders on the Explorer Home page now selects the item and opens the ring for it (before, the ring was the empty-space one).
- Removed the press-drag-release gesture and its setting: the ring opens on release and waits for a click, so it can no longer pick a sector by itself when it appears under the cursor near a screen edge.

## 0.4.0

- **Mini editors** (Ctrl + right click, or the "Edit" pill beside the ring): small glass windows for quick edits that save a new file next to the
  original. Pictures: crop, resize, colour and light (iPhone-style sliders), compress to a size. Video: trim, crop. Audio: trim, join, volume
  and speed with a listen button. PDF: merge, split, pages (extract, rotate, delete), compress. Metadata cleaner, bulk rename with undo.
  Tools that need several files only appear when several files are selected.
- **Ring navigation.** Pills under every ring switch between the normal, convert and edit rings (and "Back" inside a second-level ring);
  `Left`/`Right` and `A`/`D` do the same from the keyboard. Switching turns the old ring out and the new one in.
- Smoother animations: editor windows, flyouts and notifications slide and fade.

## 0.3.0

- **Installer.** One file `Radii-Setup-x.y.z.exe` (self-contained, no .NET needed). *Automatic* mode installs everything and
  just waits; *Manual* mode picks the folder, shortcuts and which conversion engines to download now. Per-user install, no admin
  rights. Listed in "Apps & features"; silent mode `--silent`.
- **Converter ring** (Shift + right click): images, video, audio, documents, Office, PDF, data formats, archives, text encodings.
  Engines (FFmpeg, ImageMagick, Pandoc) are downloaded once, LibreOffice only when Office files are converted on a PC without Microsoft Office; notifications with progress and cancel.
- **Updates.** Checks GitHub Releases at start and once a day (setting "Check for updates automatically", button "Check for updates"),
  asks before updating, downloads the installer with progress in the corner notification, verifies SHA-256 and installs silently over the old version.
- Excel to PDF no longer depends on the default printer; the Office instance the user already has open keeps its window.
- App icon.

## 0.2.0

- Extensions flyout (real shell menu), settings window, tray icon, gesture mode, hover-to-open, usage sorting, custom commands, animations, crash log.

## 0.1.0

- Radial context menu for Explorer and the desktop.
