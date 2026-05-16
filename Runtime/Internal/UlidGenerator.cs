using System;
using System.Security.Cryptography;

namespace PlayScopeSdk.Internal
{
    internal static class UlidGenerator
    {
        private static readonly char[] Chars = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".ToCharArray();

        internal static string NewEventId()
        {
            // 10-byte timestamp + 16-byte random = 26 crockford base32 chars
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Span<byte> rand = stackalloc byte[16];
            RandomNumberGenerator.Fill(rand);
            Span<char> result = stackalloc char[26];
            // encode timestamp (10 chars)
            for (int i = 9; i >= 0; i--) { result[i] = Chars[now & 31]; now >>= 5; }
            // encode random (16 chars)
            for (int i = 10; i < 26; i++) result[i] = Chars[rand[i - 10] & 31];
            return "evt_" + new string(result);
        }
    }
}
