// M2C Checkout - WebGL launch/return shim.
//
// Opens the vendor checkout in a tab/popup-style browser surface (never a
// full-page redirect, which would tear down the running WebGL app) and waits for
// a same-origin return page to publish an origin-scoped, nonce-bound wake signal,
// then invokes the C# callback with the return URL. Popup launch mode can pre-open
// a blank surface before async auction creation, sever its opener, then navigate it
// once the checkout URL is known. If the surface closes without a signal, reports
// an ambiguous close so C# can reconcile through status polling instead of
// assuming cancel.
//
// Same-origin success_url / cancel_url pages read the request-scoped active nonce
// from localStorage key "m2c_checkout_active:<request_id>" and publish it with
// the return URL on BroadcastChannel "m2c_checkout" or request-scoped storage.
// Cross-origin, embedded, and degraded browser paths use status polling.
//
// VERIFY IN A BROWSER.

mergeInto(LibraryManager.library, {
  $M2CCheckoutWebGL: {
    popupFeatures: 'popup=yes,width=520,height=720,resizable=yes,scrollbars=yes',

    state: function () {
      return window.__m2cCheckoutWebGL || (window.__m2cCheckoutWebGL = {});
    },

    openWindow: function (url, mode) {
      if (mode === 2) {
        return window.open(url, '_blank', this.popupFeatures);
      }
      return window.open(url, '_blank');
    },

    openerIsSevered: function (win) {
      try {
        return win.opener === null;
      } catch (e) {
        return false;
      }
    },

    prepareWindow: function (win) {
      // Run the first sever attempt inside the trusted about:blank child. Some
      // browsers make opener read-only through the parent's WindowProxy even
      // though the child can still clear its own opener.
      try {
        win.document.open();
        win.document.write('<!doctype html><meta charset="utf-8"><script>try{window.opener=null}catch(e){}<\/script><title>M2C Checkout</title><body style="font:16px system-ui,sans-serif;margin:2rem;color:#1f2937">Opening checkout...</body>');
        win.document.close();
      } catch (e) {}
      if (this.openerIsSevered(win)) return true;
      try {
        win.opener = null;
      } catch (e) {}
      return this.openerIsSevered(win);
    },

    closeWindow: function (win) {
      if (!win) return;
      try {
        if (!win.closed) win.close();
      } catch (e) {}
    },

    openNoOpener: function (url, mode) {
      var features = mode === 2 ? this.popupFeatures + ',noopener' : 'noopener';
      try {
        // With noopener, conforming browsers return null even when the surface
        // opened. The caller therefore switches immediately to status polling.
        var win = window.open(url, '_blank', features);
        if (!win) return true;
        if (this.openerIsSevered(win)) return true;
        try {
          win.opener = null;
        } catch (e) {}
        if (this.openerIsSevered(win)) return true;
        this.closeWindow(win);
        return false;
      } catch (e) {
        return false;
      }
    },

    clearPreparedPoll: function (state) {
      if (!state.preparedPoll) return;
      clearInterval(state.preparedPoll);
      state.preparedPoll = 0;
    },

    isSameOriginUrl: function (rawUrl) {
      try {
        return new URL(rawUrl, window.location.href).origin === window.location.origin;
      } catch (e) {
        return false;
      }
    },

    returnsShareGameOrigin: function (returnUrl, cancelUrl) {
      if (!returnUrl || !this.isSameOriginUrl(returnUrl)) return false;
      return !cancelUrl || this.isSameOriginUrl(cancelUrl);
    },

    isEmbedded: function () {
      try {
        return window.top !== window;
      } catch (e) {
        return true;
      }
    },

    stripQueryAndFragment: function (rawUrl) {
      if (!rawUrl) return '';
      var cut = rawUrl.length;
      var query = rawUrl.indexOf('?');
      var fragment = rawUrl.indexOf('#');
      if (query >= 0 && query < cut) cut = query;
      if (fragment >= 0 && fragment < cut) cut = fragment;
      var value = rawUrl.substring(0, cut);
      while (value.length && value.charAt(value.length - 1) === '/') {
        value = value.substring(0, value.length - 1);
      }
      return value.toLowerCase();
    },

    matchesExpectedUrl: function (actual, expected) {
      var actualBase = this.stripQueryAndFragment(actual);
      var expectedBase = this.stripQueryAndFragment(expected);
      if (!actualBase || !expectedBase || actualBase.indexOf(expectedBase) !== 0) return false;
      return actualBase.length === expectedBase.length || actualBase.charAt(expectedBase.length) === '/';
    },

    requestKeyPart: function (requestId) {
      return encodeURIComponent(String(requestId || '').toLowerCase());
    }
  },

  M2CCheckoutPrepare__deps: ['$M2CCheckoutWebGL'],
  M2CCheckoutPrepare: function (launchMode) {
    var state = M2CCheckoutWebGL.state();

    if (state.prepared && !state.prepared.closed) return 1;
    M2CCheckoutWebGL.clearPreparedPoll(state);
    state.preparedClosed = false;
    state.prepared = M2CCheckoutWebGL.openWindow('about:blank', launchMode);
    if (!state.prepared) return 0;
    M2CCheckoutWebGL.prepareWindow(state.prepared);
    try {
      state.prepared.blur();
      window.focus();
    } catch (e) {}
    state.preparedPoll = setInterval(function () {
      if (state.prepared && state.prepared.closed) {
        state.preparedClosed = true;
        state.prepared = null;
        M2CCheckoutWebGL.clearPreparedPoll(state);
      }
    }, 250);
    return 1;
  },

  M2CCheckoutCancelPrepared__deps: ['$M2CCheckoutWebGL'],
  M2CCheckoutCancelPrepared: function () {
    var state = M2CCheckoutWebGL.state();
    M2CCheckoutWebGL.clearPreparedPoll(state);
    M2CCheckoutWebGL.closeWindow(state.prepared);
    state.prepared = null;
    state.preparedClosed = false;
  },

  M2CCheckoutOpen__deps: ['$M2CCheckoutWebGL'],
  M2CCheckoutOpen: function (urlPtr, returnUrlPtr, cancelUrlPtr, requestIdPtr, launchMode, onReturn) {
    var url = UTF8ToString(urlPtr);
    var returnUrl = returnUrlPtr ? UTF8ToString(returnUrlPtr) : '';
    var cancelUrl = cancelUrlPtr ? UTF8ToString(cancelUrlPtr) : '';
    var requestId = requestIdPtr ? UTF8ToString(requestIdPtr) : '';
    var requestIdNormalized = requestId.toLowerCase();
    var state = M2CCheckoutWebGL.state();
    var popup = null;
    var settled = false;
    var pollClosed = 0;
    var closeGrace = 0;
    var channel = null;
    var observedOpen = false;
    var checkoutOpenedAt = 0;
    var hostLostFocus = false;
    var returnlessFocusGrace = 0;
    var activeNonce = '';
    var keyPart = M2CCheckoutWebGL.requestKeyPart(requestId);
    var activeKey = 'm2c_checkout_active:' + keyPart;
    var returnKey = 'm2c_checkout_return:' + keyPart;
    var statusOnly = false;
    var launchedWithoutHandle = false;

    function createNonce() {
      try {
        if (!window.crypto || !window.crypto.getRandomValues) return '';
        var bytes = new Uint8Array(16);
        window.crypto.getRandomValues(bytes);
        var value = '';
        for (var i = 0; i < bytes.length; i++) value += ('0' + bytes[i].toString(16)).slice(-2);
        return value;
      } catch (e) {
        return '';
      }
    }

    function rememberActiveNonce() {
      try {
        localStorage.removeItem(returnKey);
        var serialized = JSON.stringify({
          request_id: requestId,
          nonce: activeNonce,
          expires_at: nowMs() + 2 * 60 * 60 * 1000
        });
        localStorage.setItem(activeKey, serialized);
        return localStorage.getItem(activeKey) === serialized;
      } catch (e) {
        return false;
      }
    }

    function forgetActiveNonce() {
      try {
        var raw = localStorage.getItem(activeKey);
        var current = raw ? JSON.parse(raw) : null;
        if (current && current.nonce === activeNonce &&
            String(current.request_id || '').toLowerCase() === requestIdNormalized) {
          localStorage.removeItem(activeKey);
          localStorage.removeItem(returnKey);
        }
      } catch (e) {}
    }

    function focusGameWindow() {
      if (launchMode !== 2) return;
      try {
        window.focus();
      } catch (e) {}
    }

    function readPopupReturnUrl() {
      if (!popup) return '';
      try {
        var href = popup.location && popup.location.href;
        if (M2CCheckoutWebGL.matchesExpectedUrl(href, returnUrl) || M2CCheckoutWebGL.matchesExpectedUrl(href, cancelUrl)) return href;
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
      if (resultUrl !== '__M2C_STATUS_ONLY__') focusGameWindow();
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
      forgetActiveNonce();
      var s = resultUrl || '';
      var size = lengthBytesUTF8(s) + 1;
      var buf = _malloc(size);
      stringToUTF8(s, buf, size);
      {{{ makeDynCall('vi', 'onReturn') }}}(buf);
      _free(buf);
    }

    function onReturnSignal(data) {
      if (!data || data.m2c !== 'return' || data.nonce !== activeNonce) return;
      if (String(data.request_id || '').toLowerCase() !== requestIdNormalized) return;
      var resultUrl = data.url || '';
      // BroadcastChannel and localStorage are origin-scoped. Requiring the URL
      // itself to share the game origin prevents a same-origin sender from
      // laundering an arbitrary cross-origin URL through that trusted channel.
      if (!M2CCheckoutWebGL.isSameOriginUrl(resultUrl)) return;
      if (!M2CCheckoutWebGL.matchesExpectedUrl(resultUrl, returnUrl) && !M2CCheckoutWebGL.matchesExpectedUrl(resultUrl, cancelUrl)) return;
      finish(resultUrl);
    }

    function onStorage(e) {
      if (!e || e.key !== returnKey || !e.newValue) return;
      try {
        onReturnSignal(JSON.parse(e.newValue));
      } catch (err) {}
    }

    statusOnly = !requestId ||
      !M2CCheckoutWebGL.returnsShareGameOrigin(returnUrl, cancelUrl) ||
      M2CCheckoutWebGL.isEmbedded();
    if (!statusOnly) {
      activeNonce = createNonce();
      if (!activeNonce || !rememberActiveNonce()) statusOnly = true;
    }

    if (!statusOnly) {
      window.addEventListener('storage', onStorage);
      window.addEventListener('blur', onHostBlur);
      window.addEventListener('focus', onHostVisibleOrFocused);
      try {
        document.addEventListener('visibilitychange', onVisibilityChange);
      } catch (e) {}
      try {
        if (window.BroadcastChannel) {
          channel = new BroadcastChannel('m2c_checkout');
          channel.onmessage = function (e) { onReturnSignal(e && e.data); };
        }
      } catch (e) {
        channel = null;
      }
    }

    function launchWithNoOpenerHandle() {
      M2CCheckoutWebGL.closeWindow(popup);
      popup = null;
      if (!M2CCheckoutWebGL.openNoOpener(url, launchMode)) return false;
      launchedWithoutHandle = true;
      statusOnly = true;
      return true;
    }

    function navigatePopup() {
      if (!popup) return false;
      if (!M2CCheckoutWebGL.openerIsSevered(popup) && !M2CCheckoutWebGL.prepareWindow(popup)) {
        return launchWithNoOpenerHandle();
      }
      try {
        popup.location.replace(url);
        return true;
      } catch (e) {
        return launchWithNoOpenerHandle();
      }
    }

    var prepared = state.prepared;
    var preparedWasClosed = state.preparedClosed || (prepared && prepared.closed);
    state.prepared = null;
    state.preparedClosed = false;
    M2CCheckoutWebGL.clearPreparedPoll(state);

    if (preparedWasClosed) {
      finish('__M2C_PREPARED_CLOSED__');
      return;
    } else if (prepared) {
      popup = prepared;
      if (!navigatePopup()) {
        finish('__M2C_POPUP_BLOCKED__');
        return;
      }
    } else {
      popup = M2CCheckoutWebGL.openWindow('about:blank', launchMode);
      if (!popup) {
        finish('__M2C_POPUP_BLOCKED__');
        return;
      }
      if (!navigatePopup()) {
        finish('__M2C_POPUP_BLOCKED__');
        return;
      }
    }

    if (!popup && !launchedWithoutHandle) {
      finish('__M2C_POPUP_BLOCKED__');
      return;
    }
    if (statusOnly) {
      finish('__M2C_STATUS_ONLY__');
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
