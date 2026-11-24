#if LIBZORA_SWIFT_UI
using Foundation;
using UIKit;

namespace Libzora.iOSHead;

/// <summary>Creates the SwiftUI root before the window becomes visible.</summary>
[Register("IOSNativeSceneDelegate")]
public sealed class IOSNativeSceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    [Export("window")]
    public UIWindow? Window { get; set; }

    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions options)
    {
#if DEBUG
        Console.WriteLine($"Libzora native: connecting scene ({session.Role}).");
#endif
        if (scene is not UIWindowScene windowScene || session.Role != UIWindowSceneSessionRole.Application) return;
        Window = new UIWindow(windowScene);
        if (options.UrlContexts is { } urls) OpenUrlContexts(scene, urls);
        if (options.UserActivities is { } activities)
            foreach (var activity in activities) ContinueUserActivity(scene, activity);
        IOSNativeFrontend.Attach(Window);
        Window.MakeKeyAndVisible();
    }

    [Export("scene:openURLContexts:")]
    public void OpenUrlContexts(UIScene scene, NSSet<UIOpenUrlContext> contexts)
    {
        // Do not route through AvaloniaAppDelegate.OpenUrl: that method is not
        // virtual, so it would bypass Libzora's widget deep-link and file-import handling.
        foreach (var context in contexts)
            IOSNativeFrontend.HandleIncomingUrl(context.Url);
    }

    [Export("scene:continueUserActivity:")]
    public void ContinueUserActivity(UIScene scene, NSUserActivity activity)
    {
        if (UIApplication.SharedApplication.Delegate is AppDelegate appDelegate)
            appDelegate.ContinueUserActivity(UIApplication.SharedApplication, activity, _ => { });
    }
}
#endif
