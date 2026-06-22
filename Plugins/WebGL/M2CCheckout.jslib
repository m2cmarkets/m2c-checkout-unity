// M2C Checkout - WebGL launch/return shim.
//
// Opens the vendor checkout in a tab/popup-style browser surface (never a
// full-page redirect, which would tear down the running WebGL app) and waits for
// the return page to postMessage back to the opener, then invokes the C# callback
// with the return URL. Popup launch mode can pre-open a blank surface before
// async auction creation, then navigate it once the checkout URL is known. If the
// surface closes without a message, reports an ambiguous close so C# can reconcile
// through status polling instead of assuming cancel.
//
// The merchant's success_url / cancel_url page must post a message to its opener.
// Same-origin pages can also publish the same payload on BroadcastChannel
// "m2c_checkout" or localStorage key "m2c_checkout_return":
//   window.opener && window.opener.postMessage({ m2c: 'return', url: location.href }, '*');
// (Scope the target origin to your app's origin in production instead of '*'.)
//
// VERIFY IN A BROWSER.

mergeInto(LibraryManager.library, {
  M2CCheckoutPrepare: function (launchMode) {
    var state = window.__m2cCheckoutWebGL || (window.__m2cCheckoutWebGL = {});

    function openCheckoutWindow(url, mode) {
      if (mode === 2) {
        return window.open(url, 'm2c_checkout', 'popup=yes,width=520,height=720,resizable=yes,scrollbars=yes');
      }
      return window.open(url, '_blank');
    }

    function writePlaceholder(win) {
      try {
        win.document.open();
        win.document.write('<!doctype html><title>M2C Checkout</title><body style="font:16px system-ui,sans-serif;margin:2rem;color:#1f2937">Opening checkout...<script>function m2cFocusOpener(){try{if(window.opener&&!window.opener.closed)window.opener.focus();}catch(e){}try{window.blur();}catch(e){}}window.addEventListener("message",function(e){if(!e.data||e.data.m2c!=="navigate"||!e.data.url)return;window.location.replace(e.data.url);});m2cFocusOpener();setTimeout(m2cFocusOpener,0);setTimeout(m2cFocusOpener,100);<\/script></body>');
        win.document.close();
      } catch (e) {}
    }

    if (state.prepared && !state.prepared.closed) return 1;
    if (state.preparedPoll) {
      clearInterval(state.preparedPoll);
      state.preparedPoll = 0;
    }
    state.preparedClosed = false;
    state.prepared = openCheckoutWindow('about:blank', launchMode);
    if (!state.prepared) return 0;
    writePlaceholder(state.prepared);
    try {
      state.prepared.blur();
      window.focus();
    } catch (e) {}
    state.preparedPoll = setInterval(function () {
      if (state.prepared && state.prepared.closed) {
        state.preparedClosed = true;
        state.prepared = null;
        clearInterval(state.preparedPoll);
        state.preparedPoll = 0;
      }
    }, 250);
    return 1;
  },

  M2CCheckoutCancelPrepared: function () {
    var state = window.__m2cCheckoutWebGL || (window.__m2cCheckoutWebGL = {});
    if (state.preparedPoll) {
      clearInterval(state.preparedPoll);
      state.preparedPoll = 0;
    }
    if (state.prepared && !state.prepared.closed) {
      try {
        state.prepared.close();
      } catch (e) {}
    }
    state.prepared = null;
    state.preparedClosed = false;
  },

  M2CCheckoutOpen: function (urlPtr, returnUrlPtr, cancelUrlPtr, launchMode, onReturn) {
    var url = UTF8ToString(urlPtr);
    var returnUrl = returnUrlPtr ? UTF8ToString(returnUrlPtr) : '';
    var cancelUrl = cancelUrlPtr ? UTF8ToString(cancelUrlPtr) : '';
    var state = window.__m2cCheckoutWebGL || (window.__m2cCheckoutWebGL = {});
    var popup = null;
    var settled = false;
    var pollClosed = 0;
    var closeGrace = 0;
    var channel = null;
    var observedOpen = false;
    var checkoutOpenedAt = 0;
    var hostLostFocus = false;
    var returnlessFocusGrace = 0;

    function openCheckoutWindow(openUrl, mode) {
      if (mode === 2) {
        return window.open(openUrl, 'm2c_checkout', 'popup=yes,width=520,height=720,resizable=yes,scrollbars=yes');
      }
      return window.open(openUrl, '_blank');
    }

    function focusGameWindow() {
      if (launchMode !== 2) return;
      try {
        window.focus();
      } catch (e) {}
    }

    function matchesExpectedUrl(actual, expected) {
      if (!actual || !expected) return false;
      return actual === expected ||
        actual.indexOf(expected + '?') === 0 ||
        actual.indexOf(expected + '&') === 0 ||
        actual.indexOf(expected + '#') === 0 ||
        actual.indexOf(expected + '/') === 0;
    }

    function readPopupReturnUrl() {
      if (!popup) return '';
      try {
        var href = popup.location && popup.location.href;
        if (matchesExpectedUrl(href, returnUrl) || matchesExpectedUrl(href, cancelUrl)) return href;
      } catch (e) {}
      return '';
    }

    function nowMs() {
      return Date.now ? Date.now() : new Date().getTime();
    }

    function markCheckoutOpened() {
      checkoutOpenedAt = nowMs();
      try {
        if (document.hasFocus && !document.hasFocus()) hostLostFocus = true;
      } catch (e) {}
    }

    function onHostBlur() {
      hostLostFocus = true;
    }

    function onHostVisibleOrFocused() {
      if (settled || !hostLostFocus || !checkoutOpenedAt || returnlessFocusGrace) return;
      var waitMs = Math.max(0, 500 - (nowMs() - checkoutOpenedAt));
      returnlessFocusGrace = setTimeout(function () {
        returnlessFocusGrace = 0;
        if (settled || !hostLostFocus || !checkoutOpenedAt) return;
        var popupReturnUrl = readPopupReturnUrl();
        if (popupReturnUrl) {
          finish(popupReturnUrl);
          return;
        }
        finish('__M2C_POPUP_CLOSED__');
      }, waitMs);
    }

    function onVisibilityChange() {
      if (document.hidden) {
        onHostBlur();
      } else {
        onHostVisibleOrFocused();
      }
    }

    function hostIsActive() {
      try {
        if (document.hidden) return false;
      } catch (e) {}
      try {
        return !document.hasFocus || document.hasFocus();
      } catch (e) {
        return true;
      }
    }

    function finish(resultUrl) {
      if (settled) return;
      settled = true;
      focusGameWindow();
      window.removeEventListener('message', onMessage);
      window.removeEventListener('storage', onStorage);
      window.removeEventListener('blur', onHostBlur);
      window.removeEventListener('focus', onHostVisibleOrFocused);
      try {
        document.removeEventListener('visibilitychange', onVisibilityChange);
      } catch (e) {}
      clearInterval(pollClosed);
      clearTimeout(closeGrace);
      clearTimeout(returnlessFocusGrace);
      if (channel) {
        try {
          channel.close();
        } catch (e) {}
        channel = null;
      }
      var s = resultUrl || '';
      var size = lengthBytesUTF8(s) + 1;
      var buf = _malloc(size);
      stringToUTF8(s, buf, size);
      {{{ makeDynCall('vi', 'onReturn') }}}(buf);
      _free(buf);
    }

    function onMessage(e) {
      if (!e.data || e.data.m2c !== 'return') return;
      finish(e.data.url || '');
    }

    function onStorage(e) {
      if (!e || e.key !== 'm2c_checkout_return' || !e.newValue) return;
      try {
        onMessage({ data: JSON.parse(e.newValue) });
      } catch (err) {}
    }

    window.addEventListener('message', onMessage);
    window.addEventListener('storage', onStorage);
    window.addEventListener('blur', onHostBlur);
    window.addEventListener('focus', onHostVisibleOrFocused);
    try {
      document.addEventListener('visibilitychange', onVisibilityChange);
    } catch (e) {}
    try {
      if (window.BroadcastChannel) {
        channel = new BroadcastChannel('m2c_checkout');
        channel.onmessage = onMessage;
      }
    } catch (e) {
      channel = null;
    }

    var preparedWasClosed = state.prepared && state.prepared.closed;
    if (state.prepared && !state.prepared.closed) {
      popup = state.prepared;
      state.prepared = null;
      state.preparedClosed = false;
      if (state.preparedPoll) {
        clearInterval(state.preparedPoll);
        state.preparedPoll = 0;
      }
      try {
        popup.postMessage({ m2c: 'navigate', url: url }, '*');
        popup.location.href = url;
      } catch (e) {
        finish('__M2C_POPUP_BLOCKED__');
        return;
      }
    } else if (state.preparedClosed || preparedWasClosed) {
      state.preparedClosed = false;
      state.prepared = null;
      if (state.preparedPoll) {
        clearInterval(state.preparedPoll);
        state.preparedPoll = 0;
      }
      finish('__M2C_PREPARED_CLOSED__');
      return;
    } else {
      state.prepared = null;
      if (state.preparedPoll) {
        clearInterval(state.preparedPoll);
        state.preparedPoll = 0;
      }
      popup = openCheckoutWindow(url, launchMode);
    }

    if (!popup) {
      finish('__M2C_POPUP_BLOCKED__');
      return;
    }
    markCheckoutOpened();

    pollClosed = setInterval(function () {
      var popupReturnUrl = readPopupReturnUrl();
      if (popupReturnUrl) {
        finish(popupReturnUrl);
        return;
      }
      if (popup && !popup.closed) {
        observedOpen = true;
        return;
      }
      if (!popup || closeGrace) return;
      if (!hostLostFocus || !hostIsActive()) return;
      if (launchMode === 2 && !observedOpen) return;
      closeGrace = setTimeout(function () {
        closeGrace = 0;
        var popupReturnUrl = readPopupReturnUrl();
        if (popupReturnUrl) {
          finish(popupReturnUrl);
          return;
        }
        if (hostLostFocus && hostIsActive() && (!popup || (popup.closed && (launchMode !== 2 || observedOpen)))) finish('__M2C_POPUP_CLOSED__');
      }, 1000);
    }, 500);
  }
});
