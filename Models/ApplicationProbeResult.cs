namespace IntraDeploy.Models
{
    /// <summary>
    /// Result of probing a published application folder before deployment.
    /// </summary>
    public class ApplicationProbeResult
    {
        /// <summary>True when the source folder exists and looks deployable.</summary>
        public bool IsValid { get; set; }

        /// <summary>Short reason when the folder is not deployable, otherwise null.</summary>
        public string ErrorMessage { get; set; }

        /// <summary>Detected application metadata.</summary>
        public ApplicationInfo Info { get; set; }
    }
}
