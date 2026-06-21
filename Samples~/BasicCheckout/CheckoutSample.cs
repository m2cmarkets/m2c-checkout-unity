using M2C.Checkout;
using UnityEngine;

/// <summary>
/// Minimal example wiring. Attach to a GameObject, fill the fields, and call
/// <see cref="BuyGems"/> (client-initiated) or <see cref="StartFromBackend"/>
/// (backend-initiated) from a UI button.
///
/// Remember: the result is advisory UX. Grant entitlements server-side off the
/// signed conversion webhook, especially for virtual currency - games are a
/// high-fraud target and a client-side "completed" is spoofable.
/// </summary>
public sealed class CheckoutSample : MonoBehaviour
{
    [SerializeField] private bool useProjectSettings = true;
    [SerializeField] private string mobilePublishableKey = "pub_test_xxx"; // mobile key, client-initiated only
    [SerializeField] private string webGLPublishableKey = ""; // web/browser key for WebGL
    [SerializeField] private string mobileReturnUrl = "mygame://checkout/return";
    [SerializeField] private string mobileCancelUrl = "mygame://checkout/cancel";
    [SerializeField] private string webGLReturnUrl = "https://game.example/m2c-return.html";
    [SerializeField] private string webGLCancelUrl = "https://game.example/m2c-cancel.html";
    [SerializeField] private string statusUrlTemplate = ""; // optional: https://shop.example/status/{request_id}
    [SerializeField] private bool useExternalBrowser = false;
    [SerializeField] private float statusPollTimeoutSeconds = 90f;

    private M2CCheckoutClient _client;

    private void Awake()
    {
        _client = new M2CCheckoutClient(BuildConfig());
        _client.OnStateChanged += state => Debug.Log("[M2C] state -> " + state);
    }

    private M2CConfig BuildConfig()
    {
        if (useProjectSettings)
            return M2CConfig.FromProjectSettings();

        string statusUrl = (statusUrlTemplate ?? string.Empty).Trim();
        double pollTimeout = statusPollTimeoutSeconds > 0f ? statusPollTimeoutSeconds : PollSchedule.Default.TotalWindowSeconds;
#if UNITY_WEBGL && !UNITY_EDITOR
        string key = webGLPublishableKey;
        string successUrl = webGLReturnUrl;
        string cancelUrl = webGLCancelUrl;
#else
        string key = mobilePublishableKey;
        string successUrl = mobileReturnUrl;
        string cancelUrl = mobileCancelUrl;
#endif
        return new M2CConfig
        {
            PublishableKey = key,
            // Backend-initiated games should point at their own backend instead:
            //   StatusSource = StatusSource.Url("https://shop.example/status/{request_id}")
            StatusSource = string.IsNullOrEmpty(statusUrl) ? StatusSource.M2C : StatusSource.Url(statusUrl),
            ReturnUrl = successUrl,
            CancelUrl = cancelUrl,
            UseExternalBrowser = useExternalBrowser,
            Poll = new PollSchedule(PollSchedule.Default.RampSeconds, pollTimeout),
        };
    }

    // Client-initiated (no backend): the SDK runs the auction with the publishable key.
    public async void BuyGems()
    {
        try
        {
            CheckoutResult result = await _client.StartAsync(new AuctionRequest
            {
                TransactionValue = 4.99,
                Currency = "USD",
                Description = "100 Gems",
                DeviceType = "mobile",
            });
            HandleResult(result);
        }
        catch (M2CCheckoutException e)
        {
            Debug.LogError("[M2C] checkout error " + e.Code + ": " + e.Message
                + (e.Code == M2CErrorCode.RateLimited ? " (retry after " + e.RetryAfter + "s)" : ""));
        }
    }

    // Backend-initiated (recommended): your server ran the auction with its secret
    // key and forwarded these three fields to the client.
    public async void StartFromBackend(string checkoutUrl, string requestId, int ttl)
    {
        CheckoutResult result = await _client.StartFromSessionAsync(new CheckoutSession
        {
            CheckoutUrl = checkoutUrl,
            RequestId = requestId,
            Ttl = ttl,
        });
        HandleResult(result);
    }

    // Call once on startup so a checkout interrupted by a process kill can resume.
    public async void ResumeIfInterrupted()
    {
        CheckoutResult resumed = await _client.TryResumeAsync();
        if (resumed != null) HandleResult(resumed);
    }

    private void HandleResult(CheckoutResult result)
    {
        if (result == null) return; // coroutine/await path surfaced an error already
        switch (result.Outcome)
        {
            case CheckoutOutcome.Completed:
                Debug.Log("[M2C] completed (show success UI; goods are granted server-side off the webhook)");
                break;
            case CheckoutOutcome.Failed:
                Debug.Log("[M2C] payment failed");
                break;
            case CheckoutOutcome.Canceled:
                Debug.Log("[M2C] canceled");
                break;
            case CheckoutOutcome.PendingTimeout:
                Debug.Log("[M2C] still processing - we'll confirm shortly");
                break;
        }
    }
}
