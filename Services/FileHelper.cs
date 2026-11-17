using System;
using System.Collections.Concurrent;
using System.IO;

namespace MenuScan.Services
{
    public static class FileHelper
    {
        private static string? _webRootPath;
        private static readonly ConcurrentDictionary<string, (bool Exists, DateTime Expiry)> _diskCheckCache = new();
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

        public static void Initialize(string webRootPath)
        {
            _webRootPath = webRootPath;
            _diskCheckCache.Clear();
        }

        public static bool ImageExistsOnDisk(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            // Data URIs are self-contained and always valid without disk files
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;

            // Local relative path (e.g. /uploads/payment-qrs/... or uploads\...)
            if (url.StartsWith('/') || url.StartsWith('\\') || url.StartsWith("uploads", StringComparison.OrdinalIgnoreCase))
            {
                var now = DateTime.UtcNow;
                if (_diskCheckCache.TryGetValue(url, out var entry) && entry.Expiry > now)
                {
                    return entry.Exists;
                }

                var webRoot = _webRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                var relative = url.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
                var fullPath = Path.Combine(webRoot, relative);
                var exists = File.Exists(fullPath);

                _diskCheckCache[url] = (exists, now.Add(CacheTtl));
                return exists;
            }

            // External URLs (http / https) are not on local disk
            return false;
        }

        public static string? SafeImageUrl(string? url, string? fallbackUrl = null)
        {
            if (string.IsNullOrWhiteSpace(url)) return fallbackUrl;

            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return url;

            if (url.StartsWith('/') || url.StartsWith('\\') || url.StartsWith("uploads", StringComparison.OrdinalIgnoreCase))
            {
                return ImageExistsOnDisk(url) ? url : fallbackUrl;
            }

            return url;
        }
    }
}
