#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AOT;
using M2C.Checkout.Internal;

namespace M2C.Checkout
{
    internal sealed class IosShopSessionBrowser : IShopSessionBrowser
    {
        private delegate void LaunchCallback(int result, string message);
        private delegate void ClosedCallback();

        [DllImport("__Internal")]
        private static extern void m2c_presentShopSessionSafari(
            string url,
            LaunchCallback callback,
            ClosedCallback closedCallback);

        [DllImport("__Internal")]
        private static extern void m2c_dismissShopSessionSafari();

        private static readonly LaunchCallback LaunchComplete = OnLaunchComplete;
        private static readonly ClosedCallback BrowserClosed = OnBrowserClosed;
        private static TaskCompletionSource<bool> _pending;
        private static ReturnMonitorSession _returnMonitor;
        private static bool _isOpen;

        public void PrepareLaunch()
        {
            if (_pending != null || _isOpen)
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "an iOS shop browser is already open");
        }

        public Task LaunchAsync(string shopUrl, string returnUrl)
        {
            if (_pending != null || _isOpen)
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "an iOS shop browser is already open");
            var pending = new TaskCompletionSource<bool>();
            _pending = pending;
            _isOpen = true;
            try
            {
                if (!string.IsNullOrEmpty(returnUrl))
                {
                    _returnMonitor = ReturnMonitor.StartInProcess(
                        returnUrl,
                        returnUrl,
                        _ => m2c_dismissShopSessionSafari());
                }
                m2c_presentShopSessionSafari(shopUrl, LaunchComplete, BrowserClosed);
            }
            catch
            {
                _pending = null;
                ClearBrowserState();
                throw;
            }
            return pending.Task;
        }

        public void CancelPreparedLaunch() { }

        [MonoPInvokeCallback(typeof(LaunchCallback))]
        private static void OnLaunchComplete(int result, string message)
        {
            TaskCompletionSource<bool> pending = _pending;
            _pending = null;
            if (pending == null) return;
            if (result > 0)
            {
                pending.TrySetResult(true);
                return;
            }
            ClearBrowserState();
            pending.TrySetException(new M2CCheckoutException(
                M2CErrorCode.Network,
                string.IsNullOrEmpty(message) ? "iOS shop browser could not open" : message));
        }

        [MonoPInvokeCallback(typeof(ClosedCallback))]
        private static void OnBrowserClosed()
        {
            ClearBrowserState();
        }

        private static void ClearBrowserState()
        {
            _isOpen = false;
            ReturnMonitorSession monitor = _returnMonitor;
            _returnMonitor = null;
            monitor?.Stop();
        }
    }
}
#endif
