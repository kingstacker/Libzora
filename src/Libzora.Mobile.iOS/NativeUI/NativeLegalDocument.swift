import SwiftUI

private struct NativeLegalDocument: Decodable {
    let title: String
    let effectiveDate: String
    let summary: String
    let blocks: [Block]

    struct Block: Decodable {
        let text: String
        let isHeading: Bool
    }
}

struct NativeLegalDocumentPage: View {
    enum Kind: String {
        case terms
        case privacy

        var title: String { self == .terms ? L("用户协议") : L("隐私政策") }
        var error: String { self == .terms ? L("无法打开用户协议") : L("无法打开隐私政策") }
    }

    @ObservedObject var store: NativeStore
    let kind: Kind
    @State private var document: NativeLegalDocument?
    @State private var failed = false

    var body: some View {
        ScrollView {
            if let document {
                VStack(alignment: .leading, spacing: 12) {
                    Text(document.effectiveDate)
                        .font(NativeTheme.font(12, relativeTo: .caption))
                        .foregroundStyle(NativeTheme.secondary)
                    Text(document.summary)
                        .font(NativeTheme.font(14, relativeTo: .body))
                        .padding(14)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(Color(uiColor: .secondarySystemGroupedBackground))
                        .clipShape(RoundedRectangle(cornerRadius: 12))
                    ForEach(document.blocks.indices, id: \.self) { index in
                        let block = document.blocks[index]
                        if block.isHeading {
                            Text(block.text)
                                .font(NativeTheme.font(16, relativeTo: .headline).weight(.semibold))
                                .accessibilityAddTraits(.isHeader)
                                .padding(.top, 8)
                        } else {
                            Text(block.text)
                                .font(NativeTheme.font(14, relativeTo: .body))
                                .lineSpacing(4)
                        }
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(16)
                .textSelection(.enabled)
            } else if failed {
                Text(kind.error)
                    .frame(maxWidth: .infinity)
                    .padding(24)
            } else {
                ProgressView()
                    .frame(maxWidth: .infinity, minHeight: 240)
            }
        }
        .background(NativeTheme.page)
        .navigationTitle(document?.title ?? kind.title)
        .navigationBarTitleDisplayMode(.inline)
        .navigationBarHidden(false)
        .nativeBackButton()
        .task(id: store.uiLanguage) {
            failed = false
            do {
                let loaded: NativeLegalDocument = try await store.request("settings.legal.get", ["kind": kind.rawValue])
                guard !Task.isCancelled else { return }
                document = loaded
            } catch {
                guard !Task.isCancelled else { return }
                document = nil
                failed = true
            }
        }
    }
}
