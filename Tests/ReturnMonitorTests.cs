using System;
using System.Threading.Tasks;
using NUnit.Framework;
using M2C.Checkout.Internal;

namespace M2C.Checkout.Tests
{
    public class ReturnMonitorTests
    {
        private const string ReturnUrl = "mygame://checkout/return";
        private const string CancelUrl = "mygame://checkout/cancel";

        [Test]
        public void Matching_return_completes_and_unsubscribes()
        {
            var runtime = new FakeReturnMonitorRuntime();
            var task = ReturnMonitor.Start(ReturnUrl, CancelUrl, runtime);

            runtime.RaiseDeepLink("othergame://checkout/return");
            Assert.IsFalse(task.IsCompleted);

            const string returned = ReturnUrl + "?request_id=req_1";
            runtime.RaiseDeepLink(returned);

            Assert.AreEqual(BrowserResult.Returned, task.Result.Result);
            Assert.AreEqual(returned, task.Result.ReturnUrl);
            Assert.AreEqual(1, runtime.UnsubscribeCalls);
            Assert.IsFalse(runtime.HasSubscribers);
        }

        [Test]
        public void Foreground_without_background_is_ignored()
        {
            var runtime = new FakeReturnMonitorRuntime();
            var task = ReturnMonitor.Start(ReturnUrl, CancelUrl, runtime);

            runtime.RaiseFocus(true);

            Assert.IsFalse(task.IsCompleted);
            Assert.IsNull(runtime.DelayedAction);
            Assert.AreEqual(0, runtime.UnsubscribeCalls);
        }

        [Test]
        public void Background_return_resumes_after_the_grace_period()
        {
            var runtime = new FakeReturnMonitorRuntime();
            var task = ReturnMonitor.Start(ReturnUrl, CancelUrl, runtime);

            runtime.RaiseFocus(false);
            runtime.RaiseFocus(true);

            Assert.AreEqual(0.5, runtime.DelayedSeconds);
            Assert.IsFalse(task.IsCompleted);
            runtime.RunDelayedAction();

            Assert.AreEqual(BrowserResult.Resumed, task.Result.Result);
            Assert.AreEqual(1, runtime.UnsubscribeCalls);
            Assert.IsFalse(runtime.HasSubscribers);
        }

        [Test]
        public void Matching_deep_link_wins_during_resume_grace_period()
        {
            var runtime = new FakeReturnMonitorRuntime();
            var task = ReturnMonitor.Start(ReturnUrl, CancelUrl, runtime);

            runtime.RaiseFocus(false);
            runtime.RaiseFocus(true);
            runtime.RaiseDeepLink(CancelUrl);
            runtime.RunDelayedAction();

            Assert.AreEqual(BrowserResult.Returned, task.Result.Result);
            Assert.AreEqual(CancelUrl, task.Result.ReturnUrl);
            Assert.AreEqual(1, runtime.UnsubscribeCalls);
        }

        [Test]
        public void In_process_monitor_ignores_app_focus_changes()
        {
            var runtime = new FakeReturnMonitorRuntime();
            var session = ReturnMonitor.StartInProcess(ReturnUrl, CancelUrl, runtime);

            runtime.RaiseFocus(false);
            runtime.RaiseFocus(true);

            Assert.IsFalse(session.Task.IsCompleted);
            Assert.IsNull(runtime.DelayedAction);

            runtime.RaiseDeepLink(ReturnUrl + "?request_id=req_2");

            Assert.AreEqual(BrowserResult.Returned, session.Task.Result.Result);
            Assert.AreEqual(1, runtime.UnsubscribeCalls);
            Assert.IsFalse(runtime.HasSubscribers);
        }

        [Test]
        public void In_process_native_dismissal_completes_and_unsubscribes()
        {
            var runtime = new FakeReturnMonitorRuntime();
            BrowserOutcome beforeCompleteOutcome = default(BrowserOutcome);
            var session = ReturnMonitor.StartInProcess(
                ReturnUrl,
                CancelUrl,
                runtime,
                outcome => beforeCompleteOutcome = outcome);

            Assert.IsTrue(session.TrySetResult(BrowserOutcome.Resumed));

            Assert.AreEqual(BrowserResult.Resumed, session.Task.Result.Result);
            Assert.AreEqual(BrowserResult.Resumed, beforeCompleteOutcome.Result);
            Assert.AreEqual(1, runtime.UnsubscribeCalls);
            Assert.IsFalse(runtime.HasSubscribers);
            runtime.RaiseDeepLink(ReturnUrl);
            Assert.AreEqual(BrowserResult.Resumed, session.Task.Result.Result);
        }

        // Android Auth Tab: the helper callback is not guaranteed (Custom Tab
        // fallback, launcher-icon return), so these pin that another signal settles
        // the checkout instead of leaving it pending forever.

        [Test]
        public void Auth_tab_deep_link_settles_when_the_helper_never_reports()
        {
            var runtime = new FakeReturnMonitorRuntime();
            var authTab = new AuthTabReturn(ReturnUrl, CancelUrl, runtime, UnexpectedFallback);

            const string returned = ReturnUrl + "?request_id=req_3";
            runtime.RaiseDeepLink(returned);

            Assert.AreEqual(BrowserResult.Returned, authTab.Task.Result.Result);
            Assert.AreEqual(returned, authTab.Task.Result.ReturnUrl);
            Assert.IsFalse(runtime.HasSubscribers);
        }

        [Test]
        public void Auth_tab_launcher_return_without_a_callback_resumes()
        {
            var runtime = new FakeReturnMonitorRuntime();
            var authTab = new AuthTabReturn(ReturnUrl, CancelUrl, runtime, UnexpectedFallback);

            runtime.RaiseFocus(false);
            runtime.RaiseFocus(true);
            Assert.IsFalse(authTab.Task.IsCompleted);
            runtime.RunDelayedAction();

            Assert.AreEqual(BrowserResult.Resumed, authTab.Task.Result.Result);
            Assert.IsFalse(runtime.HasSubscribers);
        }

        [Test]
        public void Auth_tab_helper_result_wins_and_stops_the_monitor()
        {
            var runtime = new FakeReturnMonitorRuntime();
            var authTab = new AuthTabReturn(ReturnUrl, CancelUrl, runtime, UnexpectedFallback);

            const string returned = ReturnUrl + "?request_id=req_4";
            authTab.OnHelperResult("RETURNED|" + returned);

            Assert.AreEqual(BrowserResult.Returned, authTab.Task.Result.Result);
            Assert.AreEqual(returned, authTab.Task.Result.ReturnUrl);
            Assert.IsFalse(runtime.HasSubscribers);
            runtime.RaiseDeepLink(CancelUrl);
            Assert.AreEqual(returned, authTab.Task.Result.ReturnUrl);
        }

        [Test]
        public void Auth_tab_cancel_yields_to_a_deep_link_during_the_grace_period()
        {
            var runtime = new FakeReturnMonitorRuntime();
            var authTab = new AuthTabReturn(ReturnUrl, CancelUrl, runtime, UnexpectedFallback);

            authTab.OnHelperResult("CANCELED|");
            Assert.AreEqual(AuthTabReturn.CancelGraceSeconds, runtime.DelayedSeconds);
            Assert.IsFalse(authTab.Task.IsCompleted);

            runtime.RaiseDeepLink(ReturnUrl);
            runtime.RunDelayedAction();

            Assert.AreEqual(BrowserResult.Returned, authTab.Task.Result.Result);
            Assert.AreEqual(ReturnUrl, authTab.Task.Result.ReturnUrl);
        }

        [Test]
        public void Auth_tab_cancel_settles_after_the_grace_period()
        {
            var runtime = new FakeReturnMonitorRuntime();
            var authTab = new AuthTabReturn(ReturnUrl, CancelUrl, runtime, UnexpectedFallback);

            authTab.OnHelperResult("CANCELED|");
            runtime.RunDelayedAction();

            Assert.AreEqual(BrowserResult.Canceled, authTab.Task.Result.Result);
            Assert.IsFalse(runtime.HasSubscribers);
        }

        [Test]
        public void Auth_tab_foreground_after_cancel_stays_canceled()
        {
            var runtime = new FakeReturnMonitorRuntime();
            var authTab = new AuthTabReturn(ReturnUrl, CancelUrl, runtime, UnexpectedFallback);

            authTab.OnHelperResult("CANCELED|");
            runtime.RaiseFocus(false);
            runtime.RaiseFocus(true);
            runtime.RunDelayedAction();

            Assert.AreEqual(BrowserResult.Canceled, authTab.Task.Result.Result);
        }

        [Test]
        public void Auth_tab_error_stops_the_monitor_before_the_fallback_starts()
        {
            var runtime = new FakeReturnMonitorRuntime();
            bool subscribedAtFallback = true;
            var fallbackOutcome = BrowserOutcome.Returned(ReturnUrl + "?request_id=req_5");
            var authTab = new AuthTabReturn(ReturnUrl, CancelUrl, runtime, () =>
            {
                subscribedAtFallback = runtime.HasSubscribers;
                return Task.FromResult(fallbackOutcome);
            });

            authTab.OnHelperResult("ERROR|register: missing class");

            Assert.IsTrue(authTab.Task.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(subscribedAtFallback);
            Assert.AreEqual(fallbackOutcome.ReturnUrl, authTab.Task.Result.ReturnUrl);
        }

        private static Task<BrowserOutcome> UnexpectedFallback()
        {
            throw new InvalidOperationException("the Custom Tabs fallback should not start");
        }

        private sealed class FakeReturnMonitorRuntime : IReturnMonitorRuntime
        {
            private Action<string> _deepLinkHandler;
            private Action<bool> _focusHandler;

            public int UnsubscribeCalls;
            public double DelayedSeconds;
            public Action DelayedAction;
            public bool HasSubscribers => _deepLinkHandler != null || _focusHandler != null;

            public void Subscribe(Action<string> deepLinkHandler, Action<bool> focusHandler)
            {
                _deepLinkHandler = deepLinkHandler;
                _focusHandler = focusHandler;
            }

            public void Unsubscribe(Action<string> deepLinkHandler, Action<bool> focusHandler)
            {
                UnsubscribeCalls++;
                if (_deepLinkHandler == deepLinkHandler) _deepLinkHandler = null;
                if (_focusHandler == focusHandler) _focusHandler = null;
            }

            public void DelayThen(double seconds, Action action)
            {
                DelayedSeconds = seconds;
                DelayedAction = action;
            }

            public void RaiseDeepLink(string url)
            {
                _deepLinkHandler?.Invoke(url);
            }

            public void RaiseFocus(bool foreground)
            {
                _focusHandler?.Invoke(foreground);
            }

            public void RunDelayedAction()
            {
                DelayedAction?.Invoke();
            }
        }
    }
}
