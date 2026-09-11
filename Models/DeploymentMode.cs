namespace IntraDeploy.Models
{
    /// <summary>
    /// Partial operation modes. Full is the normal end-to-end pipeline;
    /// other values skip irrelevant write steps in DeploymentService.
    /// </summary>
    public enum DeploymentMode
    {
        /// <summary>Copy, IIS, optional DB/config, recycle, health.</summary>
        Full = 0,

        /// <summary>Copy + IIS + config + recycle + health; skip database restore.</summary>
        FilesAndIis = 1,

        /// <summary>IIS pool/target ensure + recycle + health; no file copy or DB.</summary>
        IisOnly = 2,

        /// <summary>Database restore only (when restore mode is selected).</summary>
        DatabaseOnly = 3,

        /// <summary>Recycle / start the IIS target only; optional health.</summary>
        RecycleOnly = 4
    }
}
