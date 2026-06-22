// M2C Checkout - iOS in-app browser shim.
//
// Presents the vendor checkout in an ASWebAuthenticationSession bound to the
// return URL's custom scheme, and reports the callback URL or launch failure
// back to C# through a function pointer. Shipped as source: Unity
// compiles it into the generated Xcode project; the Editor post-processor links
// AuthenticationServices.framework and registers the URL scheme.
//
// VERIFY ON DEVICE.

#import <Foundation/Foundation.h>
#import <AuthenticationServices/AuthenticationServices.h>
#import <UIKit/UIKit.h>

typedef void (*M2CAuthCallback)(int success, const char *url);

API_AVAILABLE(ios(13.0))
@interface M2CAuthPresenter : NSObject <ASWebAuthenticationPresentationContextProviding>
@end

static UIWindow *M2CActiveWindow(void) {
    if (@available(iOS 13.0, *)) {
        for (UIScene *scene in UIApplication.sharedApplication.connectedScenes) {
            if (scene.activationState != UISceneActivationStateForegroundActive ||
                ![scene isKindOfClass:[UIWindowScene class]]) {
                continue;
            }

            UIWindowScene *windowScene = (UIWindowScene *)scene;
            for (UIWindow *window in windowScene.windows) {
                if (window.isKeyWindow) {
                    return window;
                }
            }
            if (windowScene.windows.count > 0) {
                return windowScene.windows.firstObject;
            }
        }
    }

#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
    UIWindow *keyWindow = UIApplication.sharedApplication.keyWindow;
#pragma clang diagnostic pop
    return keyWindow ?: UIApplication.sharedApplication.windows.firstObject;
}

@implementation M2CAuthPresenter
- (ASPresentationAnchor)presentationAnchorForWebAuthenticationSession:(ASWebAuthenticationSession *)session API_AVAILABLE(ios(13.0)) {
    return M2CActiveWindow();
}
@end

// Retained while a session is in flight so ARC doesn't deallocate them.
static ASWebAuthenticationSession *g_m2cSession = nil;
static M2CAuthPresenter *g_m2cPresenter = nil;

static void M2CAuthCallbackWithMessage(M2CAuthCallback callback, int success, NSString *message) {
    if (!callback) return;
    callback(success, message.length ? message.UTF8String : NULL);
}

#ifdef __cplusplus
extern "C" {
#endif

void m2c_presentAuthSession(const char *url, const char *scheme, M2CAuthCallback callback) {
    if (@available(iOS 12.0, *)) {
        NSString *urlStr = url ? [NSString stringWithUTF8String:url] : @"";
        if (urlStr.length == 0) {
            M2CAuthCallbackWithMessage(callback, -1, @"iOS auth session failed: missing checkout URL.");
            return;
        }

        NSString *schemeStr = scheme ? [NSString stringWithUTF8String:scheme] : @"";
        if (!schemeStr) {
            schemeStr = @"";
        }

        NSURL *nsurl = [NSURL URLWithString:urlStr];
        if (!nsurl || nsurl.scheme.length == 0) {
            M2CAuthCallbackWithMessage(callback, -1, @"iOS auth session failed: invalid checkout URL.");
            return;
        }

        g_m2cSession = [[ASWebAuthenticationSession alloc]
            initWithURL:nsurl
            callbackURLScheme:(schemeStr.length ? schemeStr : nil)
            completionHandler:^(NSURL *_Nullable callbackURL, NSError *_Nullable error) {
                if (callback) {
                    if (callbackURL) {
                        callback(1, callbackURL.absoluteString.UTF8String);
                    } else if (error
                               && [error.domain isEqualToString:ASWebAuthenticationSessionErrorDomain]
                               && error.code == ASWebAuthenticationSessionErrorCodeCanceledLogin) {
                        callback(2, NULL); // explicit browser cancel; C# reconciles status
                    } else {
                        callback(0, NULL); // no callback URL, no explicit cancel - ambiguous (reconcile via status)
                    }
                }
                g_m2cSession = nil;
                g_m2cPresenter = nil;
            }];

        if (@available(iOS 13.0, *)) {
            g_m2cPresenter = [[M2CAuthPresenter alloc] init];
            g_m2cSession.presentationContextProvider = g_m2cPresenter;
            // Ephemeral session: no shared Safari cookies, which suppresses iOS's
            // "<App> Wants to Use <domain> to Sign In" consent prompt. A checkout is a
            // one-off payment, not an SSO login, so a fresh in-app session is the right
            // default - it drops that friction (which would otherwise appear on every
            // purchase) and Apple Pay still works. Trade-off: a Safari-shared vendor
            // login does not carry over; the customer authenticates in the in-app session.
            g_m2cSession.prefersEphemeralWebBrowserSession = YES;
        }
        if (![g_m2cSession start]) {
            g_m2cSession = nil;
            g_m2cPresenter = nil;
            M2CAuthCallbackWithMessage(callback, -1, @"iOS auth session failed to start.");
        }
    } else if (callback) {
        M2CAuthCallbackWithMessage(callback, -1, @"iOS auth session requires iOS 12 or newer.");
    }
}

#ifdef __cplusplus
}
#endif
