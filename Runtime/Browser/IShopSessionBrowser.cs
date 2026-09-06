using System.Threading.Tasks;

namespace M2C.Checkout
{
    /// <summary>
    /// Launch-only shop browser. Completion means the URL was handed off; the
    /// return URL lets owned in-process surfaces release their presentation.
    /// </summary>
    internal interface IShopSessionBrowser
    {
        void PrepareLaunch();
        Task LaunchAsync(string shopUrl, string returnUrl);
        void CancelPreparedLaunch();
    }
}
