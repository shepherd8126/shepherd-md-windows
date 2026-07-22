# Shepherd Markdown | MD Reader - Windows

A fast, offline reader for Markdown (`.md`) files. This is the native Windows build
(C# WinForms host + WebView2). The macOS build lives at
[shepherd-md-mac](https://github.com/shepherd8126/shepherd-md-mac); downloads are on
[shepherd-md-releases](https://github.com/shepherd8126/shepherd-md-releases/releases/latest).

## What it is

- A tiny native `.exe` that hosts the UI in WebView2 and serves it from an in-process,
  zero-dependency HTTP server bound to `127.0.0.1`. No Node, no external runtime beyond
  the WebView2 runtime that ships with Windows.
- Full Markdown: tables, task lists, syntax-highlighted code, Mermaid diagrams, callouts.
- Tabs and tab groups, a starred library, history, saved sessions, search across folders.
- Open a single file or add whole folders; 5 themes; save as MD/HTML/PDF; screenshots.

## Build

Requires only the in-box .NET Framework compiler (no .NET SDK) and the WebView2 SDK DLLs
(already in the repo root).

```powershell
# compile the exe
./bin/build-exe.ps1

# build the installer (needs Inno Setup 6: winget install JRSoftware.InnoSetup)
./bin/build-installer.ps1
```

## Layout

- `native/` - `Program.cs` (WebView2 host, custom frame, update check) and `Server.cs`
  (the in-process loopback HTTP server + read-only file API).
- `public/` - the shared web UI (`index.html`, `app.js`, `app.css`, `update-banner.js`).
- `lib/` - bundled render libraries (marked, highlight.js, mermaid, DOMPurify, html2canvas).
- `installer/` - the Inno Setup script.
- `bin/` - build and file-association scripts.

## License

MIT - see [LICENSE](LICENSE).
