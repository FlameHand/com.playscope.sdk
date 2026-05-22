package com.playscope.sdk;

import android.app.Activity;
import android.app.Application;
import android.os.Bundle;
import android.util.Log;

import java.io.File;
import java.io.FileOutputStream;
import java.io.OutputStreamWriter;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.TimeZone;

/**
 * Java side of the lifecycle hook. Installed once from C# on SDK init via
 * {@link #install(Activity, String)}. Mirrors precise intent signals back
 * to a file the C# SessionRecovery reads on the next launch:
 *
 *   onActivityDestroyed(isFinishing=true && !isChangingConfigurations) →
 *     writes state=user_close, intent=true. This is the unambiguous
 *     "the user closed the app" signal — covers swipe-from-recents on
 *     Android 11+ where the system DOES route through onDestroy, and the
 *     back-button-to-exit case.
 *
 * Pure Java, no NDK. Compiled into the consumer's APK by Unity's Gradle
 * pipeline. No external dependencies beyond the Android SDK.
 *
 * The class is null- and exception-safe by design: any I/O failure is
 * swallowed silently so we never blow up the host app in a callback
 * that fires during teardown.
 */
public final class PlayScopeLifecycle implements Application.ActivityLifecycleCallbacks {

    private static final String TAG = "PlayScope/Lifecycle";

    // Singleton — install() is idempotent.
    private static volatile PlayScopeLifecycle sInstance;

    private String mLifecyclePath;

    /**
     * Install lifecycle callbacks. Called once from C# during SDK init.
     *
     * @param activity      the host Unity activity (UnityPlayer.currentActivity)
     * @param lifecyclePath absolute filesystem path the hook should write
     *                      its intent file to — typically
     *                      {persistentDataPath}/PlayScope/current_session/session.lifecycle
     */
    public static synchronized void install(Activity activity, String lifecyclePath) {
        if (sInstance != null) {
            // Already installed. Just refresh the path in case Initialize ran twice.
            sInstance.mLifecyclePath = lifecyclePath;
            Log.i(TAG, "install() called again — path refreshed: " + lifecyclePath);
            return;
        }
        if (activity == null) {
            Log.w(TAG, "install() FAILED: activity is null");
            return;
        }
        Application app = activity.getApplication();
        if (app == null) {
            Log.w(TAG, "install() FAILED: activity.getApplication() is null");
            return;
        }

        PlayScopeLifecycle hook = new PlayScopeLifecycle();
        hook.mLifecyclePath = lifecyclePath;
        try {
            app.registerActivityLifecycleCallbacks(hook);
            sInstance = hook;
            Log.i(TAG, "install() OK: ActivityLifecycleCallbacks registered. " +
                       "Lifecycle file path: " + lifecyclePath);
        } catch (Throwable t) {
            // Some host apps wrap getApplication() in ways that throw —
            // never let that bring down the SDK init.
            Log.e(TAG, "install() FAILED: registerActivityLifecycleCallbacks threw", t);
        }
    }

    /** Test-only uninstaller. Production code never calls this. */
    public static synchronized void uninstall(Activity activity) {
        if (sInstance == null || activity == null) return;
        Application app = activity.getApplication();
        if (app == null) return;
        try {
            app.unregisterActivityLifecycleCallbacks(sInstance);
        } catch (Throwable ignored) { }
        sInstance = null;
    }

    @Override public void onActivityCreated(Activity activity, Bundle savedInstanceState) { }
    @Override public void onActivityStarted(Activity activity) { }
    @Override public void onActivityResumed(Activity activity) { }
    @Override public void onActivityPaused(Activity activity) { }
    @Override public void onActivityStopped(Activity activity) { }
    @Override public void onActivitySaveInstanceState(Activity activity, Bundle outState) { }

    @Override
    public void onActivityDestroyed(Activity activity) {
        if (activity == null) return;
        try {
            boolean finishing = activity.isFinishing();
            boolean changingConfig = activity.isChangingConfigurations();
            Log.i(TAG, "onActivityDestroyed: " + activity.getClass().getSimpleName() +
                       " isFinishing=" + finishing + " isChangingConfigurations=" + changingConfig);
            // isFinishing() = the activity was told to finish (user pressed
            // back from the root, called finish(), OR Android routed the
            // recents-tray swipe through onDestroy).
            // isChangingConfigurations() = orientation change / locale
            // change / etc — we DON'T want to count that as user-close.
            if (finishing && !changingConfig) {
                writeIntent("user_close");
            }
        } catch (Throwable t) {
            // Never throw from a lifecycle callback.
            Log.e(TAG, "onActivityDestroyed threw", t);
        }
    }

    private void writeIntent(String state) {
        final String path = mLifecyclePath;
        if (path == null) {
            Log.w(TAG, "writeIntent: mLifecyclePath is null — install() may have failed");
            return;
        }

        SimpleDateFormat fmt = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.US);
        fmt.setTimeZone(TimeZone.getTimeZone("UTC"));
        String json = "{\"state\":\"" + state + "\",\"ts\":\"" + fmt.format(new Date()) + "\",\"intent\":true}";

        File f = new File(path);
        File tmp = new File(path + ".tmp");
        try {
            File parent = f.getParentFile();
            if (parent != null && !parent.exists()) parent.mkdirs();

            try (FileOutputStream fos = new FileOutputStream(tmp);
                 OutputStreamWriter w = new OutputStreamWriter(fos, StandardCharsets.UTF_8)) {
                w.write(json);
            }
            // Atomic-ish rename so a crash mid-write can't leave half a file
            // — File.renameTo is best-effort but it's all we have without
            // requiring a particular Android API level.
            if (f.exists()) f.delete();
            boolean renamed = tmp.renameTo(f);
            Log.i(TAG, "writeIntent: wrote state=" + state + " to " + path +
                       " (rename=" + renamed + ")");
        } catch (Throwable t) {
            // Best-effort. SessionRecovery on the next launch will fall
            // back to the C#-written lifecycle state.
            Log.e(TAG, "writeIntent: failed to write " + path, t);
            try { if (tmp.exists()) tmp.delete(); } catch (Throwable ignored) { }
        }
    }
}
