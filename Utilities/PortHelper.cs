using System.Net;
using System.Net.NetworkInformation;

namespace IntraDeploy.Utilities
{
    /// <summary>
    /// Small helper to warn the operator when a binding port is already occupied.
    /// </summary>
    public static class PortHelper
    {
        /// <summary>True when some local TCP listener already occupies the port.</summary>
        public static bool IsPortInUse(int port)
        {
            IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
            IPEndPoint[] endpoints = properties.GetActiveTcpListeners();
            foreach (IPEndPoint endpoint in endpoints)
            {
                if (endpoint.Port == port)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
