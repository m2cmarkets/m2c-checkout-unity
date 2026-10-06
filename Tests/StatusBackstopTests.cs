using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using M2C.Checkout.Internal;
using NUnit.Framework;

namespace M2C.Checkout.Tests
{
    public class StatusBackstopTests
    {
        [SetUp]
        public void Reset() { ResumeStore.Clear(); }

        [TearDown]
        public void Cleanup() { ResumeStore.Clear(); }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Processing_and_retryable_URL_or_callback_errors_reach_backstop(bool url)
        {
            foreach (M2CErrorCode? code in new M2CErrorCode?[] { null, M2CErrorCode.Network,
                M2CErrorCode.RateLimited, M2CErrorCode.ServiceUnavailable })
            {
                var h = new Harness(url);
                h.Primary = () =>
                {
                    h.Now += 10;
                    if (code.HasValue) throw new M2CCheckoutException(code.Value, "primary unavailable");
                    return Task.FromResult(ClientStatus.Processing);
                };
                Assert.AreEqual(ClientStatus.Completed, await h.Client.CheckStatusAsync("req_original"));
                Assert.AreEqual(1, h.PrimaryCalls);
                Assert.AreEqual(1, h.BackstopCalls);
                Assert.AreEqual(0, h.AuctionCalls);
            }
        }

        [Test]
        public async Task Resolved_primary_wins_and_disabled_or_ineligible_backstop_is_not_read()
        {
            foreach (ClientStatus status in new[] { ClientStatus.Completed, ClientStatus.Failed,
                ClientStatus.Canceled, ClientStatus.Processing })
            foreach (bool enabled in new[] { false, true })
            {
                var h = new Harness(false, enabled);
                h.Primary = () => { h.Now += status == ClientStatus.Processing ? 1 : 10; return Task.FromResult(status); };
                Assert.AreEqual(status, await h.Client.CheckStatusAsync("req_original"));
                Assert.AreEqual(0, h.BackstopCalls);
            }
        }

        [Test]
        public void Actionable_error_and_cancellation_do_not_read_backstop()
        {
            var h = new Harness();
            h.Primary = () => { h.Now += 10; throw new M2CCheckoutException(M2CErrorCode.AuthenticationFailed, "primary rejected"); };
            var error = Assert.ThrowsAsync<M2CCheckoutException>(async () => await h.Client.CheckStatusAsync("req_original"));
            Assert.AreEqual(M2CErrorCode.AuthenticationFailed, error.Code);
            Assert.AreEqual(0, h.BackstopCalls);
            h.Primary = () => Task.FromCanceled<ClientStatus>(new CancellationToken(true));
            Assert.ThrowsAsync<TaskCanceledException>(async () => await h.Client.CheckStatusAsync("req_original"));
            Assert.AreEqual(0, h.BackstopCalls);
        }

        [Test]
        public async Task Retryable_error_before_eligibility_and_processing_with_unavailable_backstop()
        {
            foreach (bool enabled in new[] { false, true })
            {
                var h = new Harness(false, enabled);
                var original = new M2CCheckoutException(M2CErrorCode.Network, "original");
                h.Primary = () => { h.Now += 1; throw original; };
                Assert.AreSame(original, Assert.ThrowsAsync<M2CCheckoutException>(async () => await h.Client.CheckStatusAsync("req_original")));
                Assert.AreEqual(0, h.BackstopCalls);
            }
            var pending = new Harness();
            pending.Primary = () => { pending.Now += 10; return Task.FromResult(ClientStatus.Processing); };
            pending.Backstop = () => { throw new M2CCheckoutException(M2CErrorCode.AuthenticationFailed, "secondary rejected"); };
            Assert.AreEqual(ClientStatus.Processing, await pending.Client.CheckStatusAsync("req_original"));
            Assert.AreEqual(1, pending.BackstopCalls);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Unavailable_or_unresolved_backstop_preserves_original_error(bool unavailable)
        {
            var h = new Harness();
            var original = new M2CCheckoutException(M2CErrorCode.RateLimited, "original");
            h.Primary = () => { h.Now += 10; throw original; };
            h.Backstop = () =>
            {
                if (unavailable) throw new M2CCheckoutException(M2CErrorCode.AuthenticationFailed, "secondary rejected");
                return Task.FromResult(ClientStatus.Processing);
            };
            Assert.AreSame(original, Assert.ThrowsAsync<M2CCheckoutException>(async () => await h.Client.CheckStatusAsync("req_original")));
            Assert.AreEqual(1, h.BackstopCalls);
        }

        [Test]
        public async Task M2C_primary_reads_once_and_missing_backstop_key_is_rejected()
        {
            var h = new Harness();
            h.Config.StatusSource = StatusSource.M2C;
            Assert.AreEqual(ClientStatus.Completed, await h.Client.CheckStatusAsync("req_original"));
            Assert.AreEqual(1, h.BackstopCalls);
            h.Config.StatusSource = StatusSource.Callback(_ => Task.FromResult(ClientStatus.Processing));
            h.Config.PublishableKey = null;
            var error = Assert.ThrowsAsync<M2CCheckoutException>(async () => await h.Client.CheckStatusAsync("req_original"));
            Assert.AreEqual(M2CErrorCode.InvalidRequest, error.Code);
            Assert.AreEqual(1, h.BackstopCalls);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Short_return_reserves_one_backstop_before_threshold_within_existing_budget(bool stalled)
        {
            var h = new Harness();
            h.Config.Poll = new PollSchedule(new[] { 0.0, 0.25, 0.5 }, 1);
            if (stalled) h.Primary = () => new TaskCompletionSource<ClientStatus>().Task;
            CheckoutResult result = await h.Client.StartFromSessionAsync(Session("req_original"));
            Assert.AreEqual(CheckoutOutcome.Completed, result.Outcome);
            Assert.AreEqual("req_original", result.RequestId);
            Assert.AreEqual(1, h.BackstopCalls);
            Assert.GreaterOrEqual(h.Now, 0.5);
            Assert.LessOrEqual(h.Now, 1);
            Assert.AreEqual(0, h.AuctionCalls);
            Assert.AreEqual(1, h.BrowserCalls);
            Assert.IsNull(ResumeStore.PendingRecord());
        }

        [Test]
        public async Task Saved_URL_remains_primary_during_recovery_and_uses_backstop()
        {
            var h = new Harness(true);
            ResumeStore.Save("req_original", "session", StatusSource.Url("https://original.example/status/{request_id}"));
            h.Config.StatusSource = StatusSource.Url("https://current.example/status/{request_id}");
            h.Primary = () => { h.Now += 10; throw new M2CCheckoutException(M2CErrorCode.Network, "original unavailable"); };
            Assert.AreEqual(CheckoutOutcome.Completed, (await h.Client.TryResumeAsync()).Outcome);
            CollectionAssert.AreEqual(new[] { "https://original.example/status/{request_id}" }, h.Templates);
            Assert.AreEqual(1, h.BackstopCalls);
            Assert.AreEqual(0, h.BrowserCalls);
            Assert.IsNull(ResumeStore.PendingRecord());
        }

        [TestCase("dismissed")]
        [TestCase("cancel_return")]
        [TestCase("timeout")]
        public async Task Late_success_can_follow_UI_outcome_and_a_distinct_explicit_purchase(string outcome)
        {
            var h = new Harness(false, false);
            h.Config.Poll = new PollSchedule(new[] { 0.0, 0.5 }, 1);
            h.BrowserOutcome = outcome == "dismissed" ? BrowserOutcome.Dismissed
                : outcome == "cancel_return" ? BrowserOutcome.Returned(h.Config.CancelUrl)
                : BrowserOutcome.Resumed;
            CheckoutResult original = await h.Client.StartFromSessionAsync(Session("req_original"));
            Assert.AreEqual(outcome == "timeout" ? CheckoutOutcome.PendingTimeout : CheckoutOutcome.Canceled, original.Outcome);
            Assert.IsNull(ResumeStore.PendingRecord());
            h.Primary = () => Task.FromResult(ClientStatus.Completed);
            Assert.AreEqual(ClientStatus.Completed, await h.Client.CheckStatusAsync("req_original"));
            h.BrowserOutcome = BrowserOutcome.Resumed;
            CheckoutResult distinct = await h.Client.StartFromSessionAsync(Session("req_distinct"));
            Assert.AreEqual(CheckoutOutcome.Completed, distinct.Outcome);
            Assert.AreEqual("req_distinct", distinct.RequestId);
            Assert.AreEqual(0, h.AuctionCalls);
            Assert.AreEqual(0, h.PaymentFallbackCalls);
            Assert.AreEqual(2, h.BrowserCalls);
        }

        [Test]
        public async Task Cancellation_after_exposure_does_not_start_payment_fallback()
        {
            var h = new Harness();
            h.Primary = () => Task.FromCanceled<ClientStatus>(new CancellationToken(true));
            Assert.ThrowsAsync<TaskCanceledException>(async () => await h.Client.StartFromSessionAsync(Session("req_original")));
            Assert.AreEqual(0, h.BackstopCalls);
            Assert.AreEqual(0, h.PaymentFallbackCalls);
            Assert.AreEqual("req_original", ResumeStore.PendingRecord().RequestId);
            h.Primary = () => Task.FromResult(ClientStatus.Completed);
            Assert.AreEqual(CheckoutOutcome.Completed, (await h.Client.TryResumeAsync()).Outcome);
            Assert.IsNull(ResumeStore.PendingRecord());
            CheckoutResult distinct = await h.Client.StartFromSessionAsync(Session("req_distinct"));
            Assert.AreEqual(CheckoutOutcome.Completed, distinct.Outcome);
            Assert.AreEqual("req_distinct", distinct.RequestId);
            Assert.AreEqual(0, h.AuctionCalls);
            Assert.AreEqual(0, h.PaymentFallbackCalls);
        }

        private static CheckoutSession Session(string id) => new CheckoutSession
        { CheckoutUrl = "https://vendor.example/pay", RequestId = id };

        private sealed class Harness
        {
            public double Now;
            public int PrimaryCalls, BackstopCalls, AuctionCalls, BrowserCalls, PaymentFallbackCalls;
            public readonly List<string> Templates = new List<string>();
            public Func<Task<ClientStatus>> Primary = () => Task.FromResult(ClientStatus.Processing);
            public Func<Task<ClientStatus>> Backstop = () => Task.FromResult(ClientStatus.Completed);
            public BrowserOutcome BrowserOutcome = BrowserOutcome.Resumed;
            public readonly M2CConfig Config;
            public readonly M2CCheckoutClient Client;

            public Harness(bool url = false, bool enabled = true)
            {
                Config = new M2CConfig
                {
                    PublishableKey = "pub_test_example", ReturnUrl = "mygame://checkout/return", CancelUrl = "mygame://checkout/cancel",
                    StatusSource = url ? StatusSource.Url("https://merchant.example/status/{request_id}")
                        : StatusSource.Callback(_ => Primary()),
                    UseM2CStatusFallback = enabled, M2CFallbackAfterSeconds = 5,
                    Poll = new PollSchedule(new[] { 0.0, 0.25, 0.5 }, 20),
                    FallbackHandler = (_, __) => { PaymentFallbackCalls++; return Task.FromResult(FallbackDecision.Accepted); }
                };
                Client = new M2CCheckoutClient(Config,
                    (_, __, ___) => { AuctionCalls++; throw new Exception("unexpected checkout initiation"); },
                    (seconds, token) => { token.ThrowIfCancellationRequested(); Now += seconds; return Task.CompletedTask; },
                    _ => { BrowserCalls++; return new TestBrowser(this); },
                    monotonicSeconds: () => Now,
                    readStatus: (id, source, budget) =>
                    {
                        Assert.IsTrue(id == "req_original" || id == "req_distinct");
                        Assert.Greater(budget, 0);
                        if (source.Kind == StatusSourceKind.M2C) { BackstopCalls++; return Backstop(); }
                        PrimaryCalls++;
                        if (source.Kind == StatusSourceKind.Url) Templates.Add(source.UrlTemplate);
                        return Primary();
                    });
            }
        }

        private sealed class TestBrowser : ICheckoutBrowser
        {
            private readonly Harness _h;
            public TestBrowser(Harness h) { _h = h; }
            public bool RequiresReturnUrl => false;
            public Task<BrowserOutcome> LaunchAsync(string checkoutUrl, string returnUrl, string cancelUrl) => Task.FromResult(_h.BrowserOutcome);
        }
    }
}
