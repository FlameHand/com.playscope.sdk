using System.Threading;

namespace PlayScopeSdk.Internal
{
    internal static class SequenceCounter
    {
        private static long _value = 0;
        internal static long Next() => Interlocked.Increment(ref _value);
        internal static void Reset() => Interlocked.Exchange(ref _value, 0);
    }
}
