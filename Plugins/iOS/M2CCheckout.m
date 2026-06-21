// M2C Checkout - iOS in-app browser shim.
//
// Presents the vendor checkout in an ASWebAuthenticationSession bound to the
// return URL's custom scheme, and reports the callback URL (or an explicit
// user-cancel) back to C# through a function pointer. Shipped as source: Unity
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

@implementation M2CAuthPresenter
- (ASPresentationAnchor)presentationAnchorForWebAuthenticationSession:(ASWebAuthenticationSession *)session API_AVAILABLE(ios(13.0)) {
    return UIApplication.sharedApplication.keyWindow ?: UIApplication.sharedApplication.windows.firstObject;
}
@end

// Retained while a session is in flight so ARC doesn't deallocate them.
static ASWebAuthenticationSession *g_m2cSession = nil;
static M2CAuthPresenter *g_m2cPresenter = nil;

#ifdef __cplusplus
extern "C" {
#endif

void m2c_presentAuthSession(const char *url, const char *scheme, M2CAuthCallback callback) {
    if (@available(iOS 12.0, *)) {
        NSString *urlStr = url ? [NSString stringWithUTF8String:url] : @"";
        NSString *schemeStr = scheme ? [NSString stringWithUTF8String:scheme] : @"";
        NSURL *nsurl = [NSURL URLWithString:urlStr];

        g_m2cSession = [[ASWebAuthenticationSession alloc]
            initWithURL:nsurl
            callbackURLScheme:(schemeStr.length ? schemeStr : nil)
            completionHandler:^(NSURL *_Nullable callbackURL, NSError *_Nullable error) {
                if (callback) {
                    if (callbackURL) {
                        callback(1, callbackURL.absoluteString.UTF8String);
                    } else {
                        callback(0, NULL); // user canceled or no callback URL
                    }
                }
                g_m2cSession = nil;
                g_m2cPresenter = nil;
            }];

        if (@available(iOS 13.0, *)) {
            g_m2cPresenter = [[M2CAuthPresenter alloc] init];
            g_m2cSession.presentationContextProvider = g_m2cPresenter;
            g_m2cSession.prefersEphemeralWebBrowserSession = NO;
        }
        [g_m2cSession start];
    } else if (callback) {
        callback(0, NULL);
    }
}

#ifdef __cplusplus
}
#endif
