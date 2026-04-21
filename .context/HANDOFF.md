## Goal
QuickFind - Lightweight fast file search tool for Windows. System tray app with Alt+Space global hotkey, MFT-based indexing for blazing speed.

## Completed
- [x] Project setup (.NET 8 WPF + NuGet packages)
- [x] NativeMethods.cs - P/Invoke for MFT, file icons, volume info, shell properties, recycle bin
- [x] DriveDetector.cs - Auto-detect fixed NTFS drives
- [x] FileIndex.cs - In-memory index with MFT + fallback support, binary persistence, file sizes
- [x] MftIndexer.cs - FSCTL_ENUM_USN_DATA MFT reader (~1M files/sec)
- [x] FallbackIndexer.cs - Directory.EnumerateFiles for non-admin mode (stores file sizes)
- [x] SearchEngine.cs - Debounced search with scoring + size filter parsing (>100MB, <1GB)
- [x] IndexPersistence.cs - Save/load index to disk (binary format v2 with sizes)
- [x] Styles.xaml - Light gray theme (clean modern look)
- [x] SearchWindow.xaml - Borderless floating search popup with exit/hide buttons
- [x] SearchWindow.xaml.cs - Keyboard nav, file opening, icon extraction, right-click context menu
- [x] App.xaml.cs - Tray icon, Alt+Space hotkey, single instance, Task Scheduler admin, indexing
- [x] Admin without UAC via Task Scheduler approach
- [x] Right-click context menu (open, terminal, VS Code, copy paths, properties, delete)
- [x] Size-based search (>100MB, >1GB, <500KB, combinable with name)
- [x] Light gray theme (replaced dark navy theme)
- [x] Build + publish succeeds (147MB self-contained EXE)

## Key Decisions
- **No H.NotifyIcon.Wpf**: Used WindowsForms NotifyIcon instead (more reliable with .NET 8)
- **asInvoker manifest**: App detects elevation at runtime, uses MFT if admin, fallback if not
- **Task Scheduler for admin**: No UAC prompt on startup, scheduled task with HighestAvailable RunLevel
- **FRN masking**: Lower 48 bits for NTFS file reference numbers
- **Lazy path resolution**: Paths resolved on-demand from FRN parent chain, saves memory
- **Index v2**: Added file size storage; FallbackIndexer stores sizes, MFT entries resolve on demand
- **Size search**: Parallel file size resolution for MFT entries during size queries

## Known Issues
- MFT indexer doesn't store file sizes (USN records don't include them), resolved dynamically during size search
- Old v1 index files will be ignored (version mismatch), requires re-index after update

## Next Steps
1. Test size search functionality
2. Consider adding folder size calculation
3. Consider USN Journal for incremental index updates

## Important Files
- `QuickFind\QuickFind.csproj` - Project config
- `QuickFind\App.xaml.cs` - Main app logic (tray, hotkey, indexing, Task Scheduler)
- `QuickFind\SearchWindow.xaml.cs` - Search UI logic + context menu
- `QuickFind\Core\MftIndexer.cs` - MFT reader (the fast path)
- `QuickFind\Core\FileIndex.cs` - In-memory index + persistence (v2 with sizes)
- `QuickFind\Core\SearchEngine.cs` - Search with name scoring + size filters
- `QuickFind\Resources\Styles.xaml` - Light gray theme
- `publish\QuickFind.exe` - Published self-contained EXE
