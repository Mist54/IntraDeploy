namespace IntraDeploy.Models
{
    /// <summary>
    /// IIS deployment model selected by the operator.
    /// </summary>
    public enum IisMode
    {
        /// <summary>
        /// Deploy as an IIS Application under an existing site, e.g.
        /// Default Web Site → /BiCore → http://localhost/BiCore.
        /// This is the normal deployment model for our environment.
        /// </summary>
        ApplicationUnderSite,

        /// <summary>
        /// Deploy as a separate IIS Site with its own binding, e.g.
        /// BiCore → http://localhost:8099.
        /// </summary>
        SeparateSite
    }
}
