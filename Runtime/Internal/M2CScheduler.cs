using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;

namespace M2C.Checkout.Internal
{
    /// <summary>
    /// A hidden, persistent MonoBehaviour that turns a delay into an awaitable
    /// backed by a Unity coroutine. Using a coroutine (not <c>Task.Delay</c>) keeps
    /// the poll loop working on WebGL, which is single-threaded and has no timer
    /// thread. Created lazily on first use, on the main thread. Also relays the
    /// app's foreground transitions so in-app-browser return paths can fall back to
    /// polling when the user comes back without a deep-link redirect.
    /// </summary>
    internal sealed class M2CScheduler : MonoBehaviour
    {
        private static M2CScheduler _instance;
        private bool _foreground = true;

        /// <summary>
        /// Raised on the main thread when the app's foreground state changes
        /// (true = returned to the foreground, false = backgrounded). The Android /
        /// system-browser return paths subscribe to this to detect a return that did
        /// not come through a deep link.
        /// </summary>
        public event Action<bool> AppFocusChanged;

        /// <summary>
        /// Raised on the Unity main thread with the Android Auth Tab result, bridged
        /// from the native M2CAuthTabActivity via UnitySendMessage. Payload is one of
        /// "RETURNED|&lt;url&gt;", "DISMISSED|", or "ERROR|&lt;message&gt;".
        /// </summary>
        public event Action<string> AuthTabResult;

        public static M2CScheduler Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("M2CCheckoutScheduler") { hideFlags = HideFlags.HideAndDontSave };
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<M2CScheduler>();
                }
                return _instance;
            }
        }

        public Task Delay(double seconds)
        {
            var tcs = new TaskCompletionSource<bool>();
            StartCoroutine(DelayRoutine(seconds, tcs));
            return tcs.Task;
        }

        /// <summary>Invoke an action on the main thread after a real-time delay.</summary>
        public void DelayThen(double seconds, Action action)
        {
            if (action == null) return;
            StartCoroutine(DelayThenRoutine(seconds, action));
        }

        // Bridge target for the native Android Auth Tab helper, invoked via
        // UnityPlayer.UnitySendMessage("M2CCheckoutScheduler", "OnM2CAuthTabResult", payload).
        // The GameObject and method names are a contract with M2CAuthTabActivity.java.
        public void OnM2CAuthTabResult(string payload)
        {
            AuthTabResult?.Invoke(payload ?? string.Empty);
        }

        private static IEnumerator DelayRoutine(double seconds, TaskCompletionSource<bool> tcs)
        {
            if (seconds > 0) yield return new WaitForSecondsRealtime((float)seconds);
            tcs.TrySetResult(true);
        }

        private static IEnumerator DelayThenRoutine(double seconds, Action action)
        {
            if (seconds > 0) yield return new WaitForSecondsRealtime((float)seconds);
            action();
        }

        // OnApplicationPause is the reliable Android lifecycle signal: the Custom Tab
        // (or system browser) opening pauses the Unity activity, and returning to the
        // app resumes it. Relay it as a foreground bool so a browser's return path can
        // poll when the user comes back without a deep link.
        private void OnApplicationPause(bool paused)
        {
            SetForeground(!paused);
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            SetForeground(hasFocus);
        }

        private void SetForeground(bool foreground)
        {
            if (_foreground == foreground) return;
            _foreground = foreground;
            AppFocusChanged?.Invoke(foreground);
        }
    }
}
