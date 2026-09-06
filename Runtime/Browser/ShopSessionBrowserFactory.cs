namespace M2C.Checkout.Internal
{
    internal static class ShopSessionBrowserFactory
    {
        public static IShopSessionBrowser Create(M2CConfig config)
        {
            M2CBrowserMode mode = config.EffectiveBrowserMode;
#if UNITY_EDITOR
            return new SystemShopSessionBrowser();
#elif UNITY_WEBGL
            return new WebGLShopSessionBrowser(config.WebGLLaunchMode);
#elif UNITY_IOS
            return mode == M2CBrowserMode.ExternalBrowser
                ? (IShopSessionBrowser)new SystemShopSessionBrowser()
                : new IosShopSessionBrowser();
#elif UNITY_ANDROID
            return mode == M2CBrowserMode.ExternalBrowser
                ? (IShopSessionBrowser)new AndroidShopSessionBrowser(false)
                : new AndroidShopSessionBrowser(true);
#elif UNITY_STANDALONE
            return new SystemShopSessionBrowser();
#else
            throw new M2CCheckoutException(
                M2CErrorCode.InvalidRequest,
                "M2C shop sessions do not support this Unity platform yet.");
#endif
        }
    }
}
