import SwiftUI

struct NativeTrash: View {
    @ObservedObject var store: NativeStore
    @State private var items: [TrashRecord] = []
    @State private var loading = true
    @State private var busy = false
    @State private var openedItemId: String?

    var body: some View {
        GeometryReader { proxy in
            ScrollView {
                LazyVStack(spacing: 10) {
                    if loading { ProgressView().padding(24) }
                    if !loading && items.isEmpty {
                        Text(L("回收站是空的")).font(NativeTheme.font(15))
                            .foregroundStyle(NativeTheme.secondary)
                            .frame(maxWidth: .infinity).padding(.vertical, 36)
                    }
                    ForEach(items) { item in
                        NativeSwipeDeleteCard(itemId: item.id, openedEntryId: $openedItemId, disabled: busy,
                            cardIdentifier: "trash.item.\(item.id)", deleteIdentifier: "trash.delete.\(item.id)",
                            cornerRadius: 14, onOpen: {},
                            onDelete: { await performCommand("trash.purge", ["id": item.id]) }) {
                            VStack(alignment: .leading, spacing: 5) {
                                Text(item.title).font(NativeTheme.font(16, relativeTo: .headline)).lineLimit(2)
                                Text(item.detail).font(NativeTheme.font(12, relativeTo: .caption))
                                    .foregroundStyle(NativeTheme.secondary)
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.trailing, 80).padding(16)
                            .background(NativeTheme.surface.opacity(store.preferences.colorfulMode ? 0.85 : 1),
                                in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                        }
                        // This sibling overlay is above, not inside, the card's UIKit touch layer.
                        .overlay(alignment: .trailing) {
                            let isOpened = openedItemId == item.id
                            Button(L("恢复")) {
                                openedItemId = nil
                                store.perform { await performCommand("trash.restore", ["id": item.id]) }
                            }
                            .font(NativeTheme.font(13.5, relativeTo: .subheadline).weight(.medium))
                            .foregroundStyle(NativeTheme.ink).padding(.horizontal, 12).padding(.vertical, 7)
                            .background(NativeTheme.sunk, in: Capsule()).buttonStyle(.plain)
                            .padding(.trailing, 16)
                            .opacity(isOpened ? 0 : 1)
                            .animation(isOpened ? .easeOut(duration: 0.08) : .easeOut(duration: 0.15).delay(0.20),
                                value: isOpened)
                            .allowsHitTesting(!isOpened && !busy)
                            .accessibilityHidden(isOpened)
                            .disabled(busy).accessibilityIdentifier("trash.restore.\(item.id)")
                        }
                    }
                }
                .padding(16)
                .frame(maxWidth: .infinity, minHeight: proxy.size.height, alignment: .top)
                .background {
                    if openedItemId != nil {
                        Color.clear
                            .contentShape(Rectangle())
                            .onTapGesture { openedItemId = nil }
                            .accessibilityHidden(true)
                    }
                }
            }
            .refreshable { openedItemId = nil; await load() }
        }
        .font(NativeTheme.font()).modifier(NativeGroupedListBackground()).disabled(busy)
        .navigationTitle(L("回收站")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
        .nativeBackButton()
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button(L("清空"), role: .destructive) {
                    openedItemId = nil
                    store.perform { await performCommand("trash.empty", [:]) }
                }.disabled(busy || items.isEmpty).accessibilityLabel(L("清空回收站"))
                    .accessibilityIdentifier("trash.empty")
            }
        }
        .task { await load() }
    }
    private func load() async {
        defer { loading = false }
        do { items = try await store.request("trash.get") }
        catch { if !Task.isCancelled { store.failure = error.localizedDescription } }
    }
    @MainActor private func performCommand(_ command: String, _ args: [String: Any]) async {
        guard !busy else { return }
        busy = true
        defer { busy = false }
        do {
            try await store.command(command, args)
            items = try await store.request("trash.get")
            try await store.refreshLibrary()
        } catch { if !Task.isCancelled { store.failure = error.localizedDescription } }
    }
}
