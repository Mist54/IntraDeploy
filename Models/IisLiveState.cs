namespace IntraDeploy.Models
{
    /// <summary>
    /// Live IIS state for the requested deployment target, gathered immediately before
    /// the confirmation dialog so the operator sees what IIS actually contains and what
    /// IntraDeploy will do (create vs update), not merely what was typed.
    /// </summary>
    public class IisLiveState
    {
        /// <summary>True when the parent site exists (ApplicationUnderSite mode).</summary>
        public bool ParentSiteExists { get; set; }

        /// <summary>True when the target IIS Application already exists under the parent site.</summary>
        public bool ApplicationExists { get; set; }

        /// <summary>True when a separate IIS Site with the requested name already exists.</summary>
        public bool SiteExists { get; set; }

        /// <summary>True when the application pool already exists.</summary>
        public bool ApplicationPoolExists { get; set; }

        /// <summary>Current physical path of the existing application/site root, when present.</summary>
        public string CurrentPhysicalPath { get; set; }

        /// <summary>Current application pool of the existing application/site root, when present.</summary>
        public string CurrentApplicationPool { get; set; }

        /// <summary>Human readable binding summary of the parent site (ApplicationUnderSite mode).</summary>
        public string ParentSiteBindings { get; set; }

        /// <summary>Operator facing action text, e.g. "Update existing IIS Application".</summary>
        public string Action { get; set; }

        /// <summary>Operator facing description lines shown in the confirmation dialog.</summary>
        public string Details { get; set; }
    }
}
