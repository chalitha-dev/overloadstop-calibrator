using System;
using System.Configuration;
using System.Data;
using System.Data.Common;

namespace overloadstop_calibrator
{
    public sealed class DatabaseConnection
    {
        private readonly string _connectionString;
        private readonly DbProviderFactory _factory;

        public DatabaseConnection()
            : this("MySqlConn")
        {
        }

        public DatabaseConnection(string connectionStringName)
        {
            ConnectionStringSettings setting = ConfigurationManager.ConnectionStrings[connectionStringName];
            if (setting == null || string.IsNullOrEmpty(setting.ConnectionString) || setting.ConnectionString.Trim().Length == 0)
            {
                throw new ConfigurationErrorsException(
                    "Connection string '" + connectionStringName + "' was not found in app.config.");
            }

            string providerName = string.IsNullOrEmpty(setting.ProviderName)
                ? "MySql.Data.MySqlClient"
                : setting.ProviderName;

            _factory = DbProviderFactories.GetFactory(providerName);
            _connectionString = setting.ConnectionString;
        }

        public IDbConnection GetOpenConnection()
        {
            IDbConnection connection = _factory.CreateConnection();
            connection.ConnectionString = _connectionString;
            connection.Open();
            return connection;
        }

        public bool CanConnect(out string errorMessage)
        {
            errorMessage = string.Empty;
            try
            {
                using (IDbConnection connection = GetOpenConnection())
                {
                    return connection.State == ConnectionState.Open;
                }
            }
            catch (Exception ex)
            {
                errorMessage = GetFullMessage(ex);
                return false;
            }
        }
        private static string GetFullMessage(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            while (ex != null)
            {
                if (sb.Length > 0) sb.Append("\n-> ");
                sb.Append(ex.Message);
                ex = ex.InnerException;
            }
            return sb.ToString();
        }
    }
}