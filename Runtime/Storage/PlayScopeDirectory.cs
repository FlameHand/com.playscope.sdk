using UnityEngine;

namespace PlayScopeSdk.Storage
{
    internal static class PlayScopeDirectory
    {
        private static string _root;

        internal static string Root =>
            _root ??= System.IO.Path.Combine(Application.persistentDataPath, "PlayScope");

        internal static string DeviceFile =>
            System.IO.Path.Combine(Root, "device.json");

        internal static string CurrentSession =>
            System.IO.Path.Combine(Root, "current_session");

        internal static string SessionJson =>
            System.IO.Path.Combine(CurrentSession, "session.json");

        internal static string SessionLock =>
            System.IO.Path.Combine(CurrentSession, "session.lock");

        internal static string SessionHb =>
            System.IO.Path.Combine(CurrentSession, "session.hb");

        internal static string Chunks =>
            System.IO.Path.Combine(CurrentSession, "chunks");

        internal static string UploadQueue =>
            System.IO.Path.Combine(CurrentSession, "upload_queue");

        internal static string CompletedSessions =>
            System.IO.Path.Combine(Root, "completed_sessions");

        internal static string DeadLetter =>
            System.IO.Path.Combine(Root, "dead_letter");

        internal static void EnsureRootDirectories()
        {
            System.IO.Directory.CreateDirectory(Root);
            System.IO.Directory.CreateDirectory(CurrentSession);
            System.IO.Directory.CreateDirectory(Chunks);
            System.IO.Directory.CreateDirectory(UploadQueue);
            System.IO.Directory.CreateDirectory(CompletedSessions);
            System.IO.Directory.CreateDirectory(DeadLetter);
        }
    }
}
