using System.Net;
using System.Net.Sockets;

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
            var port = request.Host.Port ?? 5073;

            if (rawHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || rawHost.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || rawHost.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
            {
                var localIp = GetLocalIpAddress();
                return $"{localIp}:{port}";
            }

            return request.Host.Value ?? $"{GetLocalIpAddress()}:{port}";
        }
    }
}
