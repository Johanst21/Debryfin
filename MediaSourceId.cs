namespace Debryfin
{
    using System;
    using System.Security.Cryptography;
    using System.Text;

    internal static class MediaSourceId
    {
        public static string For(Guid itemId, string path)
        {
            if (itemId != Guid.Empty) return itemId.ToString("N");

            // netstandard2.0/2.1: Convert.ToHexString does not exist.
            using (var sha1 = SHA1.Create())
            {
                var bytes = Encoding.UTF8.GetBytes((path ?? string.Empty).ToLowerInvariant());
                var hash = sha1.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
