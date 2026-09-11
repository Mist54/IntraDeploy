using System;

namespace IntraDeploy.Models
{
    /// <summary>
    /// Summary of what the detector found in the published application folder.
    /// </summary>
    public class ApplicationInfo
    {
        /// <summary>Detected application type.</summary>
        public ApplicationType Type { get; set; }

        /// <summary>True when the folder looks like a deployable IIS application.</summary>
        public bool IsDeployable { get; set; }

        /// <summary>Short human readable type description, e.g. "ASP.NET Framework 4.8 (web.config)".</summary>
        public string Description { get; set; }

        /// <summary>True when a web.config is present at the root of the folder.</summary>
        public bool HasWebConfig { get; set; }

        /// <summary>True when a *.runtimeconfig.json is present (modern .NET app).</summary>
        public bool HasRuntimeConfig { get; set; }

        /// <summary>Name of the ASP.NET Core hosting bundle referenced by web.config, if any.</summary>
        public string AspNetCoreModuleName { get; set; }

        /// <summary>True when the web.config references the ASP.NET Core Module V2.</summary>
        public bool UsesAspNetCoreModuleV2
        {
            get { return AspNetCoreModuleName != null && AspNetCoreModuleName.IndexOf("AspNetCoreModuleV2", StringComparison.OrdinalIgnoreCase) >= 0; }
        }
    }
}
