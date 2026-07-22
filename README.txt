SHEPHERD MD
===========

A modern local reader for Markdown (.md) files - a home for viewing all your
Markdown outside Claude, with full Claude-viewer fidelity (GFM tables, task
lists, syntax-highlighted code, embedded HTML, GitHub callouts, Mermaid).

OPEN IT
  - Start Menu or Desktop -> "Shepherd MD" (clay sunburst icon).
  - Drag any .md file into the window, or (once set as default) double-click a .md.

TABS & SESSIONS
  - Open many files as tabs; drag to reorder, middle-click or x to close, Ctrl+T new.
  - Switching between open tabs is instant (each tab's render is cached).
  - Sidebar has four views (icons top-left): Files, Starred, Recent, Sessions.
  - Star files (toolbar star / Ctrl+D) to build a Library.
  - Save your open tabs as a named Session, reopen any time.
  - Everything persists across close/reopen automatically.

PDF FILES
  - Opens .pdf files as tabs in a built-in PDF viewer (zoom, search, print, download).
  - "Open folder" adds any folder to the sidebar (its .md and .pdf files) - it uses a
    normal Windows folder dialog, no browser permission prompt.

THEMES (palette button, top-right)
  Auto - Light - Sepia - Silver - True dark (OLED) - Claude (warm dark)

SAVE / EXPORT
  - Save button: Markdown (.md), standalone HTML, or PDF (via print dialog).
  - Screenshot button: save whole document as PNG, copy to clipboard, or visible area.

KEYBOARD
  Ctrl+F search      Ctrl+B sidebar      Ctrl+T new tab      Ctrl+W close tab
  Ctrl+Tab next tab  Ctrl+S save menu    Ctrl+D star         Ctrl+P print
  Ctrl+ +/- zoom     Ctrl+0 reset        Ctrl+Shift+O TOC    Alt+Left/Right back/fwd

MAKE IT THE DEFAULT .MD APP
  Shepherd MD is registered as a handler (bin/register-default.ps1). Windows 11
  protects the actual default with a locked key, so set it once by hand:
  Settings > Apps > Default apps > search ".md" > choose Shepherd MD.  (Or right-
  click any .md > Open with > Choose another app > Shepherd MD > Always.)
  Undo the registration any time with bin/unregister-default.ps1.

HOW IT WORKS
  The window is a native app, ShepherdMD.exe (WinForms + WebView2) - not a browser.
  It starts a tiny local Node engine that serves the viewer; the engine listens only
  on 127.0.0.1, reads files read-only, and makes NO internet requests.
  Single-instance: double-clicking a file adds a tab to the open window (never a 2nd one).
  Session state lives in session.json; browsing root(s) in config.json.
  Rebuild the exe: bin/build-exe.ps1 (uses the in-box .NET compiler; no SDK needed).

UNINSTALL
  Run bin/unregister-default.ps1, then delete this folder and the two shortcuts.
