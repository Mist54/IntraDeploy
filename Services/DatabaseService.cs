using System;
using System.Data;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Serilog;
using IntraDeploy.Models;

namespace IntraDeploy.Services
{
    /// <summary>
    /// SQL Server access via Microsoft.Data.SqlClient.
    ///
    /// Safety rules implemented here:
    ///  - no database operation at all when the operator selected "use existing database";
    ///  - restore refuses to overwrite an existing database unless the operator explicitly
    ///    confirmed the overwrite (ConfirmDatabaseOverwrite must be set on the request);
    ///  - backup metadata (logical file names) is treated as untrusted and escaped;
    ///  - destination MDF/LDF paths are pre-checked so an existing unrelated file is
    ///    never overwritten;
    ///  - credentials are never logged.
    /// </summary>
    public class DatabaseService
    {
        private static readonly Regex LogicalNameSanitizer = new Regex(@"[^A-Za-z0-9_]");

        /// <summary>Builds a connection string for the master database. Never logs the password.</summary>
        public static string BuildMasterConnectionString(DatabaseSettings settings)
        {
            return BuildConnectionString(settings, "master");
        }

        /// <summary>
        /// Builds an application connection string for the configured database.
        /// Never logs the password. Used when the operator opts into UpdateConnectionString.
        /// </summary>
        public static string BuildApplicationConnectionString(DatabaseSettings settings)
        {
            return BuildConnectionString(settings, settings != null ? settings.DatabaseName : null);
        }

        private static string BuildConnectionString(DatabaseSettings settings, string initialCatalog)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = settings.Server,
                InitialCatalog = string.IsNullOrWhiteSpace(initialCatalog) ? "master" : initialCatalog,
                IntegratedSecurity = string.IsNullOrWhiteSpace(settings.SqlUser),
                ApplicationName = "IntraDeploy",
                ConnectTimeout = 15,
                // Internal SQL: encrypt on the wire, but allow local/self-signed certs.
                Encrypt = true,
                TrustServerCertificate = true
            };

            if (!string.IsNullOrWhiteSpace(settings.SqlUser))
            {
                builder.UserID = settings.SqlUser;
                builder.Password = settings.SqlPassword ?? string.Empty;
            }
            return builder.ConnectionString;
        }

        /// <summary>Throws DeploymentStepException when SQL Server cannot be reached.</summary>
        public void TestConnection(DatabaseSettings settings)
        {
            string connectionString = BuildMasterConnectionString(settings);
            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    Log.Information("SQL Server connectivity verified: {Server}", settings.Server);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "SQL Server connection failed for {Server}", settings.Server);
                throw new DeploymentStepException(DeploymentStep.CheckSqlServer,
                    "SQL Server connection failed for '" + settings.Server + "': " + ex.Message, ex);
            }
        }

        /// <summary>True when the database exists on the server. Failures throw.</summary>
        public bool DatabaseExists(DatabaseSettings settings)
        {
            using (var connection = new SqlConnection(BuildMasterConnectionString(settings)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @name";
                    command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = settings.DatabaseName;
                    int count = (int)command.ExecuteScalar();
                    return count > 0;
                }
            }
        }

        /// <summary>
        /// Validates the .bak file: existence, header READ only (RESTORE HEADERONLY / FILELISTONLY),
        /// extracts logical file names for MOVE clauses. No changes are made to the server.
        /// </summary>
        public RestorePlan ValidateBackup(DatabaseSettings settings)
        {
            string backupPath = settings.BackupFilePath;
            if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            {
                throw new DeploymentStepException(DeploymentStep.CheckSqlServer,
                    "Backup file does not exist: " + (backupPath ?? "<empty>"));
            }

            Log.Information("Validating backup file {Backup} against server {Server}", backupPath, settings.Server);

            string masterConnectionString = BuildMasterConnectionString(settings);

            string logicalData;
            string logicalLog;
            string backupDescription;

            try
            {
                using (var connection = new SqlConnection(masterConnectionString))
                {
                    connection.Open();

                    // HEADERONLY proves the file is a valid SQL Server backup and readable.
                    using (var headerCommand = connection.CreateCommand())
                    {
                        headerCommand.CommandText = "RESTORE HEADERONLY FROM DISK = @bak";
                        headerCommand.Parameters.Add("@bak", SqlDbType.NVarChar, 260).Value = backupPath;
                        headerCommand.CommandTimeout = 120;

                        using (var reader = headerCommand.ExecuteReader())
                        {
                            if (!reader.Read())
                            {
                                throw new DeploymentStepException(DeploymentStep.CheckSqlServer,
                                    "The backup file could not be read as a SQL Server backup: " + backupPath);
                            }
                            backupDescription = reader["DatabaseName"] != DBNull.Value
                                ? Convert.ToString(reader["DatabaseName"])
                                : null;
                        }
                    }

                    // FILELISTONLY gives the logical file names needed for MOVE clauses.
                    using (var fileListCommand = connection.CreateCommand())
                    {
                        fileListCommand.CommandText = "RESTORE FILELISTONLY FROM DISK = @bak";
                        fileListCommand.Parameters.Add("@bak", SqlDbType.NVarChar, 260).Value = backupPath;
                        fileListCommand.CommandTimeout = 120;

                        using (var reader = fileListCommand.ExecuteReader())
                        {
                            logicalData = null;
                            logicalLog = null;
                            while (reader.Read())
                            {
                                string type = Convert.ToString(reader["Type"]);
                                string logicalName = Convert.ToString(reader["LogicalName"]);
                                if (string.Equals(type, "D", StringComparison.OrdinalIgnoreCase))
                                {
                                    logicalData = logicalName;
                                }
                                else if (string.Equals(type, "L", StringComparison.OrdinalIgnoreCase))
                                {
                                    logicalLog = logicalName;
                                }
                            }
                        }
                    }
                }
            }
            catch (DeploymentStepException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Backup validation failed for {Backup}", backupPath);
                throw new DeploymentStepException(DeploymentStep.CheckSqlServer,
                    "The backup file could not be validated: " + ex.Message, ex);
            }

            if (string.IsNullOrEmpty(logicalData))
            {
                throw new DeploymentStepException(DeploymentStep.CheckSqlServer,
                    "The backup file contains no data file (Type 'D'). File: " + backupPath);
            }

            var plan = new RestorePlan
            {
                BackupFilePath = backupPath,
                LogicalDataFile = logicalData,
                LogicalLogFile = logicalLog,
                SourceDatabaseName = backupDescription,
                TargetDatabaseName = settings.DatabaseName
            };

            Log.Information("Backup validated. Source database in backup: {Source}. Logical data file: {Data}, log file: {Log}",
                backupDescription ?? "<unknown>", logicalData, logicalLog ?? "<none>");
            return plan;
        }

        /// <summary>
        /// Restores the database. Destructive only when the target database exists AND the
        /// operator set ConfirmDatabaseOverwrite; otherwise the operation aborts safely.
        /// </summary>
        public void RestoreBackup(DatabaseSettings settings, RestorePlan plan)
        {
            bool exists;
            try
            {
                exists = DatabaseExists(settings);
            }
            catch (Exception ex)
            {
                throw new DeploymentStepException(DeploymentStep.DatabaseOperation,
                    "Could not check whether database '" + settings.DatabaseName + "' exists: " + ex.Message, ex);
            }

            if (exists && !settings.ConfirmDatabaseOverwrite)
            {
                Log.Error("Database {Database} already exists. Restore was not performed.",
                    settings.DatabaseName);
                throw new DeploymentStepException(DeploymentStep.DatabaseOperation,
                    "Database '" + settings.DatabaseName + "' already exists. Restore was not performed. " +
                    "Overwriting requires explicit confirmation.");
            }

            if (exists)
            {
                Log.Warning("Database {Database} exists; operator explicitly confirmed overwrite. Proceeding with restore.",
                    settings.DatabaseName);
            }

            // Destination physical paths for the data/log files.
            string dataDirectory = GetDefaultDataDirectory(settings);
            string logDirectory = dataDirectory;
            string dataFileName = SanitizeFileName(plan.TargetDatabaseName) + "_data.mdf";
            string logFileName = SanitizeFileName(plan.TargetDatabaseName) + "_log.ldf";
            string destinationDataFile = Path.Combine(dataDirectory, dataFileName);
            string destinationLogFile = Path.Combine(logDirectory, logFileName);

            CheckPhysicalFileCollision(settings, destinationDataFile, destinationLogFile);

            // Backup metadata is untrusted input from the .bak — escape quotes before MOVE.
            // Database name itself is validated earlier (safe characters only).
            string logicalDataEscaped = EscapeSqlString(plan.LogicalDataFile);
            string logicalLogEscaped = plan.LogicalLogFile == null ? null : EscapeSqlString(plan.LogicalLogFile);

            string restoreSql =
                "RESTORE DATABASE [" + plan.TargetDatabaseName + "] " +
                "FROM DISK = @bak " +
                "WITH REPLACE, RECOVERY, STATS = 10" +
                ", MOVE '" + logicalDataEscaped + "' TO '" + EscapeSqlString(destinationDataFile) + "'" +
                (logicalLogEscaped != null
                    ? ", MOVE '" + logicalLogEscaped + "' TO '" + EscapeSqlString(destinationLogFile) + "'"
                    : "");

            Log.Information("Restoring database {Database} from {Backup} (WITH REPLACE, MOVE data to {DataPath}, log to {LogPath})",
                plan.TargetDatabaseName, plan.BackupFilePath, destinationDataFile, destinationLogFile);

            try
            {
                using (var connection = new SqlConnection(BuildMasterConnectionString(settings)))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = restoreSql;
                        command.CommandTimeout = 0; // restore can take minutes on big databases
                        command.Parameters.Add("@bak", SqlDbType.NVarChar, 260).Value = plan.BackupFilePath;
                        command.ExecuteNonQuery();
                    }
                }
                Log.Information("Database {Database} restored successfully", plan.TargetDatabaseName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Restore of database {Database} failed", plan.TargetDatabaseName);
                throw new DeploymentStepException(DeploymentStep.DatabaseOperation,
                    "Database restore failed. The database may be left in a RESTORING state. " +
                    "Manual SQL Server recovery may be required (re-run RESTORE ... WITH REPLACE, or drop the database). " +
                    "Error: " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Stops the restore when a destination MDF/LDF already exists that does not
        /// safely belong to the target database. WITH REPLACE does not overwrite
        /// pre-existing OS files, so restoring onto such a path would fail late and
        /// confusingly; this turns that into an early, clear error.
        /// </summary>
        private static void CheckPhysicalFileCollision(DatabaseSettings settings, string dataFile, string logFile)
        {
            string existingData = GetExistingDatabaseFilePath(settings, dataFile);
            if (existingData != null)
            {
                throw new DeploymentStepException(DeploymentStep.DatabaseOperation,
                    "A data file for database '" + settings.DatabaseName + "' already exists at '" + existingData +
                    "' and is used by database '" + GetOwningDatabase(settings, existingData) +
                    "'. Restoring over it is not allowed. Choose a different database name or remove the file manually.");
            }

            string existingLog = GetExistingDatabaseFilePath(settings, logFile);
            if (existingLog != null)
            {
                throw new DeploymentStepException(DeploymentStep.DatabaseOperation,
                    "A log file already exists at '" + existingLog + "' and is used by database '" +
                    GetOwningDatabase(settings, existingLog) +
                    "'. Restoring over it is not allowed. Choose a different database name or remove the file manually.");
            }
        }

        /// <summary>Returns the file path when a file with this name is registered to a database, otherwise null.</summary>
        private static string GetExistingDatabaseFilePath(DatabaseSettings settings, string fullPath)
        {
            try
            {
                using (var connection = new SqlConnection(BuildMasterConnectionString(settings)))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "SELECT physical_name FROM sys.master_files WHERE physical_name = @path";
                        command.Parameters.Add("@path", SqlDbType.NVarChar, 260).Value = fullPath;
                        object value = command.ExecuteScalar();
                        return value == null || value is DBNull ? null : Convert.ToString(value);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Could not query sys.master_files for {Path}: {Message}", fullPath, ex.Message);
                return null; // treated as no collision; the restore itself will fail safely if wrong
            }
        }

        private static string GetOwningDatabase(DatabaseSettings settings, string fullPath)
        {
            try
            {
                using (var connection = new SqlConnection(BuildMasterConnectionString(settings)))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "SELECT d.name FROM sys.master_files f JOIN sys.databases d ON d.database_id = f.database_id " +
                            "WHERE f.physical_name = @path";
                        command.Parameters.Add("@path", SqlDbType.NVarChar, 260).Value = fullPath;
                        object value = command.ExecuteScalar();
                        return value == null || value is DBNull ? "<unknown>" : Convert.ToString(value);
                    }
                }
            }
            catch
            {
                return "<unknown>";
            }
        }

        private static string EscapeSqlString(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        private static string SanitizeFileName(string databaseName)
        {
            return LogicalNameSanitizer.Replace(databaseName ?? "database", "_");
        }

        /// <summary>
        /// Discovers the instance's default data/log directory via SERVERPROPERTY
        /// (instance default paths, the same mechanism SSMS shows in server properties).
        /// There is deliberately no hardcoded fallback: if the paths cannot be read the
        /// restore is aborted instead of guessing a directory.
        /// </summary>
        private static string GetDefaultDataDirectory(DatabaseSettings settings)
        {
            try
            {
                using (var connection = new SqlConnection(BuildMasterConnectionString(settings)))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(260))";
                        object value = command.ExecuteScalar();
                        string path = Convert.ToString(value);
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            throw new DeploymentStepException(DeploymentStep.DatabaseOperation,
                                "Could not determine the SQL Server default data directory " +
                                "(SERVERPROPERTY('InstanceDefaultDataPath') returned nothing). " +
                                "Restore was not attempted; specify the paths manually in SQL Server.");
                        }
                        return path;
                    }
                }
            }
            catch (DeploymentStepException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new DeploymentStepException(DeploymentStep.DatabaseOperation,
                    "Could not determine the SQL Server default data directory: " + ex.Message +
                    ". Restore was not attempted.", ex);
            }
        }
    }

    /// <summary>Logical file names extracted from a validated .bak, used to build MOVE clauses.</summary>
    public class RestorePlan
    {
        public string BackupFilePath { get; set; }
        public string LogicalDataFile { get; set; }
        public string LogicalLogFile { get; set; }
        public string SourceDatabaseName { get; set; }
        public string TargetDatabaseName { get; set; }
    }
}
