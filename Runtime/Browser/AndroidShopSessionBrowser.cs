#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Threading.Tasks;
using UnityEngine;

namespace M2C.Checkout
{
    internal sealed class AndroidShopSessionBrowser : IShopSessionBrowser
    {
        private readonly bool _customTab;

        public AndroidShopSessionBrowser(bool customTab)
        {
            _customTab = customTab;
        }

        public void PrepareLaunch() { }

        public Task LaunchAsync(string shopUrl, string returnUrl)
        {
            if (_customTab && TryLaunchCustomTab(shopUrl)) return Task.CompletedTask;
            if (TryLaunchExternal(shopUrl)) return Task.CompletedTask;
            throw new M2CCheckoutException(M2CErrorCode.Network, "no browser is available to open the shop");
        }

        public void CancelPreparedLaunch() { }

        private static bool TryLaunchCustomTab(string url)
        {
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var builder = new AndroidJavaObject("androidx.browser.customtabs.CustomTabsIntent$Builder"))
                using (var customTabsIntent = builder.Call<AndroidJavaObject>("build"))
                using (var uriClass = new AndroidJavaClass("android.net.Uri"))
                using (var uri = uriClass.CallStatic<AndroidJavaObject>("parse", url))
                {
                    customTabsIntent.Call("launchUrl", activity, uri);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryLaunchExternal(string url)
        {
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var uriClass = new AndroidJavaClass("android.net.Uri"))
                using (var uri = uriClass.CallStatic<AndroidJavaObject>("parse", url))
                using (var intent = new AndroidJavaObject(
                    "android.content.Intent",
                    "android.intent.action.VIEW",
                    uri))
                {
                    activity.Call("startActivity", intent);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
#endif
