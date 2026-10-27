# StorageIt

A disk space analyzer for Windows 10 and 11. Scan drives or folders and see at a glance what is taking up the space.

- **Space map**: a colorful treemap where every rectangle's area is proportional to its size. Folders are framed groups, files are colored by type. Hover for details, double-click a folder to zoom in, Backspace to zoom out.
- **Folders tab**: the directory tree as a list (size, share of parent, file count, date). It stays in sync with the map.
- **File types tab**: everything grouped by extension, with the largest files of the selected type. The map dims everything else.
- **Duplicates tab**: finds files with identical content (size → first 64 KB → SHA-256), biggest waste first. Check the copies to remove ("all but oldest / newest"), then delete them to the Recycle Bin.
- **Actions** from the map or any list (right-click, or keyboard): Open (`Enter`), Show in Explorer, Properties (`Alt+Enter`), Copy path (`Ctrl+C`), Delete to Recycle Bin (`Del`), Delete permanently (`Shift+Del`).
- **Cleanup commands**: define your own commands (toolbar → *Cleanup commands…*). They appear in every right-click menu.

## Run

```powershell
dotnet run --project StorageIt                 # opens the "what to scan" dialog
dotnet run --project StorageIt -- D:\ C:\Users # or scan straight away
```

You can also drop folders onto the window. `Ctrl+O` chooses what to scan, `F5` rescans.

Build a standalone `artifacts\StorageIt.exe` (no .NET install needed on the target PC):

```powershell
.\publish.ps1            # win-x64   (use -Arch arm64 for ARM PCs)
```

Requires the .NET 10 SDK to build. Tests: `dotnet test`.

## Cleanup commands

Each command has a name, a program, arguments, and options (files / folders, ask first, run hidden and show the output, scan again afterwards). Arguments can use:

| Placeholder | Meaning |
|---|---|
| `{path}` | full path of the item |
| `{name}` / `{nameNoExt}` / `{ext}` | file name / without extension / extension with dot |
| `{dir}` | folder containing the item |
| `{folder}` | the item itself if it is a folder, otherwise its folder |
| `{drive}` | drive root, e.g. `C:\` |
| `{paths}` | every selected item, each in quotes (for "run once for all") |

Placeholders are inserted as-is, so wrap them in quotes when paths may contain spaces: `/c rd /s /q "{path}"`.
Commands are stored in `%APPDATA%\StorageIt\commands.json`. Two examples are created on first run.

## Things worth knowing

- **Sizes** are the files' logical sizes, not size on disk.
- **Junctions and symlinks** are listed but never followed, so nothing is counted twice.
- **OneDrive "online only" files** take no space on the disk and are not counted (the status bar says how many).
- **Folders that cannot be read** (needing administrator rights) are skipped and counted in the status bar. Run StorageIt as administrator to include them.
- **Hard links** (common in `C:\Windows\WinSxS`) are recognised: several paths to one physical file are not reported as duplicates, since deleting one frees nothing.
- **Deleting** always asks first and shows the total size. Drive roots, the Windows folder, Program Files and your profile folder cannot be deleted from here; items inside Windows or program folders get an extra warning. Drives without a Recycle Bin (network, USB) delete permanently, and the dialog says so.
- The duplicate search skips `C:\Windows` and Program Files by default.
- The look follows Windows' light/dark setting. `STORAGEIT_THEME=light|dark` forces one.

## Layout

```
StorageIt/            WPF app (.NET 10, Fluent theme)
  Models/FsNode.cs    the scanned tree
  Services/           Scanner, Squarify (treemap layout), DuplicateFinder, ShellActions, CustomCommands, ExtensionColors
  Controls/           TreemapControl (custom-drawn)
  Views/              MainWindow, TreePane, ExtensionsPane, DuplicatesPane, dialogs, ActionHub (shared actions)
StorageIt.Tests/      xUnit tests (layout, scanner, duplicates, deletion, commands, menus)
publish.ps1           single-file release build
```
