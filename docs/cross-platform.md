# Linux and macOS

Libzora uses one portable Avalonia UI with separate Windows, Linux and macOS
desktop heads. Windows retains WPD support for MTP-only Kindle devices. Linux
supports Kindles exposed as mounted USB storage. macOS supports mounted storage
and MTP devices through the bundled libmtp stack.

## User data and secrets

- Linux data: `$XDG_DATA_HOME/Libzora`, falling back to
  `~/.local/share/Libzora`; root configuration uses `$XDG_CONFIG_HOME/Libzora`
  or `~/.config/Libzora`.
- macOS data and root configuration:
  `~/Library/Application Support/Libzora`.
- Linux stores its wrapping key in Secret Service using `secret-tool`.
- macOS stores its wrapping key in the login Keychain.
- Windows continues to use DPAPI and its existing data layout.

## Plans and cross-device sync

The **Free / Trial / Lifetime** plans apply across supported platforms. Free includes full local reading and library management, USB Kindle features, plugins, and your own AI APIs, subject to each platform's device support. Trial is a **7-day free trial, once per account**, claimed explicitly by the user rather than activated on registration or sign-in. Lifetime costs **¥88 once**, is permanent, and needs no renewals.

An active Trial or Lifetime unlocks cross-device books, reading progress, bookmarks, and annotations through **your own S3 / WebDAV**. Every account permits up to **6 simultaneously signed-in devices**. You provide the storage and pay its storage and traffic costs. No plan provides official cloud storage or official cloud sync; the account service handles identity and feature entitlements only and does not receive your books. Trial expiry disables only paid sync features and does not delete your own storage data or local library.

China and International accounts, entitlements, local libraries, and sync namespaces remain isolated. Use matching regional settings on devices meant to sync together; account sessions, storage credentials, and encryption keys remain locally protected and are excluded from library backups. See [account service](account-server.md), [encrypted backups](backup-encryption.md), and [snapshot compatibility](s3-snapshot-compatibility.md).

Lifetime entitlements bind directly to the regional account. Protected administrators grant permanent access based on external authorization; clients can only refresh and validate it, not grant themselves access. Purchase and enablement methods follow actual channel announcements; refresh entitlements after enablement. The existing 7-day and ¥88 rules do not imply an available purchase channel, automatic payment, or payment gateway. The service retains only necessary account/recovery, region isolation, 6-device/session security, trial, entitlement validation/expiry, deactivation, and protected admin grant/revocation/audit functions, with authentication and CSRF. Products/orders/payments, activation-code generation/redemption/management, official-cloud objects/quotas/usage, feedback screenshot uploads, and cloud cleanup are removed from the active service.

Historical paid time-limited entitlements keep their original expiry and do not become permanent. Self-service closure becomes password-and-typed-confirmation account deactivation with session revocation; necessary license/audit records remain, so it is not server-data deletion. Local and user-provided storage data remain. Existing databases, ledgers, historical redeemed codes, and cloud files are not migrated or deleted by this change. Historical codes and ledgers are retained only for necessary read-only compatibility, not renewed redemption.

## macOS feature parity

The macOS head matches the Windows build beyond device access:

- A second launch activates the running instance over a Unix domain socket
  (lock file in `~/Library/Application Support/Libzora`). A window parked in the
  menu bar is shown again, and files passed to the second launch are imported.
- The native menu bar replaces Avalonia's default "About Avalonia" menu with
  About, Settings (Cmd+,), Import (Cmd+I), Close Window (Cmd+W), window,
  shortcut-help and manual entries; it follows the selected UI language.
- Finder "Open With" works for EPUB, PDF, MOBI/PRC/AZW/AZW3 and TXT. Documents
  opened while Libzora is already running are queued until the library is
  ready, then imported through the same dialog as a manual import.
- USB attach/detach events arrive through IOKit and refresh the device list.
- Unhandled exceptions are written to `data/logs/crash.log`. Only loose
  Debug executables also write `libzora-crash.log` next to the binary; a file
  added inside `Libzora.app` would break the bundle's code signature.
- In-app updates install from the `Libzora-{version}-osx-{arm64|x64}.dmg`
  release asset. A detached helper waits for the app to exit, mounts the disk
  image read-only, copies `Libzora.app` out with `ditto`, verifies the
  downloaded bundle's code signature, stops the bundled MCP server, replaces
  `Libzora.app`, verifies the installed signature again, reopens it, and
  restores the previous bundle if anything fails. The option is only offered
  when the bundle directory is writable and the app is not running from App
  Translocation.
- Send to Kindle streams queued files into the Amazon page as real `File`
  objects through the shared webview bridge, so the platform no longer depends
  on the Windows-only WebView2 file-input API.
- MTP Kindles gain annotation database read/write with local and device-side
  backups plus SHA-256 verification, bookshelf thumbnails, and `.sdr` sidecar
  cleanup. The account annotation database is read before `My Clippings.txt`.
- Custom shortcuts accept Command (`Meta`) as well as Control, and the TTS
  cache uses `~/Library/Caches/Libzora/tts`.
- The MCP server is published self-contained into `Contents/MacOS/mcp` and uses
  libmtp plus mounted storage for device tools.
- `scripts/uninstall-macos.sh` removes the bundle, library, settings, backups,
  caches and Keychain entry; pass `--keep-data` to remove only the app. See
  [macos-uninstall.md](macos-uninstall.md).

## Build and package

Install the exact .NET SDK version pinned by the repository root `global.json`
before building. Release scripts run from the repository root and refuse other
SDK versions so Linux, macOS and Windows packages are produced by the same SDK.

```sh
dotnet --version # must print 10.0.400
dotnet build src/Libzora.Desktop.Linux/Libzora.Desktop.Linux.csproj -c Release
dotnet build src/Libzora.Desktop.MacOS/Libzora.Desktop.MacOS.csproj -c Release
bash scripts/build-linux-release.sh 1.1.3 artifacts/linux linux-x64
bash scripts/build-macos-release.sh 1.1.3 artifacts/macos osx-arm64
```

macOS release packaging also requires Xcode 26 or newer to compile the layered
Icon Composer artwork. Select it with `xcode-select` or `DEVELOPER_DIR` when
multiple Xcode versions are installed.

Each macOS build produces a single `Libzora-{version}-{rid}.dmg` used both for
manual installation and by the in-app updater. The DMG contains
`Libzora.app` and an `Applications` symlink, so the standard drag-to-Applications
install works. Locally styled images use the generated
`scripts/macos/dmg-background.png`; the Finder layout pass is best effort with a
timeout. Set `LIBZORA_DMG_STYLE=0` to create the compressed DMG directly, without
mounting an intermediate writable image or automating Finder. Both macOS CI
workflows use this non-interactive path; local builds keep the styled default.

Bundle verification checks both compiled `Assets.car` and a decodable 1024-pixel
ICNS fallback, plus the bundle signature. macOS 26 and newer also run
`assetutil -Z`. macOS 15's system CoreUI validator cannot decode Xcode 26 Icon
Composer renditions, so it must not reject the new layered assets as invalid;
older hosts still verify the fallback icon and the sealed bundle resources.

`build-macos-release.sh` builds libmtp and libusb for the package's architecture
into `artifacts/macos-deps/<arch>/`. When the target differs from the build
host, `build-macos-mtp-deps.sh` cross-compiles them with clang against the
universal macOS SDK, so either package can be produced from one Mac. The
Release and Development Build workflows still use native `macos-15`
(`osx-arm64`) and `macos-15-intel` (`osx-x64`) runners to keep CI fast.

Pass `osx-universal` as the RID for one bundle that runs natively on both
architectures. .NET self-contained output embeds RID-specific native libraries
and ReadyToRun assemblies, so a single set of files cannot be lipo-merged;
instead `Contents/MacOS/Libzora` is a universal launcher and the complete
`osx-x64` and `osx-arm64` payloads live in `Contents/MacOS/x86_64` and
`Contents/MacOS/arm64`. In-app updates still fetch the matching
single-architecture package, which replaces the universal bundle.

The Linux package is self-contained for .NET but still depends on the native
webview, font, Secret Service and desktop libraries declared by the `.deb`.
The reader follows Avalonia's Linux `NativeWebView` path and prefers WPE
WebKit when `libWPEWebKit-2.0.so.1` is installed. Ubuntu 22.04 does not package
that WPE 2.0 ABI, so the `.deb` remains installable there through the
WebKitGTK 4.1 fallback.

Windows and macOS read-aloud use installed local system voices only. Windows
uses SAPI through the built-in Windows PowerShell host; macOS uses `/usr/bin/say`.
Both generate PCM WAV files for the shared cache and existing in-process players.
No book text is uploaded and no Python/edge-tts environment is installed on those
platforms. Missing voices must be installed in system settings. On Windows and
macOS, read-aloud settings can additionally download optional sherpa-onnx voice
packs (Kokoro v1.1-zh int8/fp32 and MeloTTS zh/en) from the pinned GitHub Release,
verify their SHA-256 and run them fully offline; nothing is downloaded until the
user chooses a pack. Linux retains its existing online edge-tts integration.

The Settings > Diagnostics page performs the same local checks at runtime and
shows the detected WebView library, writable data paths, bundled PDF parser,
Calibre, TTS and Kindle service. PDFium native assets ship for Windows, Linux
and macOS. PDF pages and first-page covers use the same renderer; its text
geometry drives selection, underlines, highlights and editable comments.
Continuous scrolling, single-page and two-page modes share that page map.
View rotation is saved with reading progress; selection, annotations and outline
destinations rotate with the page. Paper, chrome and neutral ink share the reader
theme surface, while saturated colors in figures are preserved. Classic keeps the
original colors.
The reader renders clipped BGRA regions at the current display scale directly
from PDFium, avoiding PNG round-trips and enlarged low-resolution page images.
Its bounded page cache is independent of the full document's scroll extent.
The current page becomes usable before outlines and the text-only search index
finish loading in the background. Closing a book cancels that background work.
Embedded outlines retain their hierarchy and page destinations. PDFs without
outlines have a page list. Text PDFs also support local search, AI context and
TTS. Scanned pages support viewing, page navigation, bookmarks and page notes;
OCR is not included. PDF page notes can be anchored to arbitrary normalized page
positions. PDF zoom and the position within a page are restored without changing
EPUB typography preferences.

The `Development Build` GitHub Actions workflow runs only when manually
dispatched. It chooses the next development version based on the latest stable
release (or a manually supplied base version). Its `platform` input defaults to
`all`, building Windows, Linux and macOS packages. A complete build on `dev`,
`develop` or a `dev/` branch publishes a GitHub pre-release tagged with the
development version. Selecting `windows`, `linux` or `macos` builds only that
platform and keeps its artifacts for seven days without publishing an incomplete
Release. The stable `Release` workflow still builds and verifies every platform
before publishing. It pins all builds and release notes to one immutable source
commit, and creates the stable tag only after all builds, smoke tests and artifact
preparation succeed. A failed build can retry the same unconsumed version with a
fix; explicitly rebuilding an existing tag uses its original commit and never
moves the tag.

All desktop workflows select the exact SDK from the checked-out revision's
`global.json`, reuse an already installed matching SDK, and cache NuGet packages
separately by runner OS and architecture. macOS also caches MTP dependencies by
host/target architecture, Xcode toolchain and source-build inputs; the dependency
builder still verifies its stamp, binaries and corresponding source before reuse.
A cache miss is normal and the first successful run populates it. Package publish
already builds the platform head, so CI does not run an extra standalone head
build immediately beforehand.

Quick test coverage is unchanged. Portable test projects run in separate steps
and result directories; Windows keeps its solution-wide test run. Both have
console progress, TRX results and a five-minute hang diagnostic (`--blame-hang`).
Each test step has a 15-minute limit. Failed jobs upload test
results and any diagnostic dumps as non-release artifacts retained for seven
days. Linux validation cancels superseded runs for the same PR/ref; stable
release jobs are never automatically cancelled by a newer release.

Calibre is not bundled in any Windows, Linux or macOS archive. It is optional and is
discovered from the application directory, standard install locations or
`PATH`, or the user can select `ebook-convert` in Settings. On Debian/Ubuntu,
the `.deb` lists `calibre` only as a suggested package, so installing Libzora
does not automatically install it.

The Settings page also offers explicit user-initiated installation buttons.
Windows downloads the official signed MSI and launches Windows Installer;
Linux runs calibre's official isolated installer into `~/calibre-bin` without
root; macOS verifies the official DMG and application signature before placing
`calibre.app` in `~/Applications`. KFX Input is downloaded from the project's
mirror when available (calibre's official plugin index otherwise), validated
as a plugin ZIP and installed with the detected `calibre-customize`. These
downloads never become part of a Libzora release artifact.

Local macOS builds and GitHub Releases use ad-hoc signing when Apple
distribution credentials are not configured. The Gatekeeper override steps
required for that build are in `macos-adhoc-install.md`. Development Build
pre-releases are always ad-hoc signed; only the stable Release workflow imports
distribution credentials.

For normal Gatekeeper-approved distribution, configure these repository
secrets:
`APPLE_CERTIFICATE_P12_BASE64`, `APPLE_CERTIFICATE_PASSWORD`,
`APPLE_NOTARY_KEY_BASE64`, `APPLE_NOTARY_KEY_ID`, and `APPLE_NOTARY_ISSUER`.
`APPLE_SIGNING_IDENTITY` and `APPLE_KEYCHAIN_PASSWORD` are optional; the
workflow discovers the Developer ID identity and generates a temporary
keychain password when they are omitted. The release job imports the
certificate, signs with the hardened runtime, notarizes, staples and validates
the app, then signs, notarizes and staples the DMG as well. Supplying only part
of the required credentials fails the release instead of silently falling back
to ad-hoc signing. Step-by-step certificate and API-key setup, including local
notarization without CI, is in [macos-notarization.md](macos-notarization.md).

`scripts/macos/Libzora.entitlements` grants the hardened runtime's JIT,
unsigned-executable-memory and library-validation exceptions required by the
.NET runtime and the bundled libmtp/libusb. The build script signs nested
Mach-O images from the inside out and applies entitlements to the real
executables; it no longer relies on the deprecated `--deep` signing shortcut.
Both macOS workflows run the portable test projects and launch the packaged
bundle for a 20-second smoke test.
