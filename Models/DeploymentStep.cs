namespace IntraDeploy.Models
{
    /// <summary>
    /// Pipeline steps executed by DeploymentService, in execution order.
    /// Used for progress reporting and to identify the failed step.
    /// </summary>
    public enum DeploymentStep
    {
        NotStarted = 0,
        ValidateInputs = 1,
        ProbeApplication = 2,
        CheckIisAvailability = 3,
        CheckSqlServer = 4,
        PrepareTargetFolder = 5,
        CopyApplicationFiles = 6,
        ConfigureIis = 7,
        DatabaseOperation = 8,
        ApplyConfiguration = 9,
        StartAndRecycle = 10,
        HealthCheck = 11,
        PostDeployHook = 12,
        Completed = 13
    }
}
