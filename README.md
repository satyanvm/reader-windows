# Reader for Windows

A native Windows reader for PDF and EPUB books, built with C# on .NET 8 and WPF. It has the same features as the [Mac app](https://github.com/satyanvm/reader-mac) and the [Android app](https://github.com/satyanvm/reader-android), and it syncs with them through the same server. The same book opens at the same place on all three.

`Reader.Core` is a line-for-line port of the Mac app's `ReaderCore`: the position model, the restore controller, sync, PDF reflow and EPUB parsing. It keeps the same thresholds, tie-breaks and `float` math. The sync design is [reader-android/docs/position-sync.md](https://github.com/satyanvm/reader-android/blob/main/docs/position-sync.md).

## Build, run, test

You need the .NET 8 SDK. Visual Studio and MSIX packaging aren't needed.

```bash
dotnet build
```

```bash
dotnet run --project src/Reader.App
```

```bash
dotnet run --project src/Reader.App -- "C:\Books\Meditations.pdf"
```

```bash
dotnet test
```

```powershell
scripts\build.ps1
```

`build.ps1` runs the tests, then runs `dotnet publish -c Release -r win-x64 --self-contained` to produce a single-file `publish\win-x64\Reader.exe`.

On Windows, `dotnet test` also runs the WPF tests in `tests/Reader.Core.Tests/App` (snapshots, spreads, the page-turn overlay). On other operating systems only the portable Core tests build and run.

### Configuration

| Setting | Default | |
|---|---|---|
| `READER_SUPPORT_DIR` | `%LOCALAPPDATA%\Reader` | The data folder: database, books, text cache, session, settings and logs |
| `READER_SYNC_BASE_URL` | `https://reader-sync.satyanarayanv2050.workers.dev` | The sync server. Use `http://127.0.0.1:8787` for a local one |
| `READER_GOOGLE_CLIENT_ID` (build), e.g. `dotnet build -p:ReaderGoogleClientId=…` | none | Shows "Continue with Google". Without it the button is hidden |
| `READER_GOOGLE_CLIENT_SECRET` (run time, optional) | none | Google issues "Desktop app" clients a non-secret secret. Set it if the token exchange asks for it |

**Google sign-in:** create an OAuth client of type **Desktop app** in Google Cloud. **Add that client id to the server's `GOOGLE_CLIENT_IDS`**, or the server rejects its tokens. The app uses the system browser, a loopback redirect (`http://127.0.0.1:<port>/`) and PKCE.

### Against a local server

```bash
cd server && npm install && npm start
```

Run that in the Android repo's `server/` folder (Node 22.13+). It serves `http://127.0.0.1:8787` with `EMAIL_CODES` off, so sign-up returns a token immediately. Then run the end-to-end tests:

```bash
READER_E2E_SERVER=http://127.0.0.1:8787 dotnet test --filter EndToEnd
```

### Self-test harness

`SelfTest.cs` is the counterpart of the Mac app's `SelfTest.swift`. It drives the real app and only runs when environment variables are set:

```powershell
$env:READER_SUPPORT_DIR="$env:TEMP\reader-selftest"; $env:READER_SELFTEST="all"; $env:READER_SELFTEST_LOG="$env:TEMP\selftest.jsonl"; $env:READER_SELFTEST_IMPORT="C:\Books\sample.epub"; $env:READER_SELFTEST_EXIT="1"; dotnet run --project src/Reader.App
```

It opens the book and turns pages forward and back, checking that back returns exactly. It takes mid-animation snapshots, switches spreads, themes, continuous scroll, text size, original pages and zoom, and resizes the window. Every step is logged as a JSON line with the measured and saved positions. Snapshots are saved as PNGs (in `selftest-png` next to the log), each with a `flat` check. Other variables: `READER_SELFTEST_SIGNUP` / `_SIGNIN=email:password`, `READER_SELFTEST_DOC`, `READER_SELFTEST_PAGE`, `READER_SELFTEST_LINGER`, `READER_SELFTEST_OUT`. Steps: `all`, `read`, `turns`, `spreads`, `themes`, `pages`, `observe`, `library`.

## Layout

| Path | What |
|---|---|
| `src/Reader.Core/Model` | `ReadingPosition`, `Stamp` (ordinal UTF-16 tie-break), `HybridClock`, `PositionJson` (the wire format; floats written as their double value) |
| `src/Reader.Core/Data` | SQLite (WAL, `synchronous=FULL`) with the Android/Mac schema, `ReadingPositionRepository` (compare-and-store), `DocumentRepository` (import with SHA-256 ids) |
| `src/Reader.Core/Sync` | `SyncEngine`, `SyncScheduler` (backoff, every 15 min, on reconnect, at launch), `HttpSyncApi`, `AccountApi`, `SyncAccount` (sign-in stages, 401 handling) |
| `src/Reader.Core/Library` | `LibrarySync` and `HttpLibraryApi`: books follow the account, tombstones, `library-sync.json` |
| `src/Reader.Core/Reader` | `ReaderController` (the restore state machine), `PositionSaver` (500 ms debounce, 3 s max wait), `PageLayout`, `SpreadLayout`, schedulers |
| `src/Reader.Core/Flow` | `FlowLayout` / `FlowPager`: line-geometry pages, back-from-geometry, spreads with the character hand-off; `IFlowSource` for PDF and EPUB |
| `src/Reader.Core/Text` | `PdfLineExtractor` (PdfPig), `Reflow`, `PageAssembly`, `TextDocument` (background worker, 24-page LRU, disk cache `text-v4`), `EpubBook`, `LenientHtml`, `Hyphenation` |
| `src/Reader.Core/Telemetry` | `RestoreTelemetry`: the seven events, written to `logs/restore-telemetry.log` with counters |
| `src/Reader.App` | WPF: `MainWindow` (library and reader in one window), `Library/`, `Reading/` (`PagesView`, `FlowView`, `PageTurnAnimator`, `ReaderView`), `SettingsWindow`, `Platform/` (Windows.Data.Pdf, Windows.Media.Ocr, DPAPI, file associations, single instance, Google), `SelfTest` |
| `tests/Reader.Core.Tests` | xUnit: every Mac `ReaderCoreTests` scenario ported, plus library sync, flow pagination and spreads, accounts, hyphenation, end-to-end, and WPF tests (Windows only) |

## Windows-specific choices

- **Portable core, Windows shell.** `Reader.Core` targets `net8.0`, so all the logic is unit-tested on any OS. Windows APIs sit behind two small interfaces implemented in the app: `IPageRasterizer` (Windows.Data.Pdf) and `IOcr` (Windows.Media.Ocr, on device). The app targets `net8.0-windows10.0.19041.0`.
- **PDF text:** PdfPig glyphs are grouped into lines in content-stream order, with word gaps turned into spaces. They then go through the same Reflow as on the Mac. The text cache is `text-v4` because the extractor differs from PDFKit.
- **Flowing text is drawn with WPF's `TextFormatter`**, not a FlowDocument, so only the units near the reader are ever laid out. A book of thousands of paragraphs turns pages as fast as a short one. Positions are anchored to a unit and an offset inside it, so pages finishing above the reader can't move what's on screen.
- **Hyphenation:** the public `TextFormatter` has none, so soft hyphens are inserted with Liang's algorithm and the TeX US-English patterns (license in `Text/Hyphenation/NOTICE.txt`).
- **Page-by-page pages show only the lines they own,** clipped to the 24-px margins. The cut line and the previous page's descenders are never drawn, so no cover strips are needed.
- **Back** returns to the remembered start of the page a forward turn came from. Otherwise it computes the page from line geometry: the earliest line or paragraph top from which a page ends exactly here.
- **Page turns** animate two RenderTargetBitmap snapshots with TranslateTransform animations, over the live view (input is never blocked). The view is drawn by one routine shared by screen and snapshot.
- **Session token:** encrypted with DPAPI (CurrentUser) in `session.bin`. **Device id:** a UUID in `device-id`.
- **File associations:** registered under HKCU at first run (Open with, `.epub` default if unclaimed). One instance runs per library; a second launch forwards its files over a named pipe.
- **Lifecycle flushes:** going back, window close, deactivation, exit, suspend (`PowerModeChanged`) and session end. Nothing depends on them.
- Per-monitor v2 DPI (app manifest), window placement saved in `settings.json`, dark title bar via `DwmSetWindowAttribute` for Dark and Black.

## Not verified (report)

This was built on a Mac with no Windows machine available. All 137 portable tests and the 4 end-to-end tests ran and pass. The whole solution, including the Windows test configuration, compiles with no warnings, and the self-contained `win-x64` publish succeeds. The following could **not** be run or checked:

1. **The WPF app was never launched.** No self-test run and no PNGs were looked at. The WPF tests (snapshot non-blankness, spread halves, overlay removal) compile but have not run. **First thing to do on Windows:** `dotnet test`, then the self-test above, and look at the PNGs.
2. **Windows.Data.Pdf rendering and Windows.Media.Ocr were not exercised.** The OCR pipeline is covered by tests with a fake OCR engine.
3. **TextFormatter behaviour assumed but unseen:** soft hyphens breaking with a visible hyphen, justification, `LineHeight`, and the line heights the page breaks rely on.
4. **Input feel:** touchpad swipe thresholds (24 px, momentum ignored), horizontal swipes via `WM_MOUSEHWHEEL`, pinch zoom, the 250 ms wheel lockout and animation smoothness all need tuning on real hardware.
5. **File associations, single instance, DPAPI, dark title bar and Google sign-in** were never run. Google also needs a client id.
6. **Library sync has no server.** Neither the sync server repository nor the deployed Worker has `/v1/library` (both return 404), and the Mac repo has no `LibrarySync.swift`. The client follows the API in the specification exactly. It is tested against an in-memory implementation of that API and, end to end, with real accounts on the local server, where tokens are checked through `GET /v1/me`. The server was not changed, as instructed. Real cross-device library sync needs those endpoints added to the server.
