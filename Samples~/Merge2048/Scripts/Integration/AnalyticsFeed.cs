using System;
using System.Collections.Generic;

namespace Merge2048.Integration
{
    // In-process bus only — no PlayScope calls here. Integration call sites publish
    // alongside their real PlayScope.* calls; AnalyticsFeedView (Presentation) renders it.
    public static class AnalyticsFeed
    {
        private const int MAX_ENTRIES = 8;

        private static readonly List<string> _entries = new List<string>(MAX_ENTRIES);

        public static event Action<string> EntryAdded;

        public static IReadOnlyList<string> Recent => _entries.ToArray();

        public static void Publish(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            _entries.Add(message);
            if (_entries.Count > MAX_ENTRIES)
            {
                _entries.RemoveAt(0);
            }

            EntryAdded?.Invoke(message);
        }
    }
}
