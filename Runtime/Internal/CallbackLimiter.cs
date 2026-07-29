using System;
using System.Threading;
using System.Threading.Tasks;

namespace M2C.Checkout.Internal
{
    internal static class CallbackLimiter
    {
        internal const int Capacity = 4;
        private static readonly SemaphoreSlim Permits = new SemaphoreSlim(Capacity, Capacity);

        public static async Task<ClientStatus> InvokeAsync(
            Func<Task<ClientStatus>> callback,
            double timeoutBudgetSeconds)
        {
            if (callback == null)
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "status callback is missing");

            TimeSpan wait = TimeSpan.FromSeconds(Math.Max(0, timeoutBudgetSeconds));
            if (!await Permits.WaitAsync(wait))
            {
                throw new M2CCheckoutException(
                    M2CErrorCode.Network,
                    "status callback capacity is exhausted");
            }

            try
            {
                return await callback();
            }
            finally
            {
                Permits.Release();
            }
        }
    }
}
