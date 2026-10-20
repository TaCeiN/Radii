# Security policy

Radii installs low-level mouse and keyboard hooks, so security reports are taken seriously.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub private reporting:
**Security → Report a vulnerability** on the repository page.

Include the version, what you found and how to reproduce it. You can expect a first answer within a week.

## Good to know

- The hooks only react to real (non-injected) mouse events over Explorer and desktop item views; no input is recorded or sent anywhere.
- The only background request is the update check: one HTTPS GET to `api.github.com/repos/<owner>/Radii/releases/latest` (at start and once a day, switchable in settings). It sends a User-Agent with the version and nothing else.
- Downloads happen only on request: the conversion engines you choose (FFmpeg, ImageMagick, Pandoc from their GitHub releases; LibreOffice from Document Foundation mirrors, only on first conversion of an Office file without Microsoft Office installed) and an update you accepted. The update installer is verified against `SHA256SUMS.txt` of the release before it runs.
- Release files come with `SHA256SUMS.txt`; compare it with `Get-FileHash` before running.
