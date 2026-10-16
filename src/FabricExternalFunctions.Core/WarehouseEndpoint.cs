using System;

namespace FabricExternalFunctions.Core
{
    public static class WarehouseEndpoint
    {
        public static bool IsSupported(string? server)
        {
            if (server == null || string.IsNullOrWhiteSpace(server)) return false;
            string host = server.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? server.Substring(4) : server;
            host = host.Split(',')[0];
            const string suffix = "datawarehouse.fabric.microsoft.com";
            int suffixStart = host.Length - suffix.Length;
            return suffixStart > 0 && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                (host[suffixStart - 1] == '.' || host[suffixStart - 1] == '-');
        }
    }
}
