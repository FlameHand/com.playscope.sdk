// PlayScope iOS lifecycle hook.
//
// Subscribes to UIApplicationWillTerminateNotification so that when iOS is
// about to kill the process (the user swiped from the app switcher, or
// the OS is about to terminate the app cleanly), we can mirror an intent
// signal to disk for SessionRecovery to pick up on the next launch.
//
// Note: iOS does NOT always deliver UIApplicationWillTerminateNotification —
// in particular, it's NOT delivered when the OS jettisons a backgrounded
// app for low memory. That's the explicit design of iOS background-kill
// (apps are supposed to be re-entrant from saved state). For those cases
// we fall back to the C#-side lifecycle state ("background") — which the
// dashboard then labels as background_kill, intentionally not counted as
// a crash.
//
// Pure Objective-C++ — no PLCrashReporter dependency, no symbol upload
// requirement. PLCrashReporter for SIGSEGV / SIGABRT capture is a
// separate epic tracked in NATIVE_CRASH_AND_LIFECYCLE_ROADMAP.md.

#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#include <string>

static id sObserver = nil;
static std::string sLifecyclePath;

static void WriteIntentFile() {
    if (sLifecyclePath.empty()) {
        NSLog(@"[PlayScope/Lifecycle] WriteIntentFile skipped: empty path (install may have failed)");
        return;
    }
    @autoreleasepool {
        NSString *path = [NSString stringWithUTF8String:sLifecyclePath.c_str()];

        // Match the format C# writes: ISO-8601 UTC with milliseconds + flat JSON.
        NSDateFormatter *fmt = [[NSDateFormatter alloc] init];
        fmt.dateFormat = @"yyyy-MM-dd'T'HH:mm:ss.SSS'Z'";
        fmt.timeZone = [NSTimeZone timeZoneWithAbbreviation:@"UTC"];
        fmt.locale = [NSLocale localeWithLocaleIdentifier:@"en_US_POSIX"];

        NSString *json = [NSString stringWithFormat:
            @"{\"state\":\"user_close\",\"ts\":\"%@\",\"intent\":true}",
            [fmt stringFromDate:[NSDate date]]];

        // Best-effort atomic write — NSData writeToFile:atomically: writes
        // to a temp file and renames. Any failure is swallowed; the C#
        // lifecycle file will still be available as the fallback signal.
        NSData *bytes = [json dataUsingEncoding:NSUTF8StringEncoding];
        BOOL ok = [bytes writeToFile:path atomically:YES];
        NSLog(@"[PlayScope/Lifecycle] WriteIntentFile: wrote state=user_close to %@ (ok=%d)", path, ok);
    }
}

extern "C" {

/// Installs the lifecycle observer. Called once from C# during SDK init.
/// The path is the absolute filesystem location SessionFiles.WriteLifecycleState
/// uses on the C# side — keeping them in sync lets the recovery code read
/// either source.
void _playscope_install_ios_lifecycle(const char *lifecyclePath) {
    if (lifecyclePath == nullptr) {
        NSLog(@"[PlayScope/Lifecycle] install FAILED: lifecyclePath is null");
        return;
    }
    sLifecyclePath = lifecyclePath;
    if (sObserver != nil) {
        NSLog(@"[PlayScope/Lifecycle] install called again — path refreshed: %s", lifecyclePath);
        return; // idempotent — already installed
    }

    @autoreleasepool {
        NSOperationQueue *queue = [NSOperationQueue mainQueue];
        sObserver = [[NSNotificationCenter defaultCenter]
            addObserverForName:UIApplicationWillTerminateNotification
                        object:nil
                         queue:queue
                    usingBlock:^(NSNotification * _Nonnull note) {
            NSLog(@"[PlayScope/Lifecycle] UIApplicationWillTerminateNotification fired");
            WriteIntentFile();
        }];
        NSLog(@"[PlayScope/Lifecycle] install OK: WillTerminate observer registered. Path: %s", lifecyclePath);
    }
}

/// Test-only — uninstall the observer. Production code never calls this.
void _playscope_uninstall_ios_lifecycle(void) {
    if (sObserver == nil) return;
    @autoreleasepool {
        [[NSNotificationCenter defaultCenter] removeObserver:sObserver];
        sObserver = nil;
    }
}

} // extern "C"
