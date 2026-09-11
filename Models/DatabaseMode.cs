namespace IntraDeploy.Models
{
    /// <summary>
    /// Database action selected by the operator for a deployment.
    /// </summary>
    public enum DatabaseMode
    {
        /// <summary>
        /// Use the database that already exists on the SQL Server.
        /// IntraDeploy performs NO database operations in this mode (default and safest).
        /// </summary>
        UseExisting,

        /// <summary>
        /// Restore a SQL Server .bak file supplied by the operator.
        /// Requires explicit operator confirmation when the target database already exists.
        /// </summary>
        RestoreFromBak
    }
}
