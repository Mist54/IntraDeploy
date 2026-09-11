namespace IntraDeploy.Models
{
    /// <summary>
    /// High level application type of the published folder being deployed.
    /// Detection is intentionally simple (web.config vs *.runtimeconfig.json).
    /// </summary>
    public enum ApplicationType
    {
        /// <summary>Traditional ASP.NET application (MVC or Web Forms) targeting the .NET Framework.</summary>
        AspNetFramework,

        /// <summary>ASP.NET Core application hosted by IIS (reverse proxy or in-process hosting).</summary>
        AspNetCore,

        /// <summary>Folder contents did not match a known application layout.</summary>
        Unknown
    }
}
