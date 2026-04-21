# QuickFind — Production Readiness Roadmap

This document tracks everything that needs to happen before QuickFind is considered production-ready.
Tasks are ordered roughly **hardest to easiest**. Check off items as they're completed.

---

## 🔴 Critical — Must-have for 1.0

### Architecture & Correctness

- [x] **USN Journal — Incremental Index Updates**
  Background per-drive watchers (polling every 5 s) read the NTFS change journal via `FSCTL_QUERY_USN_JOURNAL` + `FSCTL_READ_USN_JOURNAL` and apply create/rename/delete records to the in-memory index. Cursor (journal ID + NextUsn per drive) is persisted to `%LOCALAPPDATA%\QuickFind\usn.state` so updates survive restarts. Journal-rollover detection falls back to the current journal tail (avoids stale cursors causing a crash). Admin-only — no-op on non-elevated launches.
  *Files:* new `Core/UsnWatcher.cs`, `Core/FileIndex.cs` (`UsnAddOrUpdate`, `UsnRemove`, tombstone-skipping), `Helpers/NativeMethods.cs` (new structs + DeviceIoControl overloads), `App.xaml.cs` (start after initial index, restart on reindex, dispose on exit).

- [~] **File Sizes from MFT directly (no per-entry I/O)** — *partial*
  `MftIndexer` still stores `size = 0` (USN records don't carry size), but `ResolveSizeSafe` now writes the resolved size back into the entry (`FileIndex.TrySetEntrySize`). First size query on a cold cache still hits disk once per file; subsequent queries are O(1). For a proper zero-I/O solution we'd need to parse raw `$MFT` records via `FSCTL_GET_NTFS_FILE_RECORD` and walk the `$STANDARD_INFORMATION` / `$DATA` attributes — substantial work, deferred.
  *Files:* `Core/FileIndex.cs`, `Core/SearchEngine.cs`.

- [~] **Proper Search Index (prefix map / trigram)** — *partial*
  Current search was O(N) linear scan + per-keystroke full snapshot allocation (`FileIndex.GetSnapshot`) + per-entry `ToLowerInvariant`.
  On 3 M files each keystroke allocated ~100 MB of tuples and allocated a lowercase copy of every name → GC storm + UI jank.
  **Done:** replaced `GetSnapshot`+foreach with streaming `ScanForSearch` (zero allocation per scan) and switched `ScoreName` to `OrdinalIgnoreCase` so no lowercased copies are allocated. That alone removes the ~100 MB-per-keystroke GC pressure on large indexes.
  **Still pending:** a proper prefix/trigram index would bring search from O(N) to O(k) for prefix matches. Not required for acceptable perceived latency at <1 M files.
  *Files:* `Core/FileIndex.cs`, `Core/SearchEngine.cs`.

- [x] **Race condition: Reindex clears index while search is in-flight**
  `ReindexAsync` calls `_index.Clear()` while `SearchEngine` may hold index references → `IndexOutOfRangeException` in `ResolveSizeSafe`.
  Fix with generation token or `ReaderWriterLockSlim`.
  *Files:* `Core/FileIndex.cs`, `Core/SearchEngine.cs`.

### UX Blockers

- [x] **Named-Pipe single-instance signaling**
  Second launch now connects to the running instance over a `NamedPipeServerStream` named `QuickFind_SingleInstance_Pipe`, sends `SHOW`, and the running instance pops its search window before the second process exits.
  *Files:* new `Core/SingleInstance.cs`, wired into `App.xaml.cs`.

- [x] **Manifest → `asInvoker` + dynamic elevation**
  `requireAdministrator` currently forces UAC on every manual launch. Installer (or first-run flow) should create the Scheduled Task; the EXE itself must run as plain user.
  *Files:* `app.manifest`, `App.xaml.cs` (elevation-on-demand via scheduled task).

### Security & Robustness

- [x] **Confirmation before launching executables**
  `Enter` on a `.exe` / `.bat` / `.ps1` / `.msi` / `.cmd` / `.vbs` result launches immediately. Needs an opt-out confirmation dialog.
  *Files:* `SearchWindow.xaml.cs`.

- [x] **Structured logging (file-based, rolling)**
  Every `catch { }` currently swallows errors silently. Add a lightweight logger that writes to `%LOCALAPPDATA%\QuickFind\logs\quickfind.log` with daily rotation.
  *Files:* new `Core/Logger.cs`, callsites throughout.

- [x] **Friendly global crash handler**
  Currently dumps full stack trace in a `MessageBox`. Should log + show a short "Something went wrong" message with a "View log" button.
  *Files:* `App.xaml.cs`.

---

## 🟡 Important — Should-have for 1.0

### Correctness

- [x] **UTF-8 encoding for content search**
  `StreamReader(path)` uses default ANSI — Hebrew UTF-8 without BOM is misread.
  Use `new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true)`.
  *Files:* `Core/SearchEngine.cs`.

- [x] **NTFS root entry causes `C:\.\...` in paths**
  MFT root (FRN = 5) has name `.` — when walking parent chain this gets pushed onto the path stack.
  Guard against pushing `.` or `..` at the top of the chain.
  *Files:* `Core/FileIndex.cs`.

- [x] **HICON handle leak in fallback icon**
  `Icon.FromHandle(bmp.GetHicon())` never calls `DestroyIcon`.
  *Files:* `App.xaml.cs`.

- [x] **Bound icon cache**
  Static `_iconCache` in `SearchWindow` grows without limit.
  Use `LruCache<string, ImageSource>` capped at ~200 entries.
  *Files:* `SearchWindow.xaml.cs`.

### Installation & Distribution

- [ ] **Installer (MSIX or WiX MSI)**
  - Start Menu shortcut
  - Add / Remove Programs entry
  - Creates the `QuickFindAdmin` scheduled task on install (no UAC on relaunch)
  - Uninstalls cleanly (removes task, removes registry keys, removes index folder)

- [ ] **Code signing**
  Unsigned EXE → SmartScreen warning on every download.
  Options: Certum Open Source cert, SignPath.io (free for OSS), Azure Trusted Signing.

- [ ] **Auto-update mechanism**
  Squirrel.Windows / Velopack / WinGet manifest.

### Testing

- [x] **Unit tests (xUnit)**
  `QuickFind.Tests` project with 31 tests covering `SearchEngine.ScoreName`, `SearchEngine.ParseSizeFilter`, `FileIndex.ResolvePath` (including the NTFS-root `.` fix), tombstone / USN mutation semantics, and `FileIndex` round-trip through both the raw binary and GZip + BufferedStream formats. Uses `InternalsVisibleTo` so we don't leak test surface into the public API.

- [x] **CI (GitHub Actions)**
  `.github/workflows/ci.yml`: restore → build (Release) → test → upload `.trx` results on every push to `master` and `feature/**` and every PR to `master`. On master / tags, also builds and uploads the self-contained `QuickFind.exe` as an artifact.

- [ ] **Smoke-test script**
  Launch EXE, wait for indexing, run a known query, verify results, exit cleanly.

---

## 🟢 Nice-to-have — 1.1+

### Performance / Storage

- [x] **GZip-compressed index file**
  ~100 MB raw → ~25-30 MB compressed, same cold-load time thanks to faster disk I/O.
  *Files:* `Core/IndexPersistence.cs`.

- [ ] **Memory-mapped / streamed index load**
  For users with >10 M files, load lazily.

- [ ] **Icon cache LRU + disk cache**
  Persist icon cache to disk so cold launch is fast.

### Features

- [ ] **Dark mode toggle**
  `Resources/Styles.xaml` currently ships light-only. Add a dark palette + runtime toggle.

- [ ] **Hebrew / RTL localization**
  UI strings → `.resx`; flip `FlowDirection` based on `CultureInfo`.

- [ ] **More search operators**
  - `ext:pdf` — extension filter
  - `folder:projects` — restrict to path substring
  - `modified:<7d` — modified date filter
  - Regex mode toggle

- [ ] **"Show all results" instead of 100-item cap**
  Add virtualization (`VirtualizingStackPanel`) and remove hard cap.

- [ ] **File preview pane**
  Optional right-side preview for images, text, PDFs.

- [ ] **Quick actions: rename, move, copy**

### System integration

- [x] **"Run as Admin" in result context menu** — appears only for launchable types (`.exe`, `.msi`, `.bat`, `.cmd`, `.ps1`). Uses `ShellExecute "runas"`.

- [ ] **Tray icon: indexing progress indicator** (spinning badge / percentage).

- [ ] **Pause / resume indexing** when on battery / when CPU is busy.

- [ ] **Explorer-restart uses `TaskbarCreated` broadcast** instead of `Kill()`.

### Polish

- [ ] **Better empty-state art / first-run welcome**
- [ ] **About dialog with version, OSS licenses, GitHub link**
- [ ] **Settings window** — hotkey, theme, drives to index, max results, scheduled re-index time
- [ ] **Telemetry opt-in** (Application Insights / Sentry) for crash reports only

---

## Legend

- 🔴 **Critical** — ship-blockers; QuickFind is not production-ready without these
- 🟡 **Important** — polish / robustness; strongly recommended for 1.0
- 🟢 **Nice-to-have** — post-1.0 features
