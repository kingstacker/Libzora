import AuthenticationServices
import CryptoKit
import Security
import SwiftUI
import UIKit

struct NativeAppleSignInCredential {
    let identityToken: String
    let authorizationCode: String?
    let nonce: String
    let email: String?
    let displayName: String?

    var bridgeArguments: [String: Any] {
        var args: [String: Any] = [
            "identityToken": identityToken,
            "nonce": nonce
        ]
        if let authorizationCode, !authorizationCode.isEmpty {
            args["authorizationCode"] = authorizationCode
        }
        if let email, !email.isEmpty {
            args["email"] = email
        }
        if let displayName, !displayName.isEmpty {
            args["displayName"] = displayName
        }
        return args
    }
}

/// Runs Apple's native Sign in with Apple sheet with a SHA-256 nonce and
/// returns `nil` when the user cancels.
@MainActor
final class NativeAppleSignInCoordinator: NSObject, ASAuthorizationControllerDelegate,
    ASAuthorizationControllerPresentationContextProviding {
    private weak var anchorWindow: UIWindow?
    private var continuation: CheckedContinuation<NativeAppleSignInCredential?, Error>?
    private var rawNonce = ""
    private var activeController: ASAuthorizationController?

    static func authorize(in window: UIWindow?) async throws -> NativeAppleSignInCredential? {
        let coordinator = NativeAppleSignInCoordinator()
        return try await coordinator.run(in: window)
    }

    private func run(in window: UIWindow?) async throws -> NativeAppleSignInCredential? {
        anchorWindow = window
        let nonce = Self.generateNonce()
        rawNonce = nonce

        let request = ASAuthorizationAppleIDProvider().createRequest()
        request.requestedScopes = [.fullName, .email]
        request.nonce = Self.sha256Hex(nonce)

        let controller = ASAuthorizationController(authorizationRequests: [request])
        controller.delegate = self
        controller.presentationContextProvider = self
        activeController = controller

        return try await withCheckedThrowingContinuation { continuation in
            self.continuation = continuation
            controller.performRequests()
        }
    }

    nonisolated func presentationAnchor(for controller: ASAuthorizationController) -> ASPresentationAnchor {
        MainActor.assumeIsolated {
            if let anchorWindow { return anchorWindow }
            if let active = UIApplication.shared.connectedScenes
                .compactMap({ $0 as? UIWindowScene })
                .flatMap(\.windows)
                .first(where: { $0.isKeyWindow }) {
                return active
            }
            return UIWindow()
        }
    }

    nonisolated func authorizationController(
        controller: ASAuthorizationController,
        didCompleteWithAuthorization authorization: ASAuthorization
    ) {
        MainActor.assumeIsolated {
            defer { activeController = nil }
            guard let appleID = authorization.credential as? ASAuthorizationAppleIDCredential,
                  let tokenData = appleID.identityToken,
                  let identityToken = String(data: tokenData, encoding: .utf8),
                  !identityToken.isEmpty else {
                finish(with: .failure(NativeFailure(message: L("未能获取 Apple 登录凭证，请重试。"))))
                return
            }
            let authCode = appleID.authorizationCode.flatMap { String(data: $0, encoding: .utf8) }
            let formattedName: String? = appleID.fullName.flatMap { components in
                let text = PersonNameComponentsFormatter().string(from: components)
                    .trimmingCharacters(in: .whitespacesAndNewlines)
                return text.isEmpty ? nil : text
            }
            let credential = NativeAppleSignInCredential(
                identityToken: identityToken,
                authorizationCode: authCode,
                nonce: rawNonce,
                email: appleID.email,
                displayName: formattedName
            )
            finish(with: .success(credential))
        }
    }

    nonisolated func authorizationController(
        controller: ASAuthorizationController,
        didCompleteWithError error: Error
    ) {
        MainActor.assumeIsolated {
            defer { activeController = nil }
            if let authError = error as? ASAuthorizationError, authError.code == .canceled {
                finish(with: .success(nil))
                return
            }
            finish(with: .failure(NativeFailure(message: L("Apple 登录失败，请重试。"))))
        }
    }

    private func finish(with result: Result<NativeAppleSignInCredential?, Error>) {
        guard let continuation else { return }
        self.continuation = nil
        continuation.resume(with: result)
    }

    private static func generateNonce(length: Int = 32) -> String {
        let charset = Array("0123456789ABCDEFGHIJKLMNOPQRSTUVXYZabcdefghijklmnopqrstuvwxyz-._")
        var bytes = [UInt8](repeating: 0, count: length)
        let status = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        if status != errSecSuccess {
            return UUID().uuidString.replacingOccurrences(of: "-", with: "")
        }
        return String(bytes.map { charset[Int($0) % charset.count] })
    }

    private static func sha256Hex(_ input: String) -> String {
        let digest = SHA256.hash(data: Data(input.utf8))
        return digest.map { String(format: "%02x", $0) }.joined()
    }
}

/// Official Apple HIG button that adapts to light/dark mode while letting
/// `NativeAccountPage` drive the async nonce + backend exchange cleanly.
struct NativeAppleSignInButton: UIViewRepresentable {
    let type: ASAuthorizationAppleIDButton.ButtonType
    let style: ASAuthorizationAppleIDButton.Style
    let cornerRadius: CGFloat
    let action: () -> Void

    final class Coordinator: NSObject {
        var action: () -> Void
        init(action: @escaping () -> Void) { self.action = action }
        @objc func tapped() { action() }
    }

    func makeCoordinator() -> Coordinator { Coordinator(action: action) }

    func makeUIView(context: Context) -> ASAuthorizationAppleIDButton {
        let button = ASAuthorizationAppleIDButton(type: type, style: style)
        button.cornerRadius = cornerRadius
        button.addTarget(context.coordinator, action: #selector(Coordinator.tapped), for: .touchUpInside)
        return button
    }

    func updateUIView(_ uiView: ASAuthorizationAppleIDButton, context: Context) {
        context.coordinator.action = action
        uiView.cornerRadius = cornerRadius
    }
}
