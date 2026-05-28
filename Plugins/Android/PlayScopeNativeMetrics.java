package com.playscope.sdk;

import android.app.ActivityManager;
import android.content.Context;
import android.os.StatFs;

import java.io.File;

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

    public static long getAvailableDiskMb(Context ctx)
    {
        try
        {
            File dir = ctx.getFilesDir();
            if (dir == null) { return -1L; }
            StatFs stat = new StatFs(dir.getAbsolutePath());
            long bytes = stat.getAvailableBlocksLong() * stat.getBlockSizeLong();
            return bytes / (1024L * 1024L);
        }
        catch (Throwable t)
        {
            return -1L;
        }
    }
}
