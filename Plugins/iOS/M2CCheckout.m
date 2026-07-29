// M2C Checkout - iOS in-app browser shim.
//
// Presents the vendor checkout in either an ephemeral
// ASWebAuthenticationSession or a persistence-preferred
// SFSafariViewController, then reports browser outcomes to C# through function
// pointers. Shipped as source: Unity compiles it into the generated Xcode
// project; the Editor post-processor links AuthenticationServices.framework,
// SafariServices.framework, and registers the URL scheme.
//
// VERIFY ON DEVICE.

#import <Foundation/Foundation.h>

// The macOS vector harness defines this to exercise the Foundation-only URL
// validator from the production translation unit without requiring UIKit.
#ifndef M2C_URL_VALIDATOR_TEST
#import <AuthenticationServices/AuthenticationServices.h>
#import <SafariServices/SafariServices.h>
#import <UIKit/UIKit.h>

typedef void (*M2CAuthCallback)(int success, const char *url);
typedef void (*M2CSafariCallback)(int result, const char *message);
typedef void (*M2CSafariDismissCallback)(void);

API_AVAILABLE(ios(13.0))
@interface M2CAuthPresenter : NSObject <ASWebAuthenticationPresentationContextProviding>
@end

@interface M2CSafariPresenter : NSObject <SFSafariViewControllerDelegate>
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

static UIViewController *M2CTopViewController(UIViewController *controller) {
    UIViewController *current = controller;
    while (current) {
        if (current.presentedViewController && !current.presentedViewController.isBeingDismissed) {
            current = current.presentedViewController;
            continue;
        }
        if ([current isKindOfClass:[UINavigationController class]]) {
            UIViewController *visible = ((UINavigationController *)current).visibleViewController;
            if (visible && visible != current) {
                current = visible;
                continue;
            }
        }
        if ([current isKindOfClass:[UITabBarController class]]) {
            UIViewController *selected = ((UITabBarController *)current).selectedViewController;
            if (selected && selected != current) {
                current = selected;
                continue;
            }
        }
        break;
    }
    return current;
}

@implementation M2CAuthPresenter
- (ASPresentationAnchor)presentationAnchorForWebAuthenticationSession:(ASWebAuthenticationSession *)session API_AVAILABLE(ios(13.0)) {
    return M2CActiveWindow();
}
@end

// Retained while a session is in flight so ARC doesn't deallocate them.
static ASWebAuthenticationSession *g_m2cSession = nil;
// Keep this slot untyped so iOS 12 builds only reference the iOS 13 presenter
// class inside the guarded allocation path below.
static id g_m2cPresenter = nil;
static SFSafariViewController *g_m2cSafariController = nil;
static M2CSafariPresenter *g_m2cSafariPresenter = nil;
static M2CSafariCallback g_m2cSafariCallback = NULL;
static BOOL g_m2cSafariDismissing = NO;

static void M2CAuthCallbackWithMessage(M2CAuthCallback callback, int success, NSString *message) {
    if (!callback) return;
    callback(success, message.length ? message.UTF8String : NULL);
}

static void M2CSafariCallbackWithMessage(M2CSafariCallback callback, int result, NSString *message) {
    if (!callback) return;
    callback(result, message.length ? message.UTF8String : NULL);
}

static void M2CClearSafariState(SFSafariViewController *controller) {
    if (g_m2cSafariController != controller) return;
    g_m2cSafariCallback = NULL;
    controller.delegate = nil;
    g_m2cSafariController = nil;
    g_m2cSafariPresenter = nil;
    g_m2cSafariDismissing = NO;
}
#endif

static BOOL M2CIsExactLoopbackHost(NSString *host) {
    if (host.length == 0) return NO;
    NSString *normalized = host.lowercaseString;
    if ([normalized hasPrefix:@"["] && [normalized hasSuffix:@"]"] && normalized.length > 2) {
        normalized = [normalized substringWithRange:NSMakeRange(1, normalized.length - 2)];
    }
    if ([normalized isEqualToString:@"localhost"] || [normalized isEqualToString:@"::1"]) {
        return YES;
    }

    NSArray<NSString *> *parts = [normalized componentsSeparatedByString:@"."];
    if (parts.count != 4 || ![parts[0] isEqualToString:@"127"]) return NO;
    NSCharacterSet *nonDigits =
        [[NSCharacterSet characterSetWithCharactersInString:@"0123456789"] invertedSet];
    for (NSString *part in parts) {
        if (part.length == 0 || [part rangeOfCharacterFromSet:nonDigits].location != NSNotFound) {
            return NO;
        }
        if (part.integerValue < 0 || part.integerValue > 255) return NO;
    }
    return YES;
}

static NSString *M2CRawHostFromURLString(NSString *urlString) {
    NSRange separator = [urlString rangeOfString:@"://"];
    if (separator.location == NSNotFound) return nil;
    NSUInteger start = NSMaxRange(separator);
    NSRange authorityEnd = [urlString rangeOfCharacterFromSet:
        [NSCharacterSet characterSetWithCharactersInString:@"/?#"]
        options:0
        range:NSMakeRange(start, urlString.length - start)];
    NSString *authority = authorityEnd.location == NSNotFound
        ? [urlString substringFromIndex:start]
        : [urlString substringWithRange:NSMakeRange(start, authorityEnd.location - start)];
    NSRange at = [authority rangeOfString:@"@" options:NSBackwardsSearch];
    if (at.location != NSNotFound) {
        NSRange earlierAt = [authority rangeOfString:@"@"
                                             options:0
                                               range:NSMakeRange(0, at.location)];
        if (earlierAt.location != NSNotFound) return nil;
        authority = [authority substringFromIndex:NSMaxRange(at)];
    }

    if ([authority hasPrefix:@"["]) {
        NSRange close = [authority rangeOfString:@"]"];
        if (close.location == NSNotFound || close.location <= 1) return nil;
        NSString *suffix = [authority substringFromIndex:NSMaxRange(close)];
        if (suffix.length > 0 && ![suffix hasPrefix:@":"]) return nil;
        return [authority substringWithRange:NSMakeRange(1, close.location - 1)];
    }

    NSRange colon = [authority rangeOfString:@":" options:NSBackwardsSearch];
    NSString *host = colon.location == NSNotFound
        ? authority
        : [authority substringToIndex:colon.location];
    return host.length == 0 ? nil : host;
}

static BOOL M2CIsAllowedSafariCheckoutURL(NSString *urlString) {
    NSURLComponents *components = [NSURLComponents componentsWithString:urlString];
    if (!components.URL || components.scheme.length == 0 || components.host.length == 0) {
        return NO;
    }
    NSString *scheme = components.scheme.lowercaseString;
    if ([scheme isEqualToString:@"https"]) return YES;
    // Inspect the original host so alternate numeric and percent-encoded spellings
    // cannot inherit the local-development HTTP exception.
    return [scheme isEqualToString:@"http"] &&
           M2CIsExactLoopbackHost(M2CRawHostFromURLString(urlString));
}

#ifndef M2C_URL_VALIDATOR_TEST
@implementation M2CSafariPresenter
- (void)safariViewControllerDidFinish:(SFSafariViewController *)controller {
    // A return deep link can arrive at nearly the same time as the Done delegate.
    // Give it a short chance to win so a successful return is not downgraded to
    // an ambiguous dismissal.
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.25 * NSEC_PER_SEC)),
                   dispatch_get_main_queue(), ^{
        if (g_m2cSafariController != controller || g_m2cSafariDismissing) return;
        M2CSafariCallback callback = g_m2cSafariCallback;
        M2CClearSafariState(controller);
        M2CSafariCallbackWithMessage(callback, 0, nil);
    });
}
@end

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

void m2c_presentSafariViewController(const char *url, M2CSafariCallback callback) {
    NSString *urlString = url ? [NSString stringWithUTF8String:url] : @"";
    void (^present)(void) = ^{
        if (g_m2cSafariController) {
            M2CSafariCallbackWithMessage(callback, -1, @"iOS persistent browser is already open.");
            return;
        }

        NSURL *checkoutURL = [NSURL URLWithString:urlString];
        if (!checkoutURL || !M2CIsAllowedSafariCheckoutURL(urlString)) {
            M2CSafariCallbackWithMessage(callback, -1, @"iOS persistent browser requires HTTPS or exact loopback HTTP.");
            return;
        }

        UIWindow *window = M2CActiveWindow();
        UIViewController *host = M2CTopViewController(window.rootViewController);
        if (!window || !host || !host.view.window) {
            M2CSafariCallbackWithMessage(callback, -1, @"iOS persistent browser could not find an active view controller.");
            return;
        }

        g_m2cSafariPresenter = [[M2CSafariPresenter alloc] init];
        g_m2cSafariController = [[SFSafariViewController alloc] initWithURL:checkoutURL];
        g_m2cSafariCallback = callback;
        g_m2cSafariDismissing = NO;
        g_m2cSafariController.delegate = g_m2cSafariPresenter;
        [host presentViewController:g_m2cSafariController animated:YES completion:nil];
    };

    if (NSThread.isMainThread) {
        present();
    } else {
        dispatch_async(dispatch_get_main_queue(), present);
    }
}

void m2c_dismissSafariViewController(M2CSafariDismissCallback callback) {
    void (^dismiss)(void) = ^{
        SFSafariViewController *controller = g_m2cSafariController;
        if (!controller) {
            if (callback) callback();
            return;
        }

        g_m2cSafariCallback = NULL;
        controller.delegate = nil;
        g_m2cSafariDismissing = YES;

        __block BOOL finished = NO;
        void (^finish)(void) = ^{
            if (finished) return;
            finished = YES;
            M2CClearSafariState(controller);
            if (callback) callback();
        };

        if (controller.presentingViewController && !controller.isBeingDismissed) {
            [controller dismissViewControllerAnimated:YES completion:finish];
            return;
        }

        id<UIViewControllerTransitionCoordinator> coordinator = controller.transitionCoordinator;
        if (!coordinator) coordinator = controller.presentingViewController.transitionCoordinator;
        if (coordinator) {
            BOOL registered = [coordinator animateAlongsideTransition:nil completion:^(id<UIViewControllerTransitionCoordinatorContext> context) {
                (void)context;
                finish();
            }];
            if (registered) return;
        }

        finish();
    };

    if (NSThread.isMainThread) {
        dismiss();
    } else {
        dispatch_async(dispatch_get_main_queue(), dismiss);
    }
}

#ifdef __cplusplus
}
#endif
#endif
