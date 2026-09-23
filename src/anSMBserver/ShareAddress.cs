// Share-address selection for the SMB URL / QR code.
//
// This lives in the SHARING LAYER ONLY. It deliberately does NOT modify the
// service-side network state: SmbService.LanIpv4 / RefreshNetworkState() /
// ScanLanIpv4() / the network monitor are untouched. In extreme fallback cases
// the URL address may differ from the status-bar address; that divergence is
// accepted.
//
// Rule:
//   1. LanIpv4 is RFC1918 -> use it.
//   2. else find an RFC1918 IPv4 on an Up, non-loopback interface that is not a
//      VPN/cellular interface.
//   3. else fall back to LanIpv4.
//   4. else no address (caller must disable Copy/QR).
// IPv4-only.
using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace anSMBserver
{
    public static class ShareAddress
    {
        /// <summary>D3-A resolved IPv4 for the share URL, or null if none usable.</summary>
        public static string Resolve(string lanIpv4)
        {
            if (IsRfc1918(lanIpv4)) return lanIpv4;

            string scanned = ScanRfc1918NonVpn();
            if (!string.IsNullOrEmpty(scanned)) return scanned;

            return string.IsNullOrEmpty(lanIpv4) ? null : lanIpv4;
        }

        /// <summary>Share URL `smb://&lt;ipv4&gt;:&lt;port&gt;/&lt;share&gt;`, or null if no usable address.</summary>
        public static string BuildShareUrl(string lanIpv4)
        {
            string ip = Resolve(lanIpv4);
            if (string.IsNullOrEmpty(ip)) return null;
            return "smb://" + ip + ":" + SmbService.ConfiguredPort + "/" + SmbService.ShareName;
        }

        public static bool IsRfc1918(string ip)
        {
            if (!IPAddress.TryParse(ip, out IPAddress addr)) return false;
            if (addr.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = addr.GetAddressBytes();
            if (b[0] == 10) return true;                                    // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;       // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;                    // 192.168.0.0/16
            return false;
        }

        private static string ScanRfc1918NonVpn()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (IsVpnOrCellular(ni)) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IsRfc1918(ua.Address.ToString())) return ua.Address.ToString();
                    }
                }
            }
            catch { }
            return null;
        }

        private static bool IsVpnOrCellular(NetworkInterface ni)
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return true;
            string n = (ni.Name ?? string.Empty).ToLowerInvariant();
            return n.StartsWith("tun") || n.StartsWith("tap") || n.StartsWith("ppp")
                || n.StartsWith("rmnet") || n.StartsWith("ccmni") || n.StartsWith("pdp")
                || n.StartsWith("clat") || n.StartsWith("vpn");
        }
    }
}
