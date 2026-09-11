namespace IntraDeploy.Models
{
    /// <summary>Database related settings for a deployment request.</summary>
    public class DatabaseSettings
    {
        /// <summary>SQL Server host/instance, e.g. "localhost" or "localhost\SQLEXPRESS".</summary>
        public string Server { get; set; }

        /// <summary>Database name used by the application and for restore.</summary>
        public string DatabaseName { get; set; }

        /// <summary>Selected database mode. UseExisting is the default and performs no database work.</summary>
        public DatabaseMode Mode { get; set; }

        /// <summary>Path to the .bak file when Mode is RestoreFromBak, otherwise null.</summary>
        public string BackupFilePath { get; set; }

        /// <summary>
        /// Optional SQL login. Leave empty for Windows authentication (what the UI uses today).
        /// </summary>
        public string SqlUser { get; set; }

        /// <summary>
        /// Optional SQL password for SqlUser. Never logged. Unused when SqlUser is empty.
        /// </summary>
        public string SqlPassword { get; set; }

        /// <summary>
        /// When true the operator explicitly confirmed overwriting an existing database.
        /// Single source of truth: set only by the confirmation dialog in restore mode
        /// and consumed by DatabaseService.RestoreBackup.
        /// </summary>
        public bool ConfirmDatabaseOverwrite { get; set; }
    }
}
