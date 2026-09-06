#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AOT;

namespace M2C.Checkout
{
    internal sealed class WebGLShopSessionBrowser : IShopSessionBrowser
    {
        private readonly M2CWebGLLaunchMode _launchMode;
        private delegate void LaunchCallback(int result);

        [DllImport("__Internal")]
        private static extern int M2CShopSessionPrepare(int launchMode);

        [DllImport("__Internal")]
        private static extern void M2CShopSessionLaunch(string url, LaunchCallback callback);

        [DllImport("__Internal")]
        private static extern void M2CShopSessionCancelPrepared();

        private static readonly LaunchCallback LaunchComplete = OnLaunchComplete;
        private static TaskCompletionSource<bool> _pending;

        public WebGLShopSessionBrowser(M2CWebGLLaunchMode launchMode)
        {
            _launchMode = launchMode;
        }

        public void PrepareLaunch()
        {
            if (M2CShopSessionPrepare(LaunchModeCode(_launchMode)) == 0)
                throw new M2CCheckoutException(
                    M2CErrorCode.InvalidRequest,
                    "shop window could not be opened safely; allow popups for this site and try again");
        }

        public Task LaunchAsync(string shopUrl, string returnUrl)
        {
            if (_pending != null)
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "a shop window is already opening");
            var pending = new TaskCompletionSource<bool>();
            _pending = pending;
            M2CShopSessionLaunch(shopUrl, LaunchComplete);
            return pending.Task;
        }

        public void CancelPreparedLaunch()
        {
            M2CShopSessionCancelPrepared();
        }

        private static int LaunchModeCode(M2CWebGLLaunchMode launchMode)
        {
            return launchMode == M2CWebGLLaunchMode.Popup ? 2 :
                   launchMode == M2CWebGLLaunchMode.NewTab ? 1 : 0;
        }

        [MonoPInvokeCallback(typeof(LaunchCallback))]
        private static void OnLaunchComplete(int result)
        {
            TaskCompletionSource<bool> pending = _pending;
            _pending = null;
            if (pending == null) return;
            if (result > 0) pending.TrySetResult(true);
            else pending.TrySetException(new M2CCheckoutException(
                M2CErrorCode.InvalidRequest,
                "the reserved shop window was blocked or closed"));
        }
    }
}
#endif
