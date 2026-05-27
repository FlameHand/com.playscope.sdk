package com.playscope.sdk;

import android.app.ActivityManager;
import android.content.Context;

public final class PlayScopeNativeMetrics
{
    private PlayScopeNativeMetrics() {}

    public static long getFreeMemoryMb(Context ctx)
    {
        try
        {
            ActivityManager am = (ActivityManager) ctx.getSystemService(Context.ACTIVITY_SERVICE);
            if (am == null) { return 0L; }
            ActivityManager.MemoryInfo mi = new ActivityManager.MemoryInfo();
            am.getMemoryInfo(mi);
            return mi.availMem / (1024L * 1024L);
        }
        catch (Throwable t)
        {
            return 0L;
        }
    }
}
