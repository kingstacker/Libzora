import SwiftUI
import UIKit

private struct NativeUiFontEnvironmentKey: EnvironmentKey {
    static let defaultValue: Int = 0
}

extension EnvironmentValues {
    /// Propagates the active interface font choice (`0` = KingHwaOldSong, `1` = system)
    /// so stateless SwiftUI views re-evaluate `NativeTheme.font(...)` immediately.
    var nativeUiFont: Int {
        get { self[NativeUiFontEnvironmentKey.self] }
        set { self[NativeUiFontEnvironmentKey.self] = newValue }
    }
}

/// The same type scale, ink and spacing as MobileTheme.axaml. Native controls
/// own their interaction; their default visual style isn't the app's design.
enum NativeTheme {
    private(set) static var useSystemFont = false

    static func font(_ size: CGFloat = 15, relativeTo style: Font.TextStyle = .body) -> Font {
        font(size, relativeTo: style, useSystem: useSystemFont)
    }

    static func font(_ size: CGFloat = 15, relativeTo style: Font.TextStyle = .body, useSystem: Bool) -> Font {
        if useSystem {
            let scaled = UIFontMetrics(forTextStyle: uiTextStyle(style)).scaledValue(for: size)
            return .system(size: scaled)
        }
        return .custom("KingHwaOldSong", size: size, relativeTo: style)
    }

    private static func uiTextStyle(_ style: Font.TextStyle) -> UIFont.TextStyle {
        switch style {
        case .largeTitle: return .largeTitle
        case .title: return .title1
        case .title2: return .title2
        case .title3: return .title3
        case .headline: return .headline
        case .subheadline: return .subheadline
        case .callout: return .callout
        case .footnote: return .footnote
        case .caption: return .caption1
        case .caption2: return .caption2
        default: return .body
        }
    }

    static func uiKitFont(_ size: CGFloat, useSystem: Bool = useSystemFont) -> UIFont {
        if useSystem { return .systemFont(ofSize: size) }
        return UIFont(name: "KingHwaOldSong", size: size) ?? .systemFont(ofSize: size)
    }

    /// The top-level tab pages use one title size so their headers align.
    static var pageTitleFont: Font { font(26, relativeTo: .largeTitle) }

    /// The window's real top inset. SwiftUI geometry inside a page can report
    /// zero here, while the status-bar glass needs the device value.
    private static var activeWindow: UIWindow? {
        let windows = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.flatMap(\.windows)
        return windows.first { $0.isKeyWindow } ?? windows.first { !$0.isHidden }
    }

    static var statusBarHeight: CGFloat { activeWindow?.safeAreaInsets.top ?? 0 }

    /// Use the actual tab bar, including its safe-area gap. iPadOS moves it to
    /// the top in a wide window and back to the bottom in compact Split View.
    static var bottomTabBarInset: CGFloat {
        guard let window = activeWindow, let root = window.rootViewController else { return 0 }
        func tabBar(in controller: UIViewController) -> UITabBar? {
            if let tabs = controller as? UITabBarController { return tabs.tabBar }
            for child in controller.children {
                if let bar = tabBar(in: child) { return bar }
            }
            return nil
        }
        guard let bar = tabBar(in: root), !bar.isHidden, bar.window === window else { return 0 }
        let frame = bar.convert(bar.bounds, to: window)
        return frame.minY > window.bounds.midY ? max(0, window.bounds.maxY - frame.minY) : 0
    }

    private static func color(_ light: UInt32, _ dark: UInt32) -> Color {
        Color(uiColor: UIColor { traits in
            let value = traits.userInterfaceStyle == .dark ? dark : light
            return UIColor(red: CGFloat((value >> 16) & 0xff) / 255,
                           green: CGFloat((value >> 8) & 0xff) / 255,
                           blue: CGFloat(value & 0xff) / 255, alpha: 1)
        })
    }

    static let page = color(0xffffff, 0x121212)
    static let surface = color(0xffffff, 0x1e1e1e)
    static let sunk = color(0xf5f5f5, 0x2a2a2a)
    static let strong = color(0xe6e6e6, 0x3a3a3a)
    static let ink = color(0x1c1c1c, 0xededed)
    static let systemSwitchTint = Color(uiColor: .systemGreen)
    static let secondary = color(0x5e5e5e, 0xb0b0b0)
    static let muted = color(0x767676, 0x8c8c8c)
    // Text drawn directly on the colourful wash: the pastel background lifts
    // luminance, so the neutral greys lose contrast and need a deeper tone.
    static let secondaryOnColorful = color(0x464646, 0xc8c8c8)
    static let mutedOnColorful = color(0x565656, 0xb4b4b4)
    static let faint = color(0x9a9a9a, 0x6e6e6e)
    static let divider = color(0xefefef, 0x262626)
    // Incoming chat bubbles sit on the white assistant page, so they need a
    // slightly deeper grey than the generic sunk surface to read as bubbles.
    static let bubble = color(0xebecef, 0x2c2c2e)
    // Plan and notice accents shared with the desktop account center.
    static let planFree = color(0x667085, 0xb0b8c6)
    static let planLifetime = color(0x7955b4, 0xbaa0ea)
    static let planTrial = color(0x98651d, 0xe6bc75)
    static let success = color(0x288064, 0x78c8ac)
    static let warning = color(0xa76b19, 0xe6bc75)

    static func planAccent(_ plan: String) -> Color {
        plan == "trial" ? planTrial : plan == "lifetime" ? planLifetime : planFree
    }

    @MainActor static func setUseSystemFont(_ useSystem: Bool) {
        useSystemFont = useSystem
        configureControls()
        refreshMountedControls()
    }

    @MainActor static func configureControls() {
        if #unavailable(iOS 16.0) {
            // iOS 15 lists use UITableView and cannot hide their scroll background in SwiftUI.
            UITableView.appearance().backgroundColor = .clear
        }
        let navigation = makeNavigationBarAppearance()
        UINavigationBar.appearance().standardAppearance = navigation
        UINavigationBar.appearance().scrollEdgeAppearance = navigation
        UINavigationBar.appearance().compactAppearance = navigation
        UIBarButtonItem.appearance().setTitleTextAttributes([.font: uiKitFont(15)], for: .normal)
        UIBarButtonItem.appearance().setTitleTextAttributes([.font: uiKitFont(15)], for: .disabled)
        // SwiftUI's segmented Picker is a UISegmentedControl; only the title
        // font is replaced so the system track, selection and glass stay intact.
        for state: UIControl.State in [.normal, .selected, .highlighted, .disabled] {
            UISegmentedControl.appearance().setTitleTextAttributes([.font: uiKitFont(13)], for: state)
        }
        UITabBarItem.appearance().setTitleTextAttributes([.font: uiKitFont(10)], for: .normal)
        UITabBarItem.appearance().setTitleTextAttributes([.font: uiKitFont(10)], for: .selected)
        UITabBar.appearance().tintColor = .label
        UITabBar.appearance().unselectedItemTintColor = .secondaryLabel
    }

    @MainActor private static func makeNavigationBarAppearance() -> UINavigationBarAppearance {
        let navigation = UINavigationBarAppearance()
        navigation.configureWithTransparentBackground()
        navigation.titleTextAttributes = [.font: uiKitFont(17), .foregroundColor: UIColor.label]
        navigation.largeTitleTextAttributes = [.font: uiKitFont(26), .foregroundColor: UIColor.label]
        // Back and toolbar titles are drawn by UINavigationBar, not by the
        // SwiftUI font environment, so give both button appearances the font.
        for appearance in [navigation.buttonAppearance, navigation.backButtonAppearance,
                           navigation.doneButtonAppearance] {
            appearance.normal.titleTextAttributes = [.font: uiKitFont(15)]
            appearance.highlighted.titleTextAttributes = [.font: uiKitFont(15)]
            appearance.disabled.titleTextAttributes = [.font: uiKitFont(15)]
        }
        return navigation
    }

    /// UIAppearance proxies only apply to newly created views; walk the live
    /// windows so mounted navigation bars, tab bars and segmented controls
    /// reflect the new interface font immediately without restarting.
    @MainActor private static func refreshMountedControls() {
        let navigationAppearance = makeNavigationBarAppearance()
        let tabFontAttrs: [NSAttributedString.Key: Any] = [.font: uiKitFont(10)]
        let segmentFontAttrs: [NSAttributedString.Key: Any] = [.font: uiKitFont(13)]
        let barButtonFontAttrs: [NSAttributedString.Key: Any] = [.font: uiKitFont(15)]

        func updateController(_ controller: UIViewController) {
            if let nav = controller as? UINavigationController {
                nav.navigationBar.standardAppearance = navigationAppearance
                nav.navigationBar.scrollEdgeAppearance = navigationAppearance
                nav.navigationBar.compactAppearance = navigationAppearance
                for item in nav.navigationBar.items ?? [] {
                    for barButton in (item.leftBarButtonItems ?? []) + (item.rightBarButtonItems ?? []) {
                        barButton.setTitleTextAttributes(barButtonFontAttrs, for: .normal)
                        barButton.setTitleTextAttributes(barButtonFontAttrs, for: .disabled)
                    }
                }
                nav.navigationBar.setNeedsLayout()
            }
            if let tabs = controller as? UITabBarController {
                for item in tabs.tabBar.items ?? [] {
                    item.setTitleTextAttributes(tabFontAttrs, for: .normal)
                    item.setTitleTextAttributes(tabFontAttrs, for: .selected)
                }
                tabs.tabBar.setNeedsLayout()
            }
            for child in controller.children { updateController(child) }
            if let presented = controller.presentedViewController { updateController(presented) }
        }

        func updateView(_ view: UIView) {
            if let segmented = view as? UISegmentedControl {
                for state: UIControl.State in [.normal, .selected, .highlighted, .disabled] {
                    segmented.setTitleTextAttributes(segmentFontAttrs, for: state)
                }
                segmented.setNeedsLayout()
            }
            for subview in view.subviews { updateView(subview) }
        }

        let windows = UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .flatMap(\.windows)
        for window in windows {
            if let root = window.rootViewController { updateController(root) }
            updateView(window)
        }
    }
}

/// Shared title geometry for every top-level page, including the shelf.
struct NativePageTitle: View {
    @Environment(\.nativeUiFont) private var uiFont
    let title: String
    let identifier: String

    var body: some View {
        let _ = uiFont
        Text(title).font(NativeTheme.pageTitleFont)
            .lineLimit(1).frame(minHeight: NativeAdaptiveLayout.mastheadHeight)
            .accessibilityIdentifier(identifier)
    }
}

/// Keep page headers on the same window edges, independent of their content.
struct NativePageMasthead: ViewModifier {
    func body(content: Content) -> some View {
        content.frame(minHeight: NativeAdaptiveLayout.mastheadHeight)
            .padding(.leading, NativeAdaptiveLayout.pageInset).padding(.trailing, 12)
            .padding(.top, NativeAdaptiveLayout.mastheadTopInset).padding(.bottom, 4)
    }
}

/// Protects status text while letting the material fade into scrolling content.
struct NativeStatusBarGlass: View {
    @State private var topInset: CGFloat = 0

    var body: some View {
        Rectangle().fill(.ultraThinMaterial)
            .frame(height: max(topInset, NativeTheme.statusBarHeight) + 28)
            .mask {
                LinearGradient(stops: [
                    .init(color: .black, location: 0),
                    .init(color: .black, location: 0.65),
                    .init(color: .clear, location: 1)
                ], startPoint: .top, endPoint: .bottom)
            }
            .opacity(0.35)
            .frame(maxWidth: .infinity, alignment: .top)
            .ignoresSafeArea(edges: .top)
            .background {
                GeometryReader { proxy in
                    Color.clear
                        .onAppear { topInset = proxy.safeAreaInsets.top }
                        .onChange(of: proxy.safeAreaInsets.top) { topInset = $0 }
                }
            }
            .allowsHitTesting(false)
            .accessibilityHidden(true)
    }
}

struct NativePrimaryButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled
    @Environment(\.nativeUiFont) private var uiFont
    func makeBody(configuration: Configuration) -> some View {
        let _ = uiFont
        return configuration.label
            .font(NativeTheme.font()).foregroundStyle(enabled ? NativeTheme.ink : NativeTheme.muted)
            .frame(maxWidth: .infinity).padding(.horizontal, 16).padding(.vertical, 14)
            .background(configuration.isPressed ? NativeTheme.strong : NativeTheme.sunk,
                        in: RoundedRectangle(cornerRadius: 12))
    }
}

/// A primary action that floats over the ambient page instead of sitting on a
/// card. iOS 26 draws it with the system's Liquid Glass material so it belongs
/// with the surrounding chrome; older systems keep the solid pill. Compact
/// sizing suits inline actions such as the cover picker.
struct NativeLiquidGlassButtonStyle: ButtonStyle {
    var compact = false
    @Environment(\.isEnabled) private var enabled
    @Environment(\.nativeUiFont) private var uiFont
    @ViewBuilder func makeBody(configuration: Configuration) -> some View {
        let _ = uiFont
        let label = configuration.label
            .font(NativeTheme.font(compact ? 13 : 15, relativeTo: compact ? .footnote : .body))
            .foregroundStyle(enabled ? NativeTheme.ink : NativeTheme.muted)
            .frame(maxWidth: compact ? nil : .infinity)
            .padding(.horizontal, compact ? 14 : 16).padding(.vertical, compact ? 8 : 14)
        if #available(iOS 26.0, *) {
            label.glassEffect(.regular.interactive(), in: Capsule())
        } else {
            label.background(configuration.isPressed ? NativeTheme.strong : NativeTheme.sunk,
                             in: RoundedRectangle(cornerRadius: compact ? 10 : 12))
        }
    }
}

struct NativeSettingsRow: View {
    @Environment(\.nativeUiFont) private var uiFont
    let title: String
    var value = ""
    var chevron = true
    var detailColor = NativeTheme.muted
    var verticalPadding: CGFloat = 13
    var body: some View {
        let _ = uiFont
        HStack(spacing: 8) {
            Text(title).font(NativeTheme.font())
            Spacer(minLength: 12)
            if !value.isEmpty { Text(value).font(NativeTheme.font(14)).foregroundStyle(detailColor).lineLimit(1) }
            if chevron { Image(systemName: "chevron.right").font(.system(size: 12, weight: .medium)).foregroundStyle(detailColor.opacity(0.85)) }
        }.frame(minHeight: 22).padding(.vertical, verticalPadding).contentShape(Rectangle())
    }
}

struct NativeSearchField: View {
    @Environment(\.nativeUiFont) private var uiFont
    @Binding var text: String
    let prompt: String
    let focused: FocusState<Bool>.Binding
    let identifier: String
    let submit: () -> Void
    var body: some View {
        let _ = uiFont
        TextField(prompt, text: $text).font(NativeTheme.font(15)).focused(focused)
            .textInputAutocapitalization(.never).disableAutocorrection(true).submitLabel(.search)
            .frame(minHeight: 44).accessibilityIdentifier(identifier).onSubmit(submit)
    }
}

struct NativeTextField: View {
    @Environment(\.nativeUiFont) private var uiFont
    let title: String
    @Binding var text: String
    var secure = false
    var identifier = ""
    var body: some View {
        let _ = uiFont
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(NativeTheme.font(13, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
            Group {
                if secure { SecureField(title, text: $text) }
                else { TextField(title, text: $text) }
            }.font(NativeTheme.font()).padding(.horizontal, 12).padding(.vertical, 11)
                .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 12))
                .accessibilityLabel(title)
                .accessibilityIdentifier(identifier)
        }
    }
}

/// Borderless labeled field for `.insetGrouped` settings cards so inputs sit
/// directly on the card surface without a nested gray box.
struct NativeInlineTextField: View {
    @Environment(\.nativeUiFont) private var uiFont
    let title: String
    @Binding var text: String
    var secure = false
    var identifier = ""
    var body: some View {
        let _ = uiFont
        VStack(alignment: .leading, spacing: 3) {
            Text(title)
                .font(NativeTheme.font(12, relativeTo: .caption))
                .foregroundStyle(NativeTheme.secondary)
            Group {
                if secure { SecureField("", text: $text) }
                else { TextField("", text: $text) }
            }
            .font(NativeTheme.font())
            .frame(maxWidth: .infinity, minHeight: 24, alignment: .leading)
            .accessibilityLabel(title)
            .accessibilityIdentifier(identifier)
        }
        .padding(.vertical, 2)
    }
}

struct NativeToggle: View {
    @Environment(\.nativeUiFont) private var uiFont
    let title: String
    @Binding var isOn: Bool
    var identifier = ""
    var body: some View {
        let _ = uiFont
        HStack(spacing: 12) {
            Text(title).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary)
                .frame(width: 58, alignment: .leading).accessibilityHidden(true)
            Spacer(minLength: 12)
            NativeSwitch(title: title, isOn: $isOn, identifier: identifier).fixedSize()
        }.frame(minHeight: 44).accessibilityElement(children: .contain)
    }
}

/// Keep UIKit's switch bounds and accessibility element together. SwiftUI can
/// expand a Toggle's accessibility frame to its List row after scrolling.
struct NativeSwitch: UIViewRepresentable {
    let title: String
    @Binding var isOn: Bool
    let identifier: String
    @Environment(\.isEnabled) private var enabled

    func makeCoordinator() -> Coordinator { Coordinator(isOn: $isOn) }

    func makeUIView(context: Context) -> UISwitch {
        let control = UISwitch()
        control.setContentHuggingPriority(.required, for: .horizontal)
        control.setContentHuggingPriority(.required, for: .vertical)
        control.setContentCompressionResistancePriority(.required, for: .horizontal)
        control.setContentCompressionResistancePriority(.required, for: .vertical)
        control.addTarget(context.coordinator, action: #selector(Coordinator.valueChanged(_:)), for: .valueChanged)
        return control
    }

    func updateUIView(_ control: UISwitch, context: Context) {
        context.coordinator.isOn = $isOn
        control.isEnabled = enabled
        control.onTintColor = nil
        control.accessibilityLabel = title
        control.accessibilityIdentifier = identifier
        if control.isOn != isOn { control.setOn(isOn, animated: false) }
    }

    @available(iOS 16.0, *)
    func sizeThatFits(_ proposal: ProposedViewSize, uiView: UISwitch, context: Context) -> CGSize? {
        uiView.intrinsicContentSize
    }

    final class Coordinator: NSObject {
        var isOn: Binding<Bool>
        init(isOn: Binding<Bool>) { self.isOn = isOn }
        @objc func valueChanged(_ control: UISwitch) { isOn.wrappedValue = control.isOn }
    }
}

struct NativeThinProgress: View {
    let value: Double
    var total = 100.0
    var height: CGFloat = 2
    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .leading) {
                NativeTheme.strong
                NativeTheme.ink.frame(width: geometry.size.width * min(1, max(0, value / max(1, total))))
            }.clipShape(Capsule())
        }.frame(height: height).accessibilityHidden(true)
    }
}

/// Inset grouped settings cards keep native keyboard avoidance, scrolling,
/// hairline separators and accessibility behavior while matching iOS Settings.
struct NativeForm<Content: View>: View {
    @EnvironmentObject private var store: NativeStore
    let content: Content
    init(@ViewBuilder content: () -> Content) { self.content = content() }
    var body: some View {
        List {
            content
                .listRowInsets(EdgeInsets(top: 10, leading: 16, bottom: 10, trailing: 16))
                .listRowBackground(NativeGroupedRowBackground(colorfulMode: store.preferences.colorfulMode))
        }.listStyle(.insetGrouped).textCase(nil).font(NativeTheme.font()).buttonStyle(.borderless)
            .modifier(NativeGroupedListBackground())
    }
}

struct NativeGroupedRowBackground: View {
    let colorfulMode: Bool
    var body: some View {
        if colorfulMode {
            Rectangle().fill(.regularMaterial)
        } else {
            Color(uiColor: .secondarySystemGroupedBackground)
        }
    }
}

struct NativeGroupedListBackground: ViewModifier {
    @EnvironmentObject private var store: NativeStore
    @ViewBuilder func body(content: Content) -> some View {
        if store.preferences.colorfulMode {
            if #available(iOS 16.0, *) { content.scrollContentBackground(.hidden).background(NativeAmbientBackground()) }
            else { content.background(NativeAmbientBackground()) }
        } else {
            if #available(iOS 16.0, *) {
                content.scrollContentBackground(.hidden)
                    .background(Color(uiColor: .systemGroupedBackground).ignoresSafeArea())
            } else {
                content.background(Color(uiColor: .systemGroupedBackground).ignoresSafeArea())
            }
        }
    }
}

struct NativeListBackground: ViewModifier {
    @ViewBuilder func body(content: Content) -> some View {
        if #available(iOS 16.0, *) { content.scrollContentBackground(.hidden).background(NativeAmbientBackground()) }
        else { content.background(NativeAmbientBackground()) }
    }
}

/// iOS 15's TextEditor paints the system background; iOS 16 can clear it so
/// the editor sits on the same card as the rest of the form.
struct NativeScrollContentBackgroundHidden: ViewModifier {
    @ViewBuilder func body(content: Content) -> some View {
        if #available(iOS 16.0, *) { content.scrollContentBackground(.hidden) } else { content }
    }
}

struct NativeScrollDismissesKeyboard: ViewModifier {
    @ViewBuilder func body(content: Content) -> some View {
        if #available(iOS 16.0, *) { content.scrollDismissesKeyboard(.interactively) } else { content }
    }
}

struct NativeFloatingSurface: ViewModifier {
    @Environment(\.colorScheme) private var scheme
    var backgroundOpacity = 0.97
    var shadowStrength = 1.0
    var verticalInset: CGFloat = 0

    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: 16)
        content.background {
            shape.fill(NativeTheme.surface.opacity(backgroundOpacity))
                .overlay { shape.stroke(NativeTheme.divider, lineWidth: 0.5) }
                .shadow(color: .black.opacity((scheme == .dark ? 0.25 : 0.10) * shadowStrength), radius: 10, y: 4)
                .padding(.vertical, verticalInset)
                .allowsHitTesting(false)
        }
    }
}

/// Uses iOS 26 Liquid Glass for floating bars, falling back to regular material on older iOS.
/// Container bars default to non-interactive glass so child buttons and sliders do not
/// contend with an outer interactive glass gesture recognizer.
struct NativeLiquidGlassSurface<S: Shape>: ViewModifier {
    let shape: S
    var interactive = false

    @ViewBuilder func body(content: Content) -> some View {
        if #available(iOS 26.0, *) {
            if interactive {
                content
                    .clipShape(shape)
                    .glassEffect(.regular.interactive(), in: shape)
            } else {
                content
                    .clipShape(shape)
                    .glassEffect(.regular, in: shape)
            }
        } else {
            content
                .clipShape(shape)
                .background(.regularMaterial, in: shape)
                .overlay(shape.stroke(NativeTheme.divider.opacity(0.6), lineWidth: 0.5))
                .shadow(color: .black.opacity(0.12), radius: 12, y: 5)
        }
    }
}

/// Immediate touch-down press feedback for buttons hosted inside a floating
/// Liquid Glass toolbar, avoiding container-level glass gesture delays.
struct NativeGlassToolbarButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .opacity(configuration.isPressed ? 0.45 : 1)
            .scaleEffect(configuration.isPressed ? 0.94 : 1)
            .animation(.easeOut(duration: 0.1), value: configuration.isPressed)
    }
}

struct NativeHideTabBar: ViewModifier {
    @ViewBuilder func body(content: Content) -> some View {
        if #available(iOS 16.0, *) { content.toolbar(.hidden, for: .tabBar) }
        else { content }
    }
}

/// The system back button grows into a translucent history menu on long press;
/// over a transparent bar that pill blurs the page title into a ghost. A plain
/// chevron keeps the same back behavior without that overlay.
struct NativeBackButton: ViewModifier {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.locale) private var uiLocale
    func body(content: Content) -> some View {
        let _ = uiLocale
        return content.navigationBarBackButtonHidden(true)
            .background(NativeSwipeBackEnabler().frame(width: 0, height: 0))
            .toolbar {
                ToolbarItem(placement: .navigationBarLeading) {
                    Button { dismiss() } label: { Image(systemName: "chevron.left") }
                        .accessibilityLabel(L("返回"))
                }
            }
    }
}

/// Hiding the system back button also disables the leading-edge swipe. Hook the
/// existing pop gesture back up, but only while there is a page to pop to.
private struct NativeSwipeBackEnabler: UIViewControllerRepresentable {
    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeUIViewController(context: Context) -> UIViewController {
        let controller = UIViewController()
        context.coordinator.host = controller
        DispatchQueue.main.async { context.coordinator.install() }
        return controller
    }

    func updateUIViewController(_ uiViewController: UIViewController, context: Context) {
        context.coordinator.host = uiViewController
        DispatchQueue.main.async { context.coordinator.install() }
    }

    final class Coordinator: NSObject, UIGestureRecognizerDelegate {
        weak var host: UIViewController?

        func install() {
            guard let navigation = host?.navigationController,
                  let gesture = navigation.interactivePopGestureRecognizer else { return }
            gesture.delegate = self
            gesture.isEnabled = true
        }

        func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
            (host?.navigationController?.viewControllers.count ?? 0) > 1
        }
    }
}

extension View {
    func nativeBackButton() -> some View { modifier(NativeBackButton()) }
}
