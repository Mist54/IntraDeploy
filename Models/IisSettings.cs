using System;

namespace IntraDeploy.Models
{
    /// <summary>IIS related settings for a deployment request.</summary>
    public class IisSettings
    {
        /// <summary>Selected deployment model.</summary>
        public IisMode Mode { get; set; }

        /// <summary>Parent IIS site name for ApplicationUnderSite mode, e.g. "Default Web Site".</summary>
        public string ParentSiteName { get; set; }

        /// <summary>Application path under the parent site for ApplicationUnderSite mode, e.g. "/BiCore".</summary>
        public string ApplicationPath { get; set; }

        /// <summary>IIS site name for SeparateSite mode, e.g. "BiCore".</summary>
        public string SiteName { get; set; }

        /// <summary>Application pool name, e.g. "BiCore".</summary>
        public string ApplicationPoolName { get; set; }

        /// <summary>HTTP port for the SeparateSite binding, e.g. 8099.</summary>
        public int Port { get; set; }

        /// <summary>
        /// Optional HTTPS port for SeparateSite. 0 or unset = HTTP only (default).
        /// When set, a certificate thumbprint is required.
        /// </summary>
        public int HttpsPort { get; set; }

        /// <summary>
        /// LocalMachine certificate thumbprint (hex, spaces optional) for the HTTPS binding.
        /// Used only when <see cref="HttpsPort"/> is greater than 0.
        /// </summary>
        public string HttpsCertificateThumbprint { get; set; }

        /// <summary>Certificate store name under LocalMachine. Default "My" when null/empty.</summary>
        public string HttpsCertificateStoreName { get; set; }

        /// <summary>Deployment root directory, e.g. C:\IntraDeploy\Applications.</summary>
        public string DeploymentRoot { get; set; }

        /// <summary>Resolved store name for HTTPS bindings ("My" when unset).</summary>
        public string ResolvedHttpsCertificateStoreName
        {
            get
            {
                return string.IsNullOrWhiteSpace(HttpsCertificateStoreName)
                    ? "My"
                    : HttpsCertificateStoreName.Trim();
            }
        }

        /// <summary>True when SeparateSite HTTPS binding was requested.</summary>
        public bool HasHttpsBinding
        {
            get { return HttpsPort > 0; }
        }

        /// <summary>Normalized application path (leading slash, no trailing slash). "/" when missing.</summary>
        public string NormalizedApplicationPath
        {
            get
            {
                string path = (ApplicationPath ?? string.Empty).Trim();
                if (path.Length == 0)
                {
                    return "/";
                }
                if (!path.StartsWith("/", StringComparison.Ordinal))
                {
                    path = "/" + path;
                }
                return path.TrimEnd('/');
            }
        }
    }
}
