using System;
using Microsoft.Data.SqlClient;

namespace FabricExternalFunctions.Ssms
{
    internal sealed class Warehouse
    {
        public Warehouse(string server, string database)
        {
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
                throw new ArgumentException("A server and warehouse database are required.");
            Server = server; Database = database;
        }
        public string Server { get; }
        public string Database { get; }
        public string DisplayName => Server + " / " + Database;
        public SqlConnection NewConnection()
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = Server,
                InitialCatalog = Database,
                ApplicationName = "Fabric External Functions",
                ConnectTimeout = 30,
                TrustServerCertificate = false
            };
            builder["Encrypt"] = "True";
            builder["Authentication"] = "Active Directory Interactive";
            return new SqlConnection(builder.ConnectionString);
        }
    }
}
