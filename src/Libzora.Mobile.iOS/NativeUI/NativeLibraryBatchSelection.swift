import SwiftUI

struct BatchDeleteReply: Decodable {
    let cancelled: Bool
    let deletedCount: Int
    let failedCount: Int
}

@MainActor
final class NativeLibrarySelectionState: ObservableObject {
    @Published var active = false
    @Published var selectedIds: Set<String> = []

    func enter(initialBookId: String? = nil) {
        active = true
        if let initialBookId, !initialBookId.isEmpty {
            selectedIds.insert(initialBookId)
        }
    }

    func exit() {
        active = false
        selectedIds.removeAll()
    }

    func toggle(_ bookId: String) {
        selectedIds = NativeLibraryBatchPolicy.toggle(bookId, in: selectedIds)
    }

    func toggleSeries(_ bookIds: [String]) {
        selectedIds = NativeLibraryBatchPolicy.toggleAll(visibleIds: bookIds, in: selectedIds)
    }

    func toggleAll(visibleIds: [String]) {
        selectedIds = NativeLibraryBatchPolicy.toggleAll(visibleIds: visibleIds, in: selectedIds)
    }

    func prune(validIds: Set<String>) {
        let next = NativeLibraryBatchPolicy.pruned(selectedIds, validIds: validIds)
        if next != selectedIds {
            selectedIds = next
        }
    }
}

struct NativeSelectionBadge: View {
    let selected: Bool

    var body: some View {
        ZStack {
            Circle()
                .fill(selected ? Color(uiColor: .systemBlue) : Color.black.opacity(0.28))
            Circle()
                .strokeBorder(.white.opacity(selected ? 0.95 : 0.85), lineWidth: 1.5)
            if selected {
                Image(systemName: "checkmark")
                    .font(.system(size: 11, weight: .bold))
                    .foregroundStyle(.white)
            }
        }
        .frame(width: 24, height: 24)
        .shadow(color: .black.opacity(0.18), radius: 3, y: 1)
        .accessibilityHidden(true)
    }
}

struct NativeSeriesSelectionBadge: View {
    let selectedCount: Int
    let totalCount: Int

    var body: some View {
        if selectedCount > 0 && selectedCount < totalCount {
            Text("\(selectedCount)/\(totalCount)")
                .font(NativeTheme.font(10.5, relativeTo: .caption2))
                .monospacedDigit()
                .foregroundStyle(.white)
                .padding(.horizontal, 7)
                .padding(.vertical, 4)
                .background(Color(uiColor: .systemBlue), in: Capsule())
                .overlay { Capsule().strokeBorder(.white.opacity(0.9), lineWidth: 1) }
                .shadow(color: .black.opacity(0.18), radius: 3, y: 1)
                .accessibilityHidden(true)
        } else {
            NativeSelectionBadge(selected: totalCount > 0 && selectedCount == totalCount)
        }
    }
}

struct NativeLibraryBatchBar: View {
    let selectedCount: Int
    let busy: Bool
    let onDelete: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            Text(L("已选 {0} 本", selectedCount))
                .font(NativeTheme.font(14))
                .foregroundStyle(NativeTheme.ink)
                .monospacedDigit()
            Spacer(minLength: 0)
            Button(role: .destructive, action: onDelete) {
                HStack(spacing: 6) {
                    if busy {
                        ProgressView().controlSize(.small)
                    } else {
                        Image(systemName: "trash")
                            .font(.system(size: 14, weight: .semibold))
                    }
                    Text(L("移到回收站"))
                        .font(NativeTheme.font(14))
                }
                .foregroundStyle(selectedCount == 0 || busy ? NativeTheme.muted : Color(uiColor: .systemRed))
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .disabled(selectedCount == 0 || busy)
            .accessibilityIdentifier("library.selection.delete")
        }
        .padding(.leading, 18)
        .padding(.trailing, 10)
        .padding(.vertical, 8)
        .modifier(NativeLibraryBatchBarSurface())
    }
}

private struct NativeLibraryBatchBarSurface: ViewModifier {
    func body(content: Content) -> some View {
        let shape = Capsule()
        if #available(iOS 26.0, *) {
            content.glassEffect(.regular.interactive(), in: shape)
                .shadow(color: .black.opacity(0.10), radius: 14, y: 6)
        } else {
            content.background(.regularMaterial, in: shape)
                .overlay { shape.strokeBorder(.primary.opacity(0.10), lineWidth: 0.5) }
                .shadow(color: .black.opacity(0.14), radius: 16, y: 6)
        }
    }
}

struct NativeSeriesVolumeTile: View {
    let book: LibraryBook
    let coverWidth: CGFloat
    @ObservedObject var store: NativeStore
    @ObservedObject var selection: NativeLibrarySelectionState
    let lifted: Bool
    let open: () -> Void
    let preview: (CGRect) -> Void

    var body: some View {
        let volumeKey = "seriesVolume:\(book.id)"
        let isSelected = selection.active && selection.selectedIds.contains(book.id)
        Button {
            if selection.active {
                selection.toggle(book.id)
            } else {
                open()
            }
        } label: {
            NativeBookTile(book: book, width: coverWidth,
                           showRating: store.preferences.showBookRating,
                           showSyncStatus: store.preferences.showBookSyncStatus,
                           coverFrameKey: volumeKey,
                           hideCover: store.isBookCoverHidden(forKey: volumeKey))
                .frame(width: coverWidth)
                .opacity(lifted ? 0 : 1)
                .overlay(alignment: .topTrailing) {
                    if selection.active {
                        NativeSelectionBadge(selected: isSelected).padding(6)
                    }
                }
        }
        .buttonStyle(.plain)
        .allowsHitTesting(!store.openingReader)
        .overlay {
            if !selection.active {
                NativeBookLink(enabled: !store.openingReader, tap: open, longPress: preview)
            }
        }
        .accessibilityValue(selection.active ? (isSelected ? L("已选中") : L("未选中")) : "")
        .accessibilityHint(selection.active ? L("轻点选中或取消") : L("轻点阅读，长按查看更多操作"))
        .accessibilityIdentifier("books.series.volume.\(book.id)")
    }
}
