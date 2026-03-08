<p align="center">
  <img src="QuickFind/Resources/icon.svg" width="80" alt="QuickFind Icon"/>
</p>

<h1 align="center">QuickFind</h1>

<p align="center">
  <strong>Blazing fast file search for Windows</strong><br/>
  Index millions of files in seconds. Search instantly as you type.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11-blue?style=flat-square" alt="Platform"/>
  <img src="https://img.shields.io/badge/.NET-8.0-purple?style=flat-square" alt=".NET 8"/>
  <img src="https://img.shields.io/badge/license-MIT-green?style=flat-square" alt="License"/>
</p>

---

## Why QuickFind?

Windows built-in search is slow and often fails to find files you *know* exist. QuickFind reads the NTFS Master File Table (MFT) directly, indexing **3+ million files in under 10 seconds** — then searches them instantly as you type.

### Key Features

- **MFT Indexing** — Reads the NTFS Master File Table directly for near-instant full-drive indexing
- **Instant Search** — Results appear as you type with smart scoring (exact > starts-with > contains)
- **Size Filters** — Find large files eating up disk space with one-click filters (>100MB, >500MB, >1GB, >5GB)
- **Drive Selector** — Focus search on a specific drive or search across all drives
- **Content Search** — Optional grep-like search inside text files (.txt, .cs, .py, .json, etc.)
- **Persistent Index** — Index saved to disk; loads instantly on restart without re-scanning
- **Global Hotkey** — `Alt+Space` toggles the search window from anywhere
- **System Tray** — Lives quietly in the tray, always one keystroke away
- **Context Menu** — Right-click results to open, copy path, open in terminal/VS Code, view properties, or delete
- **Smart Badges** — Color-coded type badges (APP, CODE, IMG, VID, ZIP, PDF, etc.)

## Getting Started

### Requirements

- Windows 10/11
- .NET 8.0 Runtime (or use the self-contained release)
- **Admin rights** recommended for MFT indexing speed (falls back to directory enumeration otherwise)

### Install

Download the latest release from [Releases](https://github.com/levy-n/QuickFind/releases), or build from source:

```bash
git clone https://github.com/levy-n/QuickFind.git
cd QuickFind
dotnet publish QuickFind/QuickFind.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

The self-contained `.exe` will be in the `publish/` folder — no .NET runtime needed.

### Usage

| Action | Shortcut |
|---|---|
| Toggle search window | `Alt + Space` |
| Open selected file | `Enter` |
| Open containing folder | `Ctrl + Enter` |
| Hide window | `Esc` |
| Navigate results | `↑` `↓` arrows |
| Context menu | Right-click |

**Size search:** Click the filter buttons or type directly: `>500MB`, `>2GB`, `<100KB`

## Architecture

```
QuickFind/
├── Core/
│   ├── MftIndexer.cs          # NTFS MFT reader via P/Invoke (FSCTL_ENUM_USN_DATA)
│   ├── FallbackIndexer.cs     # Directory enumeration fallback for non-admin
│   ├── FileIndex.cs           # In-memory index (name + parent ref, ~14MB per 1M files)
│   ├── SearchEngine.cs        # Scoring engine with size filter parsing
│   └── IndexPersistence.cs    # Binary serialization for instant reload
├── Helpers/
│   ├── NativeMethods.cs       # P/Invoke: MFT, shell icons, recycle bin
│   └── DriveDetector.cs       # Auto-detect fixed NTFS drives
├── Resources/
│   └── Styles.xaml            # Light theme + all control styles
├── App.xaml.cs                # Tray icon, hotkey, single instance
└── SearchWindow.xaml/.cs      # Main search UI
```

### How It Works

1. **Indexing** — On first launch (as admin), QuickFind reads the MFT of every NTFS drive using `DeviceIoControl` with `FSCTL_ENUM_USN_DATA`. This captures every file and directory name with its parent reference number — no full path resolution needed during indexing.

2. **Persistence** — The index is saved as a compact binary file. On subsequent launches, it loads from disk in milliseconds instead of re-scanning.

3. **Search** — Queries are scored: exact match (100) > name without extension (95) > starts-with (80) > contains (60). Results are sorted by score and capped at 100. Size filters use regex parsing to support queries like `>500MB` or `<2GB`.

4. **Path Resolution** — Full paths are resolved on-demand by walking the parent chain in the index. This keeps memory usage low while still providing complete file paths in results.

## Tech Stack

- **WPF** (.NET 8) — Modern Windows UI
- **NHotkey.Wpf** — Global hotkey registration
- **P/Invoke** — Direct Windows API calls for MFT reading and shell integration

## License

MIT

---

<p align="center">
  Built with speed in mind. If Windows search can't find it, QuickFind will.
</p>
