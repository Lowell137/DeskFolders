# DeskFolders

App folders for the Windows 11 desktop, like the folders on your phone or in the Start menu.

A folder sits on the desktop as a small tile with a 2x2 preview of its apps. Click it once and a Fluent
panel opens next to it with the apps inside. Click an app to launch it.

## Features

- **Desktop tiles** with acrylic background. They snap to the desktop icon grid, never overlap icons or each
  other, and can be dragged to another cell (right-click > Lock positions to stop that).
- **Fluent panel**: real Windows acrylic, follows the system light/dark theme, Windows 11 flyout animation.
  It closes when you click elsewhere, press Esc or switch to another window.
- **Pages**: 16 apps (4x4) per page. More apps open a page to the right. Change pages with the mouse
  wheel, by dragging the empty area left/right, or with the dots at the bottom.
- **Add apps**: drag a shortcut or an .exe onto a tile or onto the open panel. Shortcuts from the
  desktop are moved into the folder.
- **Reorder**: drag an app inside the panel. Hold it at the left/right edge to move it to another page.
- **Take apps out**: drag an app out of the panel and drop it on the desktop (it lands where you drop it)
  or on another tile (it moves to that folder). Right-click an app > Remove from folder also works.
- **New folder**: right-click the desktop > New > App folder. The new folder opens with its name ready to edit.
- **Rename**: click the folder name at the top of the panel.
- **Delete folder**: right-click the tile, or right-click empty space in the panel. The apps are kept in a
  regular folder with the same name on the desktop. A folder whose last app is removed is deleted.
- **Sort by name**: right-click empty space in the panel.

## Install

Download `DeskFolders-Setup-<version>.exe` from [Releases](https://github.com/Lowell137/DeskFolders/releases)
and run it. It installs per user (no admin rights). The optional "Start with Windows" task is on by default.

Requires Windows 11 (build 22000 or newer), x64.

## Where things are stored

| What | Where |
| --- | --- |
| Folders (each one is a normal directory of shortcuts) | `Documents\DeskFolders\<Folder>` |
| Tile positions, lock state | `%LOCALAPPDATA%\GrupPopup\tiles.json` |
| App order inside folders | `%LOCALAPPDATA%\GrupPopup\order.json` |
| Errors | `%LOCALAPPDATA%\GrupPopup\crash.log` |

Folders whose name starts with `_` are ignored.

## Build

Needs the .NET 8 SDK, the Windows App SDK workload, and [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Output: `dist\` (the app) and `out\DeskFolders-Setup-<version>.exe` (the installer).

## How it works (notes)

- Tiles are top-level layered windows placed in the z-order just above the desktop (`Progman`). On Windows 11
  24H2/25H2 child windows of `Progman` are not drawn, so tiles cannot live inside the desktop window.
  When the desktop is in front (Win+D) tiles move to the topmost band.
- The panel is a WinUI 3 window with `DesktopAcrylicBackdrop`. The process watches the foreground window to
  close the panel when focus leaves.
- Dragging apps out of the panel is done by hand (a small ghost icon follows the cursor). OLE drag and drop
  does not see mouse input inside a WinUI 3 process (`EnableMouseInPointer`).
- If Windhawk's "translucent-windows" mod is active it adds its own blur to every window; the panel turns that
  off for itself.

The code comments are in Turkish.
