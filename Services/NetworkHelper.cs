using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

namespace MenuScan.Services
{
    public static class NetworkHelper
    {
        /// <summary>
        /// Detects the active LAN IPv4 address of the host machine (e.g., 192.168.1.9).
        /// Ignores virtual adapters (VirtualBox, Docker) and loopbacks.
        /// </summary>
        public static string GetLocalIpAddress()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                if (socket.LocalEndPoint is IPEndPoint endPoint)
                {
                    var ipStr = endPoint.Address.ToString();
                    if (!string.IsNullOrEmpty(ipStr) && ipStr != "0.0.0.0")
                    {
                        return ipStr;
                    }
                }
            }
            catch { }

            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(ip)
                        && !ip.ToString().StartsWith("192.168.56.")
                        && !ip.ToString().StartsWith("169.254."))
                    {
                        return ip.ToString();
                    }
                }
            }
            catch { }

            return "127.0.0.1";
        }

        /// <summary>
        /// Resolves a host string suitable for mobile phones scanning QR codes.
        /// If the browser is on localhost/127.0.0.1, replaces it with the LAN IP so phones can connect.
        /// </summary>
        public static string ResolveReachableHost(HttpRequest request)
        {
            var rawHost = request.Host.Host;
            var port = request.Host.Port ?? (request.IsHttps ? 443 : 5073);

            if (rawHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || rawHost.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || rawHost.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
            {
                var localIp = GetLocalIpAddress();
                return $"{localIp}:{port}";
            }

            return request.Host.Value ?? $"{GetLocalIpAddress()}:{port}";
        }

        /// <summary>
        /// Resolves the base URL for QR codes, WhatsApp, and links.
        /// Uses App:PublicBaseUrl if configured; otherwise falls back to request scheme + reachable host.
        /// </summary>
        public static string ResolveBaseUrl(HttpRequest request, IConfiguration? configuration = null)
        {
            var publicBaseUrl = configuration?["App:PublicBaseUrl"];
            if (!string.IsNullOrWhiteSpace(publicBaseUrl))
            {
                return publicBaseUrl.TrimEnd('/');
            }

            var reachableHost = ResolveReachableHost(request);
            var scheme = request.Scheme ?? "http";
            return $"{scheme}://{reachableHost}".TrimEnd('/');
        }

        /// <summary>
        /// Validates that stream begins with valid magic bytes for JPEG, PNG, or WEBP images.
        /// </summary>
        public static bool IsValidImageMagicBytes(Stream stream)
        {
            if (stream == null) return false;
            if (stream.CanSeek) stream.Position = 0;

            var header = new byte[12];
            int bytesRead = stream.Read(header, 0, header.Length);
            if (stream.CanSeek) stream.Position = 0;

            if (bytesRead < 4) return false;

            // JPEG: FF D8 FF
            if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                return true;

            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (bytesRead >= 8 &&
                header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
                header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
                return true;

            // WEBP: RIFF .... WEBP
            if (bytesRead >= 12 &&
                header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 && // RIFF
                header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)  // WEBP
                return true;

            return false;
        }
    }
}
