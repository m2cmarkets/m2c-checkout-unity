using System.Threading.Tasks;
using UnityEngine;

namespace M2C.Checkout
{
    internal sealed class SystemShopSessionBrowser : IShopSessionBrowser
    {
        public void PrepareLaunch() { }

        public Task LaunchAsync(string shopUrl, string returnUrl)
        {
            Application.OpenURL(shopUrl);
            return Task.CompletedTask;
        }

        public void CancelPreparedLaunch() { }
    }
}
