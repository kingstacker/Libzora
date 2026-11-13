import PhotosUI
import SwiftUI
import UIKit

/// The receipt returned by the managed feedback service.
struct NativeFeedbackReceipt: Decodable {
    let id: String
    let number: Int64
    let status: String
    let createdAt: String
}

struct NativeFeedbackScreenshot: Identifiable {
    let id = UUID()
    let data: Data
    let image: UIImage
}

/// Mirrors the desktop feedback window: reported type, description, optional
/// contact and up to three screenshots, all submitted through the signed-in
/// account. Screenshots are bounded and re-encoded as opaque JPEG before they
/// cross the native bridge, so no original photo data leaves the device.
struct NativeFeedbackPage: View {
    @ObservedObject var store: NativeStore
    @Environment(\.dismiss) private var dismiss
    @State private var account: AccountStatus?
    @State private var loading = true
    @State private var type = "bug"
    @State private var content = ""
    @State private var contact = ""
    @State private var screenshots: [NativeFeedbackScreenshot] = []
    @State private var attachLogs = true
    @State private var pickerOpen = false
    @State private var busy = false
    @State private var status = ""
    @State private var receipt: NativeFeedbackReceipt?
    // One idempotency key per page: retrying a failed submission must not
    // create a second report if the first request reached the server.
    @State private var requestId = UUID().uuidString
    @State private var actionTask: Task<Void, Never>?

    private static let maximumScreenshotBytes = 4 * 1024 * 1024
    private static let maximumTotalScreenshotBytes = 10 * 1024 * 1024
    private static let maximumContentLength = 4000
    private static let types = [
        FeedbackType(value: "bug", name: "问题 Bug"),
        FeedbackType(value: "feature", name: "功能建议"),
        FeedbackType(value: "sync", name: "同步问题"),
        FeedbackType(value: "other", name: "其他问题")
    ]

    private var signedIn: Bool { account?.isLoggedIn == true }
    private var trimmedContent: String { content.trimmingCharacters(in: .whitespacesAndNewlines) }

    var body: some View {
        NativeForm {
            if let receipt {
                successSection(receipt)
            } else if loading {
                Section { ProgressView().frame(maxWidth: .infinity) }
            } else if !signedIn {
                loginSection
            } else {
                introductionSection
                formSection
                if !status.isEmpty {
                    Section {
                        Text(status).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary)
                            .accessibilityIdentifier("feedback.status")
                    }
                }
                submitSection
            }
        }
        .navigationTitle("问题反馈").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
        .nativeBackButton()
        .modifier(NativeKeyboardDone())
        .modifier(NativeHideTabBar())
        .onAppear { Task { await loadAccount() } }
        .onDisappear { actionTask?.cancel() }
        .sheet(isPresented: $pickerOpen) {
            NativePhotoPicker(selectionLimit: max(1, 3 - screenshots.count)) { images in
                add(images)
            }.ignoresSafeArea()
        }
    }

    @ViewBuilder private var loginSection: some View {
        Section {
            Text("登录后即可提交问题反馈。").font(NativeTheme.font())
                .accessibilityIdentifier("feedback.loginRequired")
            Text("反馈会关联到当前账号，方便我们跟进处理。")
                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
        }
        Section {
            NavigationLink { NativeAccountPage(store: store) } label: {
                NativeSettingsRow(title: "前往登录", chevron: false)
            }.accessibilityIdentifier("feedback.openAccount")
        }
    }

    @ViewBuilder private var introductionSection: some View {
        Section {
            Text("遇到问题或有功能建议？告诉我们，我们会尽快处理。")
                .font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary)
            Text("提交时会附带账号、应用版本、系统版本与设备型号；勾选“自动附加软件日志”时会一并上传已脱敏的软件日志。不包含书籍、笔记或阅读记录。")
                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
            if let name = account?.user?.username {
                Text("将以 \(name) 提交反馈。")
                    .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                    .accessibilityIdentifier("feedback.account")
            }
        }
    }

    @ViewBuilder private var formSection: some View {
        Section("反馈内容") {
            Picker("反馈类型", selection: $type) {
                ForEach(Self.types) { Text($0.name).tag($0.value) }
            }.pickerStyle(.menu).accessibilityIdentifier("feedback.type")
            VStack(alignment: .leading, spacing: 6) {
                Text("问题描述").font(NativeTheme.font(13, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                ZStack(alignment: .topLeading) {
                    if content.isEmpty {
                        Text("请尽量描述复现步骤、期望结果和实际结果（至少 5 个字）")
                            .font(NativeTheme.font()).foregroundStyle(NativeTheme.faint)
                            .padding(.horizontal, 5).padding(.vertical, 8).allowsHitTesting(false)
                    }
                    TextEditor(text: $content)
                        .font(NativeTheme.font())
                        .frame(minHeight: 140)
                        .scrollContentBackgroundHidden()
                        .background(Color.clear)
                        .accessibilityIdentifier("feedback.content")
                        .onChange(of: content) { value in
                            if value.count > Self.maximumContentLength {
                                content = String(value.prefix(Self.maximumContentLength))
                            }
                        }
                }.padding(8).background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 12))
                Text(verbatim: "\(content.count)/\(Self.maximumContentLength)")
                    .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                    .frame(maxWidth: .infinity, alignment: .trailing)
            }
            NativeTextField(title: "联系方式（选填）", text: $contact, identifier: "feedback.contact")
                .textInputAutocapitalization(.never).disableAutocorrection(true)
            Text("填写 QQ 号或邮箱，方便我们联系你。")
                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
        }
        Section("截图（选填）") {
            screenshotRow
            Text("最多 3 张，提交前会压缩为 JPEG，每张不超过 4 MB。")
                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
        }
        Section("软件日志") {
            Toggle("自动附加软件日志", isOn: $attachLogs)
                .accessibilityIdentifier("feedback.attachLogs")
            Text("仅包含应用版本、错误类型和必要技术事件；账号、邮箱、书籍、笔记、阅读内容、文件路径与密钥都会脱敏。")
                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
        }
    }

    @ViewBuilder private var screenshotRow: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 10) {
                ForEach(Array(screenshots.enumerated()), id: \.element.id) { index, screenshot in
                    ZStack(alignment: .topTrailing) {
                        Image(uiImage: screenshot.image).resizable().scaledToFill()
                            .frame(width: 96, height: 72)
                            .clipShape(RoundedRectangle(cornerRadius: 8))
                        Button {
                            screenshots.removeAll { $0.id == screenshot.id }
                        } label: {
                            Image(systemName: "xmark.circle.fill")
                                .font(.system(size: 18))
                                .symbolRenderingMode(.palette)
                                .foregroundStyle(.white, .black.opacity(0.65))
                        }.buttonStyle(.plain)
                            .offset(x: 6, y: -6)
                            .accessibilityLabel("移除截图")
                            .accessibilityIdentifier("feedback.removeScreenshot.\(index)")
                    }
                }
                if screenshots.count < 3 {
                    Button { pickerOpen = true } label: {
                        VStack(spacing: 5) {
                            Image(systemName: "plus").font(.system(size: 19))
                            Text("添加截图").font(NativeTheme.font(12, relativeTo: .caption))
                        }.foregroundStyle(NativeTheme.secondary)
                            .frame(width: 96, height: 72)
                            .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 8))
                    }.buttonStyle(.plain).accessibilityIdentifier("feedback.addScreenshot")
                }
            }.padding(.vertical, 4)
        }
    }

    @ViewBuilder private var submitSection: some View {
        Section {
            Button { submit() } label: { Text("提交反馈") }
                .buttonStyle(NativePrimaryButtonStyle())
                .disabled(busy || trimmedContent.count < 5)
                .accessibilityIdentifier("feedback.submit")
            if busy { ProgressView().frame(maxWidth: .infinity) }
        }
    }

    @ViewBuilder private func successSection(_ receipt: NativeFeedbackReceipt) -> some View {
        Section {
            Text("反馈已提交").font(NativeTheme.font(20, relativeTo: .title3))
                .accessibilityIdentifier("feedback.success")
            Text("反馈编号：#\(receipt.number)").font(NativeTheme.font())
                .accessibilityIdentifier("feedback.number")
            Text("感谢你的反馈，我们会尽快处理。")
                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
        }
        Section {
            Button("完成") { dismiss() }.buttonStyle(NativePrimaryButtonStyle())
        }
    }

    private func loadAccount() async {
        do {
            let value: AccountStatus = try await store.request("settings.account.get")
            guard !Task.isCancelled else { return }
            account = value
        } catch {
            if !Task.isCancelled { status = error.localizedDescription }
        }
        loading = false
    }

    private func submit() {
        guard signedIn, !busy else { return }
        let text = trimmedContent
        guard text.count >= 5 else { status = "问题描述至少需要 5 个字。"; return }
        busy = true; status = "正在提交反馈…"
        let payload = screenshots.map { $0.data.base64EncodedString() }
        actionTask = Task {
            defer { busy = false }
            do {
                let value: NativeFeedbackReceipt = try await store.request("settings.feedback.submit", [
                    "requestId": requestId,
                    "type": type,
                    "content": content,
                    "contact": contact,
                    "platformVersion": UIDevice.current.systemVersion,
                    "deviceModel": Self.deviceModel,
                    "attachLogs": attachLogs,
                    "images": payload
                ])
                guard !Task.isCancelled else { return }
                receipt = value; status = ""
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }

    private func add(_ images: [UIImage]) {
        var unreadable = 0, tooLarge = 0, overTotal = 0, skipped = 0
        for image in images {
            guard screenshots.count < 3 else { skipped += 1; continue }
            guard let data = Self.compressedScreenshot(image) else { unreadable += 1; continue }
            if data.count > Self.maximumScreenshotBytes { tooLarge += 1; continue }
            let total = screenshots.reduce(0) { $0 + $1.data.count } + data.count
            if total > Self.maximumTotalScreenshotBytes { overTotal += 1; continue }
            screenshots.append(NativeFeedbackScreenshot(data: data, image: image))
        }
        if unreadable > 0 { status = "有 \(unreadable) 张截图无法读取，请换一张图片。" }
        else if tooLarge > 0 { status = "有 \(tooLarge) 张截图处理后仍超过 4 MB，请换一张。" }
        else if overTotal > 0 { status = "截图总大小不能超过 10 MB。" }
        else if skipped > 0 { status = "最多只能添加 3 张截图。" }
        else { status = "" }
    }

    /// Same bounds as the desktop window: longest edge 1600 px, white
    /// background, JPEG quality 82.
    private static func compressedScreenshot(_ image: UIImage) -> Data? {
        let pixels = CGSize(width: image.size.width * image.scale, height: image.size.height * image.scale)
        guard pixels.width > 0, pixels.height > 0 else { return nil }
        let factor = min(1, 1600 / max(pixels.width, pixels.height))
        let output = CGSize(width: max(1, (pixels.width * factor).rounded()),
                            height: max(1, (pixels.height * factor).rounded()))
        let format = UIGraphicsImageRendererFormat.default()
        format.scale = 1
        format.opaque = true
        let rendered = UIGraphicsImageRenderer(size: output, format: format).image { context in
            UIColor.white.setFill()
            context.fill(CGRect(origin: .zero, size: output))
            image.draw(in: CGRect(origin: .zero, size: output))
        }
        return rendered.jpegData(compressionQuality: 0.82)
    }

    private static var deviceModel: String {
        var info = utsname()
        uname(&info)
        let identifier = Mirror(reflecting: info.machine).children.reduce(into: "") { value, element in
            guard let byte = element.value as? Int8, byte != 0 else { return }
            value.append(Character(UnicodeScalar(UInt8(byte))))
        }
        return String((identifier.isEmpty ? UIDevice.current.model : identifier).prefix(128))
    }
}

private struct FeedbackType: Identifiable {
    let value: String
    let name: String
    var id: String { value }
}

/// System photo picker; no photo-library permission prompt is required.
struct NativePhotoPicker: UIViewControllerRepresentable {
    let selectionLimit: Int
    let onPicked: ([UIImage]) -> Void
    @Environment(\.dismiss) private var dismiss

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeUIViewController(context: Context) -> PHPickerViewController {
        var configuration = PHPickerConfiguration(photoLibrary: .shared())
        configuration.filter = .images
        configuration.selectionLimit = max(1, selectionLimit)
        let controller = PHPickerViewController(configuration: configuration)
        controller.delegate = context.coordinator
        return controller
    }

    func updateUIViewController(_ uiViewController: PHPickerViewController, context: Context) {}

    final class Coordinator: NSObject, PHPickerViewControllerDelegate {
        private let parent: NativePhotoPicker
        init(_ parent: NativePhotoPicker) { self.parent = parent }

        func picker(_ picker: PHPickerViewController, didFinishPicking results: [PHPickerResult]) {
            parent.dismiss()
            guard !results.isEmpty else { return }
            let group = DispatchGroup()
            let lock = NSLock()
            var images = [UIImage?](repeating: nil, count: results.count)
            for (index, result) in results.enumerated() {
                guard result.itemProvider.canLoadObject(ofClass: UIImage.self) else { continue }
                group.enter()
                result.itemProvider.loadObject(ofClass: UIImage.self) { object, _ in
                    lock.lock()
                    images[index] = object as? UIImage
                    lock.unlock()
                    group.leave()
                }
            }
            group.notify(queue: .main) { [parent] in
                parent.onPicked(images.compactMap { $0 })
            }
        }
    }
}

/// iOS 15's TextEditor paints the system list background; iOS 16 can clear it.
private extension View {
    @ViewBuilder func scrollContentBackgroundHidden() -> some View {
        if #available(iOS 16.0, *) { scrollContentBackground(.hidden) } else { self }
    }
}
