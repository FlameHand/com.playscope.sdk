#import <Foundation/Foundation.h>
#import <TargetConditionals.h>
#import <mach/mach.h>
#import <mach/mach_host.h>

extern "C"
{
    long PlayScopeGetFreeMemoryMb(void)
    {
#if TARGET_OS_SIMULATOR
        // Simulator shares the host Mac's kernel; host_statistics64 would
        // return the developer's macOS free RAM, not the simulated app's.
        // Skip entirely.
        return -1;
#else
        @try
        {
            mach_port_t host = mach_host_self();
            long result = -1;
            vm_size_t pageSize = 0;
            if (host_page_size(host, &pageSize) == KERN_SUCCESS && pageSize != 0)
            {
                vm_statistics64_data_t vmStats;
                mach_msg_type_number_t count = HOST_VM_INFO64_COUNT;
                if (host_statistics64(host, HOST_VM_INFO64, (host_info64_t)&vmStats, &count) == KERN_SUCCESS)
                {
                    // free + inactive is the closest analogue to "available" on iOS —
                    // matches what Xcode's memory gauge counts as free-ish.
                    uint64_t bytes = ((uint64_t)vmStats.free_count + (uint64_t)vmStats.inactive_count) * (uint64_t)pageSize;
                    result = (long)(bytes / (1024UL * 1024UL));
                }
            }
            mach_port_deallocate(mach_task_self(), host);
            return result;
        }
        @catch (NSException *e)
        {
            return -1;
        }
#endif
    }

    long PlayScopeGetAvailableDiskMb(void)
    {
        @try
        {
            NSArray<NSString *> *paths = NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES);
            NSString *path = paths.firstObject;
            if (path == nil) { return -1; }

            NSError *err = nil;
            NSDictionary<NSFileAttributeKey, id> *attrs =
                [[NSFileManager defaultManager] attributesOfFileSystemForPath:path error:&err];
            if (attrs == nil || err != nil) { return -1; }

            NSNumber *freeSize = attrs[NSFileSystemFreeSize];
            if (freeSize == nil) { return -1; }

            uint64_t bytes = [freeSize unsignedLongLongValue];
            return (long)(bytes / (1024UL * 1024UL));
        }
        @catch (NSException *e)
        {
            return -1;
        }
    }
}
