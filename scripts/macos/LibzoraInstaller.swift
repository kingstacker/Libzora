import AppKit
import Foundation

// MARK: - Localization

private struct InstallerStrings {
    let isChinese: Bool

    init(languages: [String] = Locale.preferredLanguages) {
        self.isChinese = languages.first?.lowercased().hasPrefix("zh") ?? false
    }

    var windowTitle: String { isChinese ? "安装 Libzora" : "Install Libzora" }
    var headingInstalling: String { isChinese ? "正在安装 Libzora…" : "Installing Libzora…" }
    var headingSuccess: String { isChinese ? "Libzora 安装完成" : "Libzora Installed" }
    var headingFailed: String { isChinese ? "未能安装 Libzora" : "Installation Failed" }

    var stepChecking: String { isChinese ? "正在检查安装包…" : "Checking installer package…" }
    var stepClosingRunning: String { isChinese ? "正在关闭正在运行的 Libzora…" : "Closing running Libzora…" }
    func stepInstalling(to directoryDisplay: String) -> String {
        isChinese ? "正在安装到 \(directoryDisplay)…" : "Installing to \(directoryDisplay)…"
    }
    var stepLaunching: String { isChinese ? "正在打开 Libzora…" : "Launching Libzora…" }
    func detailInstalled(at path: String) -> String {
        isChinese ? "已安装到 \(path)" : "Installed to \(path)"
    }
    var detailAdminCancelled: String {
        isChinese
            ? "需要管理员权限才能安装到“应用程序”文件夹，你也可以安装到个人应用程序文件夹（~/Applications）。"
            : "Administrator permission is required to install into /Applications, or you can install into your user Applications folder (~/Applications)."
    }

    var retryButton: String { isChinese ? "重试安装" : "Retry" }
    var userAppsButton: String { isChinese ? "安装到 ~/Applications" : "Install to ~/Applications" }
    var openButton: String { isChinese ? "打开 Libzora" : "Open Libzora" }
    var closeButton: String { isChinese ? "关闭" : "Close" }

    var missingBundleMessage: String {
        isChinese
            ? "安装器中缺少完整的 Libzora.app 应用包，请重新下载安装包。"
            : "The embedded Libzora.app bundle is missing or incomplete. Please re-download the installer."
    }
    var runningAppTimeoutMessage: String {
        isChinese
            ? "正在运行的 Libzora 未能退出，请先手动退出后再重试安装。"
            : "The running copy of Libzora could not be closed. Quit Libzora and try again."
    }
}

// MARK: - Core Installer Engine

enum InstallerError: LocalizedError {
    case missingEmbeddedBundle(String)
    case runningAppCouldNotTerminate
    case administratorAuthorizationCancelled
    case installFailed(String)

    var errorDescription: String? {
        let strings = InstallerStrings()
        switch self {
        case .missingEmbeddedBundle:
            return strings.missingBundleMessage
        case .runningAppCouldNotTerminate:
            return strings.runningAppTimeoutMessage
        case .administratorAuthorizationCancelled:
            return strings.detailAdminCancelled
        case .installFailed(let message):
            return message
        }
    }
}

struct InstallerOptions {
    var targetURL: URL
    var launchAfterInstall: Bool
    var ejectVolumeAfterInstall: Bool
    var headless: Bool

    static func parse(arguments: [String], homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser) -> InstallerOptions {
        var targetPath = "/Applications/Libzora.app"
        var launch = true
        var eject = true
        var headless = false

        var index = 1
        while index < arguments.count {
            let arg = arguments[index]
            switch arg {
            case "--install-to":
                if index + 1 < arguments.count {
                    targetPath = (arguments[index + 1] as NSString).expandingTildeInPath
                    index += 1
                    headless = true
                }
            case "--no-launch":
                launch = false
            case "--no-eject":
                eject = false
            case "--headless":
                headless = true
            default:
                break
            }
            index += 1
        }

        if targetPath.hasPrefix("~/") {
            targetPath = homeDirectory.appendingPathComponent(String(targetPath.dropFirst(2))).path
        }

        return InstallerOptions(
            targetURL: URL(fileURLWithPath: targetPath).standardizedFileURL,
            launchAfterInstall: launch,
            ejectVolumeAfterInstall: eject,
            headless: headless
        )
    }
}

enum LibzoraInstallCore {
    static let targetBundleIdentifier = "io.github.kingstacker.libzora"

    // Paths are always passed as positional arguments; the script itself never interpolates them.
    static let installScript = #"""
#!/bin/sh
set -u
source_app="$1"
target_app="$2"
if [ ! -d "$source_app" ] || [ ! -x "$source_app/Contents/MacOS/Libzora" ]; then
  echo "Invalid embedded Libzora.app bundle." >&2
  exit 2
fi
target_dir="$(/usr/bin/dirname "$target_app")"
if ! /bin/mkdir -p "$target_dir"; then
  echo "Unable to create destination directory: $target_dir" >&2
  exit 1
fi
stage="$(/usr/bin/mktemp -d "$target_dir/.libzora-install.XXXXXX" 2>/dev/null)" \
  || stage="$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/libzora-install.XXXXXX")" || exit 1
cleanup() {
  /bin/rm -rf "$stage"
}
trap cleanup EXIT

if ! /usr/bin/ditto "$source_app" "$stage/Libzora.app"; then
  echo "Failed to copy Libzora.app." >&2
  exit 1
fi
if ! /usr/bin/codesign --verify --deep --strict "$stage/Libzora.app" >/dev/null 2>&1; then
  echo "Code signature verification failed for the staged Libzora.app." >&2
  exit 1
fi

/bin/ps -axo pid=,command= | while read -r pid command; do
  case "$command" in
    "$target_app"/Contents/MacOS/mcp/Libzora.McpServer*|"$target_app"/Contents/MacOS/*/mcp/Libzora.McpServer*)
      /bin/kill "$pid" 2>/dev/null || true
      ;;
  esac
done

backup="$target_app.libzora-old"
/bin/rm -rf "$backup"
if [ -e "$target_app" ]; then
  if ! /bin/mv "$target_app" "$backup"; then
    echo "Failed to move the existing Libzora.app aside." >&2
    exit 1
  fi
fi
if ! /bin/mv "$stage/Libzora.app" "$target_app"; then
  if [ -e "$backup" ]; then
    /bin/rm -rf "$target_app"
    /bin/mv "$backup" "$target_app" >/dev/null 2>&1 || true
  fi
  echo "Failed to install Libzora.app into $target_dir." >&2
  exit 1
fi
if ! /usr/bin/codesign --verify --deep --strict "$target_app" >/dev/null 2>&1; then
  /bin/rm -rf "$target_app"
  if [ -e "$backup" ]; then
    /bin/mv "$backup" "$target_app" >/dev/null 2>&1 || true
  fi
  echo "Installed Libzora.app failed signature verification." >&2
  exit 1
fi
/bin/chmod -R u+rwX,go+rX "$target_app" 2>/dev/null || true
/usr/bin/xattr -dr com.apple.quarantine "$target_app" >/dev/null 2>&1 || true
/bin/rm -rf "$backup"
exit 0
"""#

    static func resolveEmbeddedBundleURL(installerBundleURL: URL = Bundle.main.bundleURL) -> URL {
        installerBundleURL
            .appendingPathComponent("Contents", isDirectory: true)
            .appendingPathComponent("Helpers", isDirectory: true)
            .appendingPathComponent("Libzora.app", isDirectory: true)
    }

    static func isValidEmbeddedBundle(_ bundleURL: URL) -> Bool {
        let executableURL = bundleURL
            .appendingPathComponent("Contents", isDirectory: true)
            .appendingPathComponent("MacOS", isDirectory: true)
            .appendingPathComponent("Libzora", isDirectory: false)
        var isDir: ObjCBool = false
        let exists = FileManager.default.fileExists(atPath: bundleURL.path, isDirectory: &isDir)
        return exists && isDir.boolValue && FileManager.default.isExecutableFile(atPath: executableURL.path)
    }

    static func mountedVolumePath(for bundleURL: URL) -> String? {
        let path = bundleURL.standardizedFileURL.path
        if path.contains("/AppTranslocation/") {
            return nil
        }
        guard path.hasPrefix("/Volumes/") else {
            return nil
        }
        let components = path.split(separator: "/", omittingEmptySubsequences: true)
        guard components.count >= 2, components[0] == "Volumes" else {
            return nil
        }
        return "/Volumes/" + components[1]
    }

    static func shellSingleQuoted(_ value: String) -> String {
        "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    static func appleScriptStringLiteral(_ value: String) -> String {
        let escaped = value
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
        return "\"\(escaped)\""
    }

    static func terminateRunningInstances(matching targetURL: URL) -> Bool {
        let standardizedTarget = targetURL.standardizedFileURL.path
        let running = NSRunningApplication.runningApplications(withBundleIdentifier: targetBundleIdentifier)
        let matching = running.filter { app in
            if app.bundleURL?.standardizedFileURL.path == standardizedTarget {
                return true
            }
            return standardizedTarget == "/Applications/Libzora.app"
        }
        guard !matching.isEmpty else { return true }

        for app in matching where !app.isTerminated {
            _ = app.terminate()
        }
        let deadline = Date().addingTimeInterval(5.0)
        while Date() < deadline {
            if matching.allSatisfy({ $0.isTerminated }) {
                return true
            }
            Thread.sleep(forTimeInterval: 0.1)
        }
        for app in matching where !app.isTerminated {
            _ = app.forceTerminate()
        }
        let forceDeadline = Date().addingTimeInterval(2.0)
        while Date() < forceDeadline {
            if matching.allSatisfy({ $0.isTerminated }) {
                return true
            }
            Thread.sleep(forTimeInterval: 0.1)
        }
        return matching.allSatisfy { $0.isTerminated }
    }

    static func canWriteDirectly(to targetURL: URL) -> Bool {
        let fm = FileManager.default
        let parent = targetURL.deletingLastPathComponent()
        guard fm.fileExists(atPath: parent.path) else {
            // Check nearest existing ancestor directory.
            var ancestor = parent
            while ancestor.path != "/" && !fm.fileExists(atPath: ancestor.path) {
                ancestor = ancestor.deletingLastPathComponent()
            }
            return fm.isWritableFile(atPath: ancestor.path)
        }
        guard fm.isWritableFile(atPath: parent.path) else {
            return false
        }
        let probe = parent.appendingPathComponent(".libzora-write-probe-\(UUID().uuidString)")
        let created = fm.createFile(atPath: probe.path, contents: Data())
        if created {
            try? fm.removeItem(at: probe)
        }
        if !created {
            return false
        }
        if fm.fileExists(atPath: targetURL.path) {
            return fm.isWritableFile(atPath: targetURL.path)
        }
        return true
    }

    static func performInstall(
        sourceBundleURL: URL,
        targetBundleURL: URL,
        allowPrivilegeEscalation: Bool,
        onProgress: ((Double, String) -> Void)? = nil
    ) throws {
        let strings = InstallerStrings()
        onProgress?(0.15, strings.stepChecking)
        guard isValidEmbeddedBundle(sourceBundleURL) else {
            throw InstallerError.missingEmbeddedBundle(sourceBundleURL.path)
        }

        onProgress?(0.30, strings.stepClosingRunning)
        guard terminateRunningInstances(matching: targetBundleURL) else {
            throw InstallerError.runningAppCouldNotTerminate
        }

        let directoryDisplay = targetBundleURL.deletingLastPathComponent().path == "/Applications"
            ? (strings.isChinese ? "“应用程序”" : "Applications")
            : targetBundleURL.deletingLastPathComponent().path
        onProgress?(0.55, strings.stepInstalling(to: directoryDisplay))

        let scriptURL = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("libzora-install-\(UUID().uuidString).sh")
        try installScript.write(to: scriptURL, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: scriptURL.path)
        defer {
            try? FileManager.default.removeItem(at: scriptURL)
        }

        if canWriteDirectly(to: targetBundleURL) {
            let directResult = runDirectInstall(scriptURL: scriptURL, sourceURL: sourceBundleURL, targetURL: targetBundleURL)
            if directResult.exitCode == 0 {
                onProgress?(0.95, strings.stepLaunching)
                return
            }
            if !allowPrivilegeEscalation {
                throw InstallerError.installFailed(directResult.output)
            }
        } else if !allowPrivilegeEscalation {
            throw InstallerError.installFailed("Destination is not writable without administrator privileges: \(targetBundleURL.path)")
        }

        try runPrivilegedInstall(scriptURL: scriptURL, sourceURL: sourceBundleURL, targetURL: targetBundleURL)
        onProgress?(0.95, strings.stepLaunching)
    }

    private static func runDirectInstall(scriptURL: URL, sourceURL: URL, targetURL: URL) -> (exitCode: Int32, output: String) {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = [scriptURL.path, sourceURL.path, targetURL.path]

        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        do {
            try process.run()
            process.waitUntilExit()
        } catch {
            return (1, error.localizedDescription)
        }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        let output = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return (process.terminationStatus, output.isEmpty ? "Install exited with code \(process.terminationStatus)." : output)
    }

    private static func runPrivilegedInstall(scriptURL: URL, sourceURL: URL, targetURL: URL) throws {
        let shellCommand = "/bin/sh \(shellSingleQuoted(scriptURL.path)) \(shellSingleQuoted(sourceURL.path)) \(shellSingleQuoted(targetURL.path))"
        let appleScriptSource = "do shell script \(appleScriptStringLiteral(shellCommand)) with administrator privileges"

        var errorInfo: NSDictionary?
        let result: NSAppleEventDescriptor? = DispatchQueue.main.syncIfNeeded {
            let script = NSAppleScript(source: appleScriptSource)
            return script?.executeAndReturnError(&errorInfo)
        }
        if result != nil {
            return
        }
        let errorNumber = errorInfo?[NSAppleScript.errorNumber] as? Int ?? -1
        if errorNumber == -128 {
            throw InstallerError.administratorAuthorizationCancelled
        }
        let message = errorInfo?[NSAppleScript.errorMessage] as? String
            ?? errorInfo?[NSAppleScript.errorBriefMessage] as? String
            ?? "Administrator installation failed (\(errorNumber))."
        throw InstallerError.installFailed(message)
    }

    static func scheduleVolumeEjectIfMounted(installerBundleURL: URL = Bundle.main.bundleURL) {
        guard let volumePath = mountedVolumePath(for: installerBundleURL) else { return }
        let pid = ProcessInfo.processInfo.processIdentifier
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = [
            "-c",
            "pid=\"$1\"; vol=\"$2\"; i=0; while kill -0 \"$pid\" 2>/dev/null && [ \"$i\" -lt 30 ]; do sleep 0.2; i=$((i+1)); done; sleep 0.3; /usr/bin/hdiutil detach \"$vol\" -quiet >/dev/null 2>&1 || true",
            "libzora-eject",
            String(pid),
            volumePath
        ]
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try? process.run()
    }
}

private extension DispatchQueue {
    func syncIfNeeded<T>(_ work: () -> T) -> T {
        if Thread.isMainThread {
            return work()
        }
        return sync(execute: work)
    }
}

// MARK: - AppKit UI

final class InstallerAppDelegate: NSObject, NSApplicationDelegate {
    private let options: InstallerOptions
    private let strings = InstallerStrings()
    private var window: NSWindow!
    private var iconView: NSImageView!
    private var titleLabel: NSTextField!
    private var statusLabel: NSTextField!
    private var progressIndicator: NSProgressIndicator!
    private var primaryButton: NSButton!
    private var secondaryButton: NSButton!
    private var tertiaryButton: NSButton!
    private var activeTargetURL: URL
    private var isInstalling = false
    private var installedTargetURL: URL?

    init(options: InstallerOptions) {
        self.options = options
        self.activeTargetURL = options.targetURL
        super.init()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        setupWindow()
        NSApp.activate(ignoringOtherApps: true)
        startInstall(to: activeTargetURL)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }

    private func setupWindow() {
        let width: CGFloat = 440
        let height: CGFloat = 248
        window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: width, height: height),
            styleMask: [.titled, .closable, .miniaturizable],
            backing: .buffered,
            defer: false
        )
        window.title = strings.windowTitle
        window.isReleasedWhenClosed = false
        window.center()

        let content = NSView(frame: NSRect(x: 0, y: 0, width: width, height: height))
        window.contentView = content

        iconView = NSImageView(frame: NSRect(x: (width - 68) / 2, y: 156, width: 68, height: 68))
        iconView.imageScaling = .scaleProportionallyUpOrDown
        iconView.image = NSApp.applicationIconImage
        content.addSubview(iconView)

        titleLabel = NSTextField(labelWithString: strings.headingInstalling)
        titleLabel.frame = NSRect(x: 24, y: 120, width: width - 48, height: 24)
        titleLabel.alignment = .center
        titleLabel.font = NSFont.systemFont(ofSize: 16, weight: .semibold)
        content.addSubview(titleLabel)

        statusLabel = NSTextField(wrappingLabelWithString: strings.stepChecking)
        statusLabel.frame = NSRect(x: 28, y: 74, width: width - 56, height: 40)
        statusLabel.alignment = .center
        statusLabel.font = NSFont.systemFont(ofSize: 12.5)
        statusLabel.textColor = .secondaryLabelColor
        content.addSubview(statusLabel)

        progressIndicator = NSProgressIndicator(frame: NSRect(x: 42, y: 54, width: width - 84, height: 14))
        progressIndicator.isIndeterminate = false
        progressIndicator.minValue = 0
        progressIndicator.maxValue = 1
        progressIndicator.doubleValue = 0.08
        content.addSubview(progressIndicator)

        primaryButton = NSButton(title: strings.retryButton, target: self, action: #selector(handlePrimaryButton))
        primaryButton.bezelStyle = .rounded
        primaryButton.keyEquivalent = "\r"
        primaryButton.isHidden = true
        content.addSubview(primaryButton)

        secondaryButton = NSButton(title: strings.userAppsButton, target: self, action: #selector(handleSecondaryButton))
        secondaryButton.bezelStyle = .rounded
        secondaryButton.isHidden = true
        content.addSubview(secondaryButton)

        tertiaryButton = NSButton(title: strings.closeButton, target: self, action: #selector(handleTertiaryButton))
        tertiaryButton.bezelStyle = .rounded
        tertiaryButton.isHidden = true
        content.addSubview(tertiaryButton)

        window.makeKeyAndOrderFront(nil)
    }

    private func startInstall(to targetURL: URL) {
        guard !isInstalling else { return }
        isInstalling = true
        activeTargetURL = targetURL
        installedTargetURL = nil

        titleLabel.stringValue = strings.headingInstalling
        statusLabel.stringValue = strings.stepChecking
        progressIndicator.isHidden = false
        progressIndicator.doubleValue = 0.08
        primaryButton.isHidden = true
        secondaryButton.isHidden = true
        tertiaryButton.isHidden = true

        let sourceURL = LibzoraInstallCore.resolveEmbeddedBundleURL()
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            do {
                try LibzoraInstallCore.performInstall(
                    sourceBundleURL: sourceURL,
                    targetBundleURL: targetURL,
                    allowPrivilegeEscalation: true
                ) { progress, message in
                    DispatchQueue.main.async {
                        self?.progressIndicator.doubleValue = progress
                        self?.statusLabel.stringValue = message
                    }
                }
                DispatchQueue.main.async {
                    self?.handleInstallSucceeded(targetURL: targetURL)
                }
            } catch {
                DispatchQueue.main.async {
                    self?.handleInstallFailed(error: error, targetURL: targetURL)
                }
            }
        }
    }

    private func handleInstallSucceeded(targetURL: URL) {
        isInstalling = false
        installedTargetURL = targetURL
        progressIndicator.doubleValue = 1.0
        titleLabel.stringValue = strings.headingSuccess
        statusLabel.stringValue = strings.detailInstalled(at: targetURL.path)

        guard options.launchAfterInstall else {
            layoutButtons(primary: strings.closeButton, secondary: nil, tertiary: nil)
            return
        }

        let config = NSWorkspace.OpenConfiguration()
        config.activates = true
        NSWorkspace.shared.openApplication(at: targetURL, configuration: config) { [weak self] _, error in
            DispatchQueue.main.async {
                guard let self else { return }
                if let error {
                    self.statusLabel.stringValue = "\(self.strings.detailInstalled(at: targetURL.path))\n\(error.localizedDescription)"
                    self.layoutButtons(primary: self.strings.openButton, secondary: nil, tertiary: self.strings.closeButton)
                    return
                }
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) {
                    if self.options.ejectVolumeAfterInstall {
                        LibzoraInstallCore.scheduleVolumeEjectIfMounted()
                    }
                    NSApp.terminate(nil)
                }
            }
        }
    }

    private func handleInstallFailed(error: Error, targetURL: URL) {
        isInstalling = false
        progressIndicator.isHidden = true
        titleLabel.stringValue = strings.headingFailed
        statusLabel.stringValue = error.localizedDescription

        let userApplications = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Applications", isDirectory: true)
            .appendingPathComponent("Libzora.app", isDirectory: true)
        let offerUserApplications = targetURL.path != userApplications.path
        layoutButtons(
            primary: strings.retryButton,
            secondary: offerUserApplications ? strings.userAppsButton : nil,
            tertiary: strings.closeButton
        )
    }

    private func layoutButtons(primary: String?, secondary: String?, tertiary: String?) {
        let buttons: [(NSButton, String?)] = [
            (tertiaryButton, tertiary),
            (secondaryButton, secondary),
            (primaryButton, primary),
        ]
        var rightEdge = window.contentView!.bounds.width - 24
        for (button, title) in buttons {
            if let title {
                button.title = title
                button.sizeToFit()
                let width = max(button.frame.width + 20, 88)
                button.frame = NSRect(x: rightEdge - width, y: 18, width: width, height: 30)
                button.isHidden = false
                rightEdge -= width + 10
            } else {
                button.isHidden = true
            }
        }
    }

    @objc private func handlePrimaryButton() {
        if let installedTargetURL {
            let config = NSWorkspace.OpenConfiguration()
            config.activates = true
            NSWorkspace.shared.openApplication(at: installedTargetURL, configuration: config) { _, _ in
                DispatchQueue.main.async {
                    if self.options.ejectVolumeAfterInstall {
                        LibzoraInstallCore.scheduleVolumeEjectIfMounted()
                    }
                    NSApp.terminate(nil)
                }
            }
            return
        }
        if primaryButton.title == strings.closeButton {
            NSApp.terminate(nil)
            return
        }
        startInstall(to: activeTargetURL)
    }

    @objc private func handleSecondaryButton() {
        let userTarget = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Applications", isDirectory: true)
            .appendingPathComponent("Libzora.app", isDirectory: true)
        startInstall(to: userTarget)
    }

    @objc private func handleTertiaryButton() {
        NSApp.terminate(nil)
    }
}

// MARK: - Entry Point

let options = InstallerOptions.parse(arguments: CommandLine.arguments)
if options.headless {
    let sourceURL = LibzoraInstallCore.resolveEmbeddedBundleURL()
    do {
        try LibzoraInstallCore.performInstall(
            sourceBundleURL: sourceURL,
            targetBundleURL: options.targetURL,
            allowPrivilegeEscalation: false
        )
        if options.launchAfterInstall {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/open")
            process.arguments = [options.targetURL.path]
            try? process.run()
        }
        if options.ejectVolumeAfterInstall {
            LibzoraInstallCore.scheduleVolumeEjectIfMounted()
        }
        exit(0)
    } catch {
        fputs("LibzoraInstaller error: \(error.localizedDescription)\n", stderr)
        exit(1)
    }
} else {
    let app = NSApplication.shared
    app.setActivationPolicy(.regular)
    let delegate = InstallerAppDelegate(options: options)
    app.delegate = delegate
    app.run()
}
