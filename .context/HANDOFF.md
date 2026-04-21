## Goal
QuickFind - Lightweight fast file search tool for Windows. System tray app with Alt+Space global hotkey, MFT-based indexing for blazing speed.

**Current phase:** Production-readiness hardening (feature branch `feature/production-readiness`).

## Completed (v1.0 release — merged to master)
- [x] Project setup (.NET 8 WPF + NuGet packages)
- [x] NativeMethods.cs - P/Invoke for MFT, file icons, volume info, shell properties, recycle bin
- [x] DriveDetector.cs - Auto-detect fixed NTFS drives
- [x] FileIndex.cs - In-memory index with MFT + fallback support, binary persistence, file sizes
- [x] MftIndexer.cs - FSCTL_ENUM_USN_DATA MFT reader (~1M files/sec)
- [x] FallbackIndexer.cs - Directory.EnumerateFiles for non-admin mode (stores file sizes)
- [x] SearchEngine.cs - Debounced search with scoring + size filter parsing (>100MB, <1GB)
- [x] IndexPersistence.cs - Save/load index to disk (binary format)
- [x] Styles.xaml - Light gray theme (clean modern look)
- [x] SearchWindow.xaml/.cs - Borderless floating search popup with keyboard nav, icons, context menu
- [x] App.xaml.cs - Tray icon, Alt+Space hotkey, single instance, Task Scheduler admin, indexing
- [x] Right-click context menu (open, terminal, VS Code, copy paths, properties, delete)
- [x] Size-based search (>100MB, >1GB, <500KB, combinable with name)
- [x] Build + publish succeeds (~150MB self-contained EXE)
- [x] Professional README and MIT license

## Completed this session (feature/production-readiness) — Pass 3
- [x] **USN Journal — Incremental Index Updates** — new `Core/UsnWatcher.cs`. One background thread per NTFS drive polls `FSCTL_READ_USN_JOURNAL` every 5 s with `ReturnOnlyOnClose=1` and applies creates / renames / deletes to the in-memory index. Cursor (JournalID + NextUsn) is persisted per drive to `%LOCALAPPDATA%\QuickFind\usn.state`. Admin-only — skips silently on non-elevated launches.
- [x] **FileIndex mutations** — `UsnAddOrUpdate(driveRoot, frn, parentFrn, name, isDir)` and `UsnRemove(driveRoot, frn)`. Removes are tombstoned (zero-length name) so existing integer indices held by search snapshots remain valid; tombstones are filtered in `ScanForSearch`.
- [x] **NativeMethods** — added `FSCTL_QUERY_USN_JOURNAL` / `FSCTL_READ_USN_JOURNAL` constants, `USN_JOURNAL_DATA_V0` / `READ_USN_JOURNAL_DATA_V0` structs, two additional `DeviceIoControl` P/Invoke overloads, USN reason flags.
- [x] **App lifecycle** — USN watcher starts after initial index load, stops before any reindex, restarts after successful reindex, disposes on `ExitApp`.
- [x] **Build + publish verified** — 0 warnings, 0 errors.

## Completed this session (feature/production-readiness) — Pass 2
- [x] **Search hot-path allocation fix** — `FileIndex.ScanForSearch` streams entries without materialising a 100 MB snapshot of tuples per keystroke. `ScoreName` uses `OrdinalIgnoreCase` so no per-entry lowercase copy is allocated. Net effect: searches on large indexes no longer trigger a GC storm.
- [x] **Named-Pipe single-instance signaling** — new `Core/SingleInstance.cs`. A second launch of QuickFind connects to pipe `QuickFind_SingleInstance_Pipe`, sends `SHOW`, and the running instance pops its search window. Falls back to silent exit if the pipe connect fails.
- [x] **Build + publish verified** — 0 warnings, 0 errors in Release; self-contained single-file EXE built.

## Completed this session (feature/production-readiness) — Pass 1
- [x] **PRODUCTION_ROADMAP.md** — master task list ordered hardest-to-easiest
- [x] **Race-condition safety in FileIndex** — bounds-check `_entries[i]` everywhere; add `Generation` counter so callers can detect stale snapshots
- [x] **NTFS root `.` path fix** — `ResolvePath` no longer emits `C:\.\Users\...`
- [x] **Logger.cs** — thread-safe file-based logger with daily rotation and 7-day retention at `%LOCALAPPDATA%\QuickFind\logs\`
- [x] **Friendly crash handler** — `DispatcherUnhandledException`, `AppDomain.UnhandledException`, and `TaskScheduler.UnobservedTaskException` all log; UI shows "Open log folder?" instead of a raw stack trace
- [x] **Tray menu: Open Log Folder** entry
- [x] **Manifest → `asInvoker`** — UAC no longer pops on every launch; elevation is opt-in via tray menu ("Restart as Admin" → runas) or Scheduled Task
- [x] **Executable launch confirmation** — .exe / .bat / .ps1 / .msi / .cmd / .vbs / .scr / etc. now require Yes/No before running
- [x] **UTF-8 encoding for content search** — Hebrew / Cyrillic / CJK files no longer mangled
- [x] **HICON handle leak fix** — `CreateFallbackIcon` clones the Icon and destroys the HICON
- [x] **Bounded icon cache (LRU, 512 entries)** — no unbounded growth on pathological filetypes
- [x] **GZip-compressed index** (v3) + atomic temp-rename save + backward-compat load of v1/v2 files
- [x] **Logging wired into** `IndexPersistence`, `RegisterHotkey`, `ExitApp`, `OnExit`, `RestartElevated`, `OpenResult`
- [x] **Silent single-instance exit** — second launch no longer shows a `MessageBox`
- [x] **Build + publish both succeed** — 0 warnings, 0 errors in Release

## In Progress
Nothing currently in flight. See `PRODUCTION_ROADMAP.md` for the full todo list.

## Next Steps (from PRODUCTION_ROADMAP.md — critical items still open)
1. **USN Journal incremental updates** — new / renamed / deleted files need to appear without full re-index (largest remaining architectural gap)
2. **MFT attribute parsing for file sizes** — removes the per-entry disk I/O on size queries
3. **Proper search index (prefix map / trigram)** — replace O(N) scan with O(k) lookup; removes per-keystroke GC storm
4. **Named-pipe single-instance signaling** — second launch should pop the existing window, not silently exit
5. **Installer (MSIX or WiX MSI)** — handles scheduled-task creation, Start Menu, uninstall
6. **Code signing** — removes SmartScreen warnings

## Key Decisions
- **No H.NotifyIcon.Wpf**: Used WindowsForms NotifyIcon instead (more reliable with .NET 8)
- **asInvoker manifest**: App detects elevation at runtime, uses MFT if admin, fallback if not
- **Task Scheduler for admin**: No UAC prompt on startup, scheduled task with HighestAvailable RunLevel
- **FRN masking**: Lower 48 bits for NTFS file reference numbers
- **Lazy path resolution**: Paths resolved on-demand from FRN parent chain, saves memory
- **Index v3**: GZip-compressed body + backward-compat readers for v1/v2; atomic temp-rename writes
- **Bounds-check over locking**: FileIndex bounds-checks stale indices rather than introducing reader/writer locks; simpler, low-cost, and crash-safe

## Known Issues (still open)
- MFT indexer doesn't store file sizes (USN records don't include them); resolved dynamically during size search — **slow on large indexes**
- Old v1 index files will be ignored (version mismatch), requires re-index after update
- Search is still O(N) linear scan over a full snapshot allocation per keystroke — fine at 100 k files, sluggish at 3 M+
- No incremental updates — new/deleted files require manual re-index

## Rollback / Safety
- **Stable baseline tag:** `v1.0.0-baseline` (commit `d823329` — the release commit). Roll back with `git checkout v1.0.0-baseline` if any production-readiness change regresses.
- **Feature branch:** `feature/production-readiness`. Master is untouched.
- **Remote:** both branch and tag pushed to `origin`.

## Important Files
- `QuickFind/QuickFind.csproj` - Project config
- `QuickFind/App.xaml.cs` - Main app logic (tray, hotkey, indexing, Task Scheduler, crash handlers)
- `QuickFind/SearchWindow.xaml.cs` - Search UI logic + context menu + EXE launch confirmation
- `QuickFind/Core/MftIndexer.cs` - MFT reader (the fast path)
- `QuickFind/Core/FileIndex.cs` - In-memory index + persistence (generation counter, bounds-checked)
- `QuickFind/Core/SearchEngine.cs` - Search with name scoring + size filters (UTF-8 content search)
- `QuickFind/Core/IndexPersistence.cs` - v3 GZip format, atomic writes, v1/v2 load compat
- `QuickFind/Core/Logger.cs` - Daily-rotating file logger
- `QuickFind/app.manifest` - asInvoker (was requireAdministrator)
- `QuickFind/Resources/Styles.xaml` - Light gray theme
- `PRODUCTION_ROADMAP.md` - Master task list for 1.0 production-readiness
- `publish/QuickFind.exe` - Published self-contained EXE
