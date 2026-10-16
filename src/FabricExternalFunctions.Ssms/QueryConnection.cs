using System;
using System.Linq;
using System.Reflection;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;

namespace FabricExternalFunctions.Ssms
{
    internal static class QueryConnection
    {
        public static UIConnectionInfo ForNode(INodeInformation source, Warehouse target)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ObjectExplorer") ??
                throw new NotSupportedException("The native Object Explorer connection service is unavailable.");
            UIConnectionInfo? connection;
            try
            {
                connection = ResolveNativeMethod(assembly).Invoke(null, new object[] { source }) as UIConnectionInfo;
            }
            catch (TargetInvocationException error)
            {
                throw new InvalidOperationException("SSMS could not reuse the Object Explorer connection. Reconnect Object Explorer.",
                    error.InnerException ?? error);
            }
            return ValidateTarget(connection, target);
        }

        public static MethodInfo ResolveNativeMethod(Assembly assembly)
        {
            // Use the same connection-context helper as the native Object Explorer New Query command.
            var method = assembly.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.Utils")?
                .GetMethod("GetUIConnectionWithDatabaseForNode", BindingFlags.Static | BindingFlags.NonPublic,
                    null, new[] { typeof(INodeInformation) }, null);
            if (method == null || method.ReturnType != typeof(UIConnectionInfo))
                throw new NotSupportedException("This SSMS build does not expose the expected Object Explorer query connection helper.");
            return method;
        }

        public static UIConnectionInfo ValidateTarget(UIConnectionInfo? connection, Warehouse target)
        {
            if (connection == null)
                throw new InvalidOperationException("The Object Explorer connection is unavailable. Reconnect Object Explorer.");
            if (!string.Equals(connection.ServerName, target.Server, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(connection.AdvancedOptions["DATABASE"], target.Database, StringComparison.Ordinal))
                throw new InvalidOperationException("The Object Explorer connection no longer matches the selected warehouse. Refresh Object Explorer.");
            return connection;
        }
    }
}
