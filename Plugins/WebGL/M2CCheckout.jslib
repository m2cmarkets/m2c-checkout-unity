// M2C Checkout - WebGL launch/return shim.
//
// Opens the vendor checkout in a popup (never a full-page redirect, which would
// tear down the running WebGL app) and waits for the return page to postMessage
// back to the opener, then invokes the C# callback with the return URL. If the
// popup is closed without a message, reports an ambiguous close so C# can
// reconcile through status polling instead of assuming cancel.
//
// The merchant's success_url / cancel_url page must post a message to its opener:
//   window.opener && window.opener.postMessage({ m2c: 'return', url: location.href }, '*');
// (Scope the target origin to your app's origin in production instead of '*'.)
//
// VERIFY IN A BROWSER.

mergeInto(LibraryManager.library, {
  M2CCheckoutOpen: function (urlPtr, onReturn) {
    var url = UTF8ToString(urlPtr);
    var popup = window.open(url, '_blank');
    var settled = false;
    var pollClosed = 0;

    function finish(returnUrl) {
      if (settled) return;
      settled = true;
      window.removeEventListener('message', onMessage);
      clearInterval(pollClosed);
      var s = returnUrl || '';
      var size = lengthBytesUTF8(s) + 1;
      var buf = _malloc(size);
      stringToUTF8(s, buf, size);
      {{{ makeDynCall('vi', 'onReturn') }}}(buf);
      _free(buf);
    }

    function onMessage(e) {
      if (popup && e.source !== popup) return;
      if (!e.data || e.data.m2c !== 'return') return;
      finish(e.data.url || '');
    }
    window.addEventListener('message', onMessage);

    if (!popup) {
      finish('__M2C_POPUP_BLOCKED__');
      return;
    }

    pollClosed = setInterval(function () {
      if (popup && popup.closed) finish('__M2C_POPUP_CLOSED__');
    }, 500);
  }
});
