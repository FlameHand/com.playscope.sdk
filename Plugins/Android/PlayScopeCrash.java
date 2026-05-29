package com.playscope.sdk;

/**
 * JNI bridge to libplayscope_crash.so — native signal handler that
 * captures SIGSEGV/SIGABRT/SIGBUS/SIGILL/SIGFPE and writes a JSON crash
 * record to {crashDir}/{sessionId}.json before letting the OS produce
 * its tombstone.
 *
 * Idempotent. install() may be called multiple times; subsequent calls
 * only refresh the session id. All Throwable from native calls is
 * swallowed — failing to install crash capture must never bring down
 * the host app's SDK init.
 */
public final class PlayScopeCrash {

    private static final String TAG = "PlayScope/Crash";

    private static volatile boolean sLoaded = false;
    private static volatile boolean sInstalled = false;

    private PlayScopeCrash() {}

    public static synchronized void install(String crashDir, String sessionId) {
        if (sInstalled) {
            updateSessionId(sessionId);
            return;
        }
        if (crashDir == null || sessionId == null) {
            android.util.Log.w(TAG, "install: null arg crashDir=" + crashDir + " sid=" + sessionId);
            return;
        }
        if (!sLoaded) {
            try {
                System.loadLibrary("playscope_crash");
                sLoaded = true;
            } catch (Throwable t) {
                android.util.Log.e(TAG, "loadLibrary failed", t);
                return;
            }
        }
        try {
            int rc = nativeInstall(crashDir, sessionId);
            if (rc == 0) {
                sInstalled = true;
                android.util.Log.i(TAG, "installed dir=" + crashDir + " sid=" + sessionId);
            } else {
                android.util.Log.w(TAG, "nativeInstall returned " + rc);
            }
        } catch (Throwable t) {
            android.util.Log.e(TAG, "nativeInstall threw", t);
        }
    }

    public static void updateSessionId(String sessionId) {
        if (!sInstalled || sessionId == null) {
            return;
        }
        try {
            nativeUpdateSessionId(sessionId);
        } catch (Throwable ignored) { }
    }

    private static native int nativeInstall(String crashDir, String sessionId);
    private static native void nativeUpdateSessionId(String sessionId);
}
