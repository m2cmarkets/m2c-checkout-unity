using System;
using System.Threading.Tasks;
using UnityEngine;

namespace M2C.Checkout.Internal
{
    internal interface IReturnMonitorRuntime
    {
        void Subscribe(Action<string> deepLinkHandler, Action<bool> focusHandler);
        void Unsubscribe(Action<string> deepLinkHandler, Action<bool> focusHandler);
        void DelayThen(double seconds, Action action);
    }

    internal sealed class UnityReturnMonitorRuntime : IReturnMonitorRuntime
    {
        public static readonly UnityReturnMonitorRuntime Instance = new UnityReturnMonitorRuntime();

        private UnityReturnMonitorRuntime() { }

        public void Subscribe(Action<string> deepLinkHandler, Action<bool> focusHandler)
        {
            Application.deepLinkActivated += deepLinkHandler;
            M2CScheduler.Instance.AppFocusChanged += focusHandler;
        }

        public void Unsubscribe(Action<string> deepLinkHandler, Action<bool> focusHandler)
        {
            Application.deepLinkActivated -= deepLinkHandler;
            M2CScheduler.Instance.AppFocusChanged -= focusHandler;
        }

        public void DelayThen(double seconds, Action action)
        {
            M2CScheduler.Instance.DelayThen(seconds, action);
        }
    }

    internal sealed class ReturnMonitorSession
    {
        private readonly TaskCompletionSource<BrowserOutcome> _completion =
            new TaskCompletionSource<BrowserOutcome>();
        private readonly Action<BrowserOutcome> _beforeComplete;
        private Action _cleanup;

        internal ReturnMonitorSession(Action<BrowserOutcome> beforeComplete)
        {
            _beforeComplete = beforeComplete;
        }

        internal Task<BrowserOutcome> Task => _completion.Task;
        internal bool IsStopped { get; private set; }

        internal void SetCleanup(Action cleanup)
        {
            _cleanup = cleanup;
        }

        internal bool TrySetResult(BrowserOutcome outcome)
        {
            if (IsStopped) return false;
            Stop();
            try
            {
                _beforeComplete?.Invoke(outcome);
            }
            catch (Exception error)
            {
                return _completion.TrySetException(error);
            }
            return _completion.TrySetResult(outcome);
        }

        internal bool TrySetException(Exception error)
        {
            if (IsStopped) return false;
            Stop();
            return _completion.TrySetException(error);
        }

        internal void Stop()
        {
            if (IsStopped) return;
            IsStopped = true;
            _cleanup?.Invoke();
        }
    }

    /// <summary>
    /// Coordinates the deep-link and foreground signals shared by external browser
    /// surfaces. A bare resume is ambiguous, so it yields to a matching deep link
    /// briefly and then asks the checkout core to reconcile status.
    /// </summary>
    internal static class ReturnMonitor
    {
        private const double ReturnGraceSeconds = 0.5;

        public static Task<BrowserOutcome> Start(string returnUrl, string cancelUrl)
        {
            return Start(returnUrl, cancelUrl, UnityReturnMonitorRuntime.Instance);
        }

        internal static Task<BrowserOutcome> Start(
            string returnUrl,
            string cancelUrl,
            IReturnMonitorRuntime runtime)
        {
            return StartSession(returnUrl, cancelUrl, runtime, true, null).Task;
        }

        internal static ReturnMonitorSession StartInProcess(
            string returnUrl,
            string cancelUrl,
            Action<BrowserOutcome> beforeComplete = null)
        {
            return StartSession(
                returnUrl,
                cancelUrl,
                UnityReturnMonitorRuntime.Instance,
                false,
                beforeComplete);
        }

        internal static ReturnMonitorSession StartInProcess(
            string returnUrl,
            string cancelUrl,
            IReturnMonitorRuntime runtime,
            Action<BrowserOutcome> beforeComplete = null)
        {
            return StartSession(returnUrl, cancelUrl, runtime, false, beforeComplete);
        }

        private static ReturnMonitorSession StartSession(
            string returnUrl,
            string cancelUrl,
            IReturnMonitorRuntime runtime,
            bool reconcileOnForeground,
            Action<BrowserOutcome> beforeComplete)
        {
            var session = new ReturnMonitorSession(beforeComplete);
            Action<string> deepLinkHandler = null;
            Action<bool> focusHandler = null;
            bool backgrounded = false;

            session.SetCleanup(() => runtime.Unsubscribe(deepLinkHandler, focusHandler));

            deepLinkHandler = url =>
            {
                if (session.IsStopped
                    || !ReturnClassifier.IsConfiguredReturn(url, returnUrl, cancelUrl))
                    return;
                session.TrySetResult(BrowserOutcome.Returned(url));
            };

            focusHandler = foreground =>
            {
                if (session.IsStopped || !reconcileOnForeground) return;
                if (!foreground)
                {
                    backgrounded = true;
                    return;
                }
                if (!backgrounded) return;

                runtime.DelayThen(ReturnGraceSeconds, () =>
                {
                    if (session.IsStopped) return;
                    session.TrySetResult(BrowserOutcome.Resumed);
                });
            };

            runtime.Subscribe(deepLinkHandler, focusHandler);
            return session;
        }
    }
}
