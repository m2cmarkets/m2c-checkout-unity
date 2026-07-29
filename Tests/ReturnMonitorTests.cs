using System;
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
