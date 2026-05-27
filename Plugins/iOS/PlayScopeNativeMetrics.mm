#import <Foundation/Foundation.h>
#import <mach/mach.h>
#import <mach/mach_host.h>

extern "C"
{
    long PlayScopeGetFreeMemoryMb(void)
    {
        @try
        {
            mach_port_t host = mach_host_self();
            long result = 0;
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
            return 0;
        }
    }
}
