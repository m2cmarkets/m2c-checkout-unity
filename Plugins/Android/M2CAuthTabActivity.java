package com.m2c.checkout;

import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;

import androidx.activity.ComponentActivity;
import androidx.activity.result.ActivityResultLauncher;
import androidx.annotation.Nullable;
import androidx.browser.auth.AuthTabIntent;

import com.unity3d.player.UnityPlayer;

/**
 * Transparent helper that hosts the Auth Tab result launcher. An Auth Tab returns
 * its outcome only through an ActivityResult launcher registered on an AndroidX
 * ComponentActivity (registered before STARTED) - Unity's player activity is not
 * one - so this stands in: it registers the launcher in onCreate, launches the Auth
 * Tab for the checkout URL + return scheme, bridges the result back to Unity, and
 * finishes. It draws no UI (translucent theme); the Auth Tab / Custom Tab is what
 * the user sees.
 *
 * Why Auth Tab instead of a plain Custom Tab: no minimize button, and a real result
 * callback instead of inferring the return from app focus - so a minimize, an OTP /
 * 3-D Secure bounce, or a backgrounded tab no longer trip a false return. On
 * browsers that do not support Auth Tab natively it auto-falls-back to a standard
 * Custom Tab but still delivers the result through this launcher.
 *
 * Bridge contract (keep in sync with AndroidAuthTabBrowser.cs and M2CScheduler.cs):
 *   GameObject "M2CCheckoutScheduler", method "OnM2CAuthTabResult",
 *   payload "RETURNED|<url>" | "CANCELED|" | "RESUMED|" | "ERROR|<message>".
 *
 * VERIFY ON DEVICE: the androidx.browser.auth symbols below are pinned by the
 * androidx.browser:browser:1.9.0 build, not by tooling here. If they do not resolve
 * in the generated Android Studio project, adjust to match the installed library:
 *   - result type: AuthTabIntent.AuthResult (nested)
 *   - members:     result.resultCode / result.resultUri  (may be getResultCode() / getResultUri())
 *   - constants:   AuthTabIntent.RESULT_OK / AuthTabIntent.RESULT_CANCELED
 *   - launch:      build().launch(launcher, Uri, String scheme)
 */
public final class M2CAuthTabActivity extends ComponentActivity {

    private static final String GAME_OBJECT = "M2CCheckoutScheduler";
    private static final String METHOD = "OnM2CAuthTabResult";

    private ActivityResultLauncher<Intent> launcher;

    @Override
    protected void onCreate(@Nullable Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        // Must register before the activity reaches STARTED.
        try {
            launcher = AuthTabIntent.registerActivityResultLauncher(this, this::onAuthResult);
        } catch (Throwable t) {
            send("ERROR|register: " + t.getMessage());
            finish();
            return;
        }

        // A recreation (config change / process restart) must not relaunch the tab;
        // the in-flight launch still delivers its result to the launcher.
        if (savedInstanceState != null) {
            return;
        }

        String url = getIntent().getStringExtra("m2c_url");
        String scheme = getIntent().getStringExtra("m2c_scheme");
        if (url == null || scheme == null) {
            send("ERROR|missing extras");
            finish();
            return;
        }

        try {
            new AuthTabIntent.Builder().build().launch(launcher, Uri.parse(url), scheme);
        } catch (Throwable t) {
            send("ERROR|launch: " + t.getMessage());
            finish();
        }
    }

    private void onAuthResult(AuthTabIntent.AuthResult result) {
        if (result != null
                && result.resultCode == AuthTabIntent.RESULT_OK
                && result.resultUri != null) {
            send("RETURNED|" + result.resultUri.toString());
        } else if (result != null && result.resultCode == AuthTabIntent.RESULT_CANCELED) {
            // User closed the Auth Tab without completing - a reliable browser-cancel
            // signal. The C# side lets already-visible backend terminal status win.
            send("CANCELED|");
        } else {
            // Verification failed/timed out, or an unknown code: a return we could not
            // confirm. Ambiguous - reconcile via status rather than calling it a cancel.
            send("RESUMED|");
        }
        finish();
    }

    private static void send(String payload) {
        try {
            UnityPlayer.UnitySendMessage(GAME_OBJECT, METHOD, payload);
        } catch (Throwable ignored) {
            // Unity not loaded - cannot happen while the game is running.
        }
    }
}
