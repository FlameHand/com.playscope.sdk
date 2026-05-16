using System.Collections.Concurrent;

namespace PlayScopeSdk.Internal
{
    internal sealed class UploadQueue
    {
        private readonly ConcurrentQueue<string> _paths = new();
        internal void Enqueue(string chunkPath) => _paths.Enqueue(chunkPath);
        internal bool TryDequeue(out string path) => _paths.TryDequeue(out path);
        internal int Count => _paths.Count;
    }
}
