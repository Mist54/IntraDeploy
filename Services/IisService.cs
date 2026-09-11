using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Microsoft.Web.Administration;
using Serilog;
using IntraDeploy.Models;

namespace IntraDeploy.Services
{
    /// <summary>
    /// All IIS access goes through Microsoft.Web.Administration (no appcmd / shell commands).
    ///
    /// Two deployment models are supported and are kept strictly separate:
    ///
    ///   ApplicationUnderSite : an IIS *Application* under an existing site
    ///                          (Default Web Site → /BiCore → http://localhost/BiCore).
    ///                          The parent site is never created, deleted or re-bound.
    ///   SeparateSite         : a dedicated IIS *Site* with its own binding
    ///                          (BiCore → http://localhost:8099).
    ///
    /// Safety rules implemented here:
    ///  - existing applications/sites are updated in place, never deleted;
    ///  - an existing application pool is reused (its configuration is logged and
    ///    checked for compatibility with the detected application type);
    ///  - parent-site bindings are never modified in ApplicationUnderSite mode;
    ///  - requested SeparateSite bindings are pre-checked against other sites.
    /// </summary>
    public class IisService
    {
        /// <summary>True when IIS administration is available on this machine.</summary>
        public bool IsIisAvailable()
        {
            try
            {
                using (ServerManager serverManager = new ServerManager())
                {
                    // Opening the ServerManager and touching the Sites collection is the
                    // cheapest reliable way to prove that IIS administration works.
                    int siteCount = serverManager.Sites.Count;
                    Log.Debug("IIS is accessible ({SiteCount} sites configured)", siteCount);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("IIS availability check failed: {Message}", ex.Message);
                return false;
            }
        }

        /// <summary>Names of all existing IIS sites, for the parent-site selector.</summary>
        public string[] ListSiteNames()
        {
            using (ServerManager serverManager = new ServerManager())
            {
                return serverManager.Sites.Select(s => s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        // ------------------------------------------------------------------
        // Live state for the confirmation dialog
        // ------------------------------------------------------------------

        /// <summary>
        /// Inspects IIS for the requested target and reports what exists and what
        /// IntraDeploy will do (create vs update in place).
        /// </summary>
        public IisLiveState GetLiveState(DeploymentRequest request)
        {
            var state = new IisLiveState();

            using (ServerManager serverManager = new ServerManager())
            {
                if (request.Iis.Mode == IisMode.ApplicationUnderSite)
                {
                    Site parent = GetSite(serverManager, request.Iis.ParentSiteName);
                    state.ParentSiteExists = parent != null;
                    if (parent == null)
                    {
                        state.Action = "Cannot deploy: parent site not found";
                        state.Details = "IIS site '" + request.Iis.ParentSiteName + "' does not exist. " +
                                        "ApplicationUnderSite mode requires an existing parent site.";
                        return state;
                    }

                    state.ParentSiteBindings = DescribeBindings(parent);

                    string appPath = request.Iis.NormalizedApplicationPath;
                    Application app = parent.Applications[appPath];
                    state.ApplicationExists = app != null;
                    if (app != null)
                    {
                        state.CurrentPhysicalPath = app.VirtualDirectories["/"]?.PhysicalPath;
                        state.CurrentApplicationPool = app.ApplicationPoolName;
                        state.Action = "Update existing IIS Application";
                        state.Details = "Application '" + appPath + "' already exists under site '" + parent.Name +
                                        "'. Its physical path and pool will be updated in place. " +
                                        "Nothing will be deleted.";
                    }
                    else
                    {
                        state.Action = "Create IIS Application";
                        state.Details = "Application '" + appPath + "' does not exist yet under site '" + parent.Name +
                                        "' and will be created. The parent site and its bindings are not modified.";
                    }
                }
                else
                {
                    Site site = GetSite(serverManager, request.Iis.SiteName);
                    state.SiteExists = site != null;
                    if (site != null)
                    {
                        Application root = site.Applications["/"];
                        state.CurrentPhysicalPath = root?.VirtualDirectories["/"]?.PhysicalPath;
                        state.CurrentApplicationPool = root?.ApplicationPoolName;
                        state.Action = "Update existing IIS Site";
                        state.Details = "Site '" + site.Name + "' already exists. Its root physical path, pool and " +
                                        "binding will be updated in place. Nothing will be deleted.";
                    }
                    else
                    {
                        state.Action = "Create IIS Site";
                        state.Details = "A new IIS site '" + request.Iis.SiteName + "' will be created with HTTP binding *:" +
                                        request.Iis.Port + ":.";
                        if (request.Iis.HasHttpsBinding)
                        {
                            state.Details += " HTTPS *:" + request.Iis.HttpsPort + ": will also be added" +
                                             (string.IsNullOrWhiteSpace(request.Iis.HttpsCertificateThumbprint)
                                                 ? "."
                                                 : " (cert …" + ShortThumb(request.Iis.HttpsCertificateThumbprint) + ").");
                        }
                    }

                    if (site != null && request.Iis.HasHttpsBinding)
                    {
                        state.Details += " HTTPS binding *:" + request.Iis.HttpsPort + ": will be ensured" +
                                         (string.IsNullOrWhiteSpace(request.Iis.HttpsCertificateThumbprint)
                                             ? "."
                                             : " (cert …" + ShortThumb(request.Iis.HttpsCertificateThumbprint) + ").");
                    }
                }

                state.ApplicationPoolExists = GetPool(serverManager, request.Iis.ApplicationPoolName) != null;
                if (state.ApplicationPoolExists && state.Details != null)
                {
                    state.Details += " Application pool '" + request.Iis.ApplicationPoolName + "' already exists and will be reused.";
                }
            }

            return state;
        }

        // ------------------------------------------------------------------
        // Pre-flight checks
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns a conflict description when the requested SeparateSite HTTP (and optional
        /// HTTPS) binding collides with another site, or null when clear. Not applicable to
        /// ApplicationUnderSite mode (no new binding is created there).
        /// </summary>
        public string GetPortConflict(DeploymentRequest request)
        {
            if (request.Iis.Mode != IisMode.SeparateSite)
            {
                return null;
            }

            string httpConflict = FindPortConflict(request.Iis.SiteName, request.Iis.Port, "http");
            if (httpConflict != null)
            {
                return httpConflict;
            }

            if (request.Iis.HasHttpsBinding)
            {
                return FindPortConflict(request.Iis.SiteName, request.Iis.HttpsPort, "https");
            }

            return null;
        }

        private static string FindPortConflict(string ourSiteName, int port, string preferredProtocolLabel)
        {
            string requested = "*:" + port + ":";
            using (ServerManager serverManager = new ServerManager())
            {
                foreach (Site site in serverManager.Sites)
                {
                    bool isOurSite = string.Equals(site.Name, ourSiteName, StringComparison.OrdinalIgnoreCase);
                    if (isOurSite)
                    {
                        continue;
                    }

                    foreach (Binding binding in site.Bindings)
                    {
                        if (binding.Protocol != "http" && binding.Protocol != "https")
                        {
                            continue;
                        }

                        string[] parts = binding.BindingInformation.Split(':');
                        int boundPort;
                        if (parts.Length < 2 || !int.TryParse(parts[1], out boundPort) || boundPort != port)
                        {
                            continue;
                        }

                        // A binding without a host header claims every host on that port.
                        bool existingIsWildcard = string.IsNullOrEmpty(parts.Length >= 3 ? parts[2] : null);
                        if (existingIsWildcard)
                        {
                            return "Port/binding conflict detected. Requested " + preferredProtocolLabel +
                                   " binding '" + requested + "' collides with site '" + site.Name +
                                   "' which already binds '" + binding.BindingInformation + "'.";
                        }
                    }
                }
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Certificates (LocalMachine)
        // ------------------------------------------------------------------

        /// <summary>
        /// Lists certificates from LocalMachine\{storeName} suitable for HTTPS bindings.
        /// Read-only; safe for UI population. Returns empty on store access failure.
        /// </summary>
        public IList<CertificateInfo> ListLocalMachineCertificates(string storeName = "My")
        {
            var list = new List<CertificateInfo>();
            string name = string.IsNullOrWhiteSpace(storeName) ? "My" : storeName.Trim();
            try
            {
                using (var store = new X509Store(name, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadOnly);
                    foreach (X509Certificate2 cert in store.Certificates)
                    {
                        if (cert == null || string.IsNullOrWhiteSpace(cert.Thumbprint))
                        {
                            continue;
                        }

                        // Prefer certs with a private key (required for IIS HTTPS).
                        if (!cert.HasPrivateKey)
                        {
                            continue;
                        }

                        list.Add(new CertificateInfo
                        {
                            Thumbprint = NormalizeThumbprint(cert.Thumbprint),
                            Subject = cert.Subject,
                            FriendlyName = cert.FriendlyName,
                            NotAfter = cert.NotAfter
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not list LocalMachine\\{Store} certificates", name);
            }

            return list
                .OrderBy(c => c.FriendlyName ?? c.Subject ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// True when a certificate with the given thumbprint exists in LocalMachine\{storeName}.
        /// Pure existence check for pre-flight; does not require private key.
        /// </summary>
        public static bool CertificateExists(string thumbprint, string storeName = "My")
        {
            string normalized = NormalizeThumbprint(thumbprint);
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            string name = string.IsNullOrWhiteSpace(storeName) ? "My" : storeName.Trim();
            try
            {
                using (var store = new X509Store(name, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadOnly);
                    X509Certificate2Collection found = store.Certificates.Find(
                        X509FindType.FindByThumbprint, normalized, validOnly: false);
                    return found != null && found.Count > 0;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "CertificateExists check failed for thumbprint …{Tip} in {Store}",
                    ShortThumb(normalized), name);
                return false;
            }
        }

        /// <summary>Normalizes a thumbprint to uppercase hex without spaces or colons.</summary>
        public static string NormalizeThumbprint(string thumbprint)
        {
            if (string.IsNullOrWhiteSpace(thumbprint))
            {
                return string.Empty;
            }

            var chars = thumbprint.Where(c => !char.IsWhiteSpace(c) && c != ':').ToArray();
            return new string(chars).ToUpperInvariant();
        }

        private static string ShortThumb(string thumbprint)
        {
            string n = NormalizeThumbprint(thumbprint);
            if (string.IsNullOrEmpty(n))
            {
                return string.Empty;
            }
            return n.Length <= 8 ? n : n.Substring(n.Length - 8);
        }

        // ------------------------------------------------------------------
        // Application pool
        // ------------------------------------------------------------------

        /// <summary>
        /// Ensures the application pool exists, configured for the detected application type:
        /// ASP.NET Framework → CLR v4.0 integrated; ASP.NET Core → No Managed Code.
        /// An existing pool is reused; its configuration is logged and a warning is raised
        /// when it appears incompatible with the application being deployed.
        /// Existing pools are NEVER mutated (CLR/pipeline left as-is).
        /// </summary>
        public string EnsureApplicationPool(DeploymentRequest request, ApplicationType applicationType)
        {
            string poolName = request.Iis.ApplicationPoolName;
            string expectedRuntime = ExpectedManagedRuntime(applicationType);

            using (ServerManager serverManager = new ServerManager())
            {
                ApplicationPool pool = GetPool(serverManager, poolName);
                if (pool != null)
                {
                    Log.Information("Reusing existing application pool '{Pool}' (state {State}, CLR '{Runtime}', pipeline {Pipeline})",
                        poolName, pool.State, string.IsNullOrEmpty(pool.ManagedRuntimeVersion) ? "<none>" : pool.ManagedRuntimeVersion,
                        pool.ManagedPipelineMode);

                    string mismatch = DescribeClrMismatch(poolName, pool.ManagedRuntimeVersion, expectedRuntime);
                    if (mismatch != null)
                    {
                        // Never mutate the pool. PreflightService surfaces this as a continue-required warning.
                        Log.Warning("{Mismatch}", mismatch);
                    }
                    return poolName;
                }

                Log.Information("Creating application pool '{Pool}' for application type {Type} (CLR '{Runtime}')",
                    poolName, applicationType, string.IsNullOrEmpty(expectedRuntime) ? "<none - No Managed Code>" : expectedRuntime);

                ApplicationPool created = serverManager.ApplicationPools.Add(poolName);
                created.ManagedRuntimeVersion = expectedRuntime; // "" == No Managed Code
                created.ManagedPipelineMode = ManagedPipelineMode.Integrated;
                created.StartMode = StartMode.AlwaysRunning;
                serverManager.CommitChanges();
                Log.Information("Application pool '{Pool}' created (CLR '{Runtime}', integrated, AlwaysRunning)",
                    poolName, string.IsNullOrEmpty(expectedRuntime) ? "<none>" : expectedRuntime);
                return poolName;
            }
        }

        /// <summary>
        /// Read-only: when the named pool already exists and its managed runtime does not match
        /// the application type, returns an operator-facing mismatch message; otherwise null.
        /// Does not create or mutate pools.
        /// </summary>
        public string GetApplicationPoolClrMismatch(string poolName, ApplicationType applicationType)
        {
            if (string.IsNullOrWhiteSpace(poolName))
            {
                return null;
            }

            string expectedRuntime = ExpectedManagedRuntime(applicationType);
            using (ServerManager serverManager = new ServerManager())
            {
                ApplicationPool pool = GetPool(serverManager, poolName);
                if (pool == null)
                {
                    return null;
                }

                return DescribeClrMismatch(poolName, pool.ManagedRuntimeVersion, expectedRuntime);
            }
        }

        /// <summary>Expected IIS managedRuntimeVersion: empty for ASP.NET Core, "v4.0" for Framework.</summary>
        public static string ExpectedManagedRuntime(ApplicationType applicationType)
        {
            return applicationType == ApplicationType.AspNetCore ? string.Empty : "v4.0";
        }

        /// <summary>Operator-facing CLR label for a managedRuntimeVersion value.</summary>
        public static string FormatManagedRuntimeLabel(string managedRuntimeVersion)
        {
            return string.IsNullOrEmpty(managedRuntimeVersion) ? "No Managed Code" : managedRuntimeVersion;
        }

        /// <summary>
        /// Returns a mismatch message when actual ≠ expected, otherwise null.
        /// Pure helper — safe for SmokeTest without IIS.
        /// </summary>
        public static string DescribeClrMismatch(string poolName, string actualRuntime, string expectedRuntime)
        {
            string actual = actualRuntime ?? string.Empty;
            string expected = expectedRuntime ?? string.Empty;
            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return "Application pool '" + poolName + "' is configured with CLR '" +
                   FormatManagedRuntimeLabel(actual) + "' but this application expects '" +
                   FormatManagedRuntimeLabel(expected) +
                   "'. IntraDeploy will not change an existing pool. Continue only if you accept that risk, " +
                   "or choose a different pool name / fix the pool in IIS Manager.";
        }

        // ------------------------------------------------------------------
        // Site / application configuration
        // ------------------------------------------------------------------

        /// <summary>Configures IIS for the selected deployment model.</summary>
        public void EnsureTarget(DeploymentRequest request)
        {
            if (request.Iis.Mode == IisMode.ApplicationUnderSite)
            {
                EnsureApplicationUnderSite(request);
            }
            else
            {
                EnsureSite(request);
            }
        }

        /// <summary>
        /// Ensures the IIS Application exists under the parent site and points at the
        /// deployed folder. The parent site itself is never created, deleted or re-bound.
        /// </summary>
        public void EnsureApplicationUnderSite(DeploymentRequest request)
        {
            string parentSiteName = request.Iis.ParentSiteName;
            string appPath = request.Iis.NormalizedApplicationPath;
            string targetPath = request.TargetFolder;
            string poolName = request.Iis.ApplicationPoolName;

            using (ServerManager serverManager = new ServerManager())
            {
                Site parent = GetSite(serverManager, parentSiteName);
                if (parent == null)
                {
                    throw new DeploymentStepException(DeploymentStep.ConfigureIis,
                        "Parent IIS site '" + parentSiteName + "' does not exist. ApplicationUnderSite mode " +
                        "does not create new sites; create the site in IIS Manager first or switch to Separate Site mode.");
                }

                Application app = parent.Applications[appPath];
                if (app == null)
                {
                    Log.Information("Creating IIS application '{AppPath}' under site '{Site}' with root path {Path}",
                        appPath, parentSiteName, targetPath);
                    app = parent.Applications.Add(appPath, targetPath);
                    app.ApplicationPoolName = poolName;
                    serverManager.CommitChanges();
                    Log.Information("IIS application '{AppPath}' created under '{Site}'", appPath, parentSiteName);
                    return;
                }

                Log.Information("Updating existing IIS application '{AppPath}' under site '{Site}' in place", appPath, parentSiteName);

                if (!string.Equals(app.ApplicationPoolName, poolName, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("Changing application '{AppPath}' pool: '{Old}' -> '{New}'",
                        appPath, app.ApplicationPoolName, poolName);
                    app.ApplicationPoolName = poolName;
                }

                VirtualDirectory rootVdir = app.VirtualDirectories["/"];
                if (rootVdir == null)
                {
                    app.VirtualDirectories.Add("/", targetPath);
                }
                else if (!string.Equals(NormalizePath(rootVdir.PhysicalPath), NormalizePath(targetPath), StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("Changing application '{AppPath}' physical path: '{Old}' -> '{New}'",
                        appPath, rootVdir.PhysicalPath, targetPath);
                    rootVdir.PhysicalPath = targetPath;
                }

                serverManager.CommitChanges();
                Log.Information("IIS application '{AppPath}' updated (path {Path}, pool '{Pool}')", appPath, targetPath, poolName);
            }
        }

        /// <summary>
        /// Ensures the separate IIS site exists and points at the deployed folder.
        /// Existing sites are updated in place. Only the root application path, pool and
        /// HTTP (and optional HTTPS) bindings are touched.
        /// </summary>
        public void EnsureSite(DeploymentRequest request)
        {
            string siteName = request.Iis.SiteName;
            string targetPath = request.TargetFolder;
            int port = request.Iis.Port;
            string poolName = request.Iis.ApplicationPoolName;

            using (ServerManager serverManager = new ServerManager())
            {
                Site site = GetSite(serverManager, siteName);

                if (site == null)
                {
                    Log.Information("Creating IIS site '{Site}' on port {Port} with root path {Path}",
                        siteName, port, targetPath);
                    string bindingInformation = "*:" + port + ":";
                    site = CreateSite(serverManager, siteName, bindingInformation, targetPath, poolName);
                    site.ServerAutoStart = true;
                    EnsureOptionalHttpsBinding(site, request.Iis);
                    serverManager.CommitChanges();
                    Log.Information("IIS site '{Site}' created", siteName);
                    return;
                }

                // Existing site: update in place, never delete.
                Log.Information("Updating existing IIS site '{Site}' in place", siteName);

                Application rootApp = site.Applications["/"];
                if (rootApp == null)
                {
                    rootApp = site.Applications.Add("/", targetPath);
                }

                if (!string.Equals(rootApp.ApplicationPoolName, poolName, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("Changing site '{Site}' pool: '{Old}' -> '{New}'", siteName, rootApp.ApplicationPoolName, poolName);
                    rootApp.ApplicationPoolName = poolName;
                }

                VirtualDirectory rootVdir = rootApp.VirtualDirectories["/"];
                if (rootVdir == null)
                {
                    rootVdir = rootApp.VirtualDirectories.Add("/", targetPath);
                }
                else if (!string.Equals(NormalizePath(rootVdir.PhysicalPath), NormalizePath(targetPath), StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("Changing site '{Site}' physical path: '{Old}' -> '{New}'",
                        siteName, rootVdir.PhysicalPath, targetPath);
                    rootVdir.PhysicalPath = targetPath;
                }

                EnsureHttpBinding(site, port);
                EnsureOptionalHttpsBinding(site, request.Iis);

                site.ServerAutoStart = true;
                serverManager.CommitChanges();
                Log.Information("IIS site '{Site}' updated (root path, pool '{Pool}', port {Port})",
                    siteName, poolName, port);
            }
        }

        /// <summary>
        /// Points the existing IIS application/site at <paramref name="newPhysicalPath"/> and
        /// recycles the pool. Does not delete any deployment folders. The target must already
        /// exist in IIS (rollback is not a create path).
        /// </summary>
        public void RetargetPhysicalPathAndRecycle(DeploymentRequest request, string newPhysicalPath)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }
            if (string.IsNullOrWhiteSpace(newPhysicalPath) || !Directory.Exists(newPhysicalPath))
            {
                throw new DeploymentStepException(DeploymentStep.ConfigureIis,
                    "Rollback target folder does not exist: " + newPhysicalPath);
            }

            string targetPath = Path.GetFullPath(newPhysicalPath);
            Log.Information("Rollback: retargeting IIS physical path to {Path} for app {App}",
                targetPath, request.ApplicationName);

            using (ServerManager serverManager = new ServerManager())
            {
                if (request.Iis.Mode == IisMode.ApplicationUnderSite)
                {
                    Site parent = GetSite(serverManager, request.Iis.ParentSiteName);
                    if (parent == null)
                    {
                        throw new DeploymentStepException(DeploymentStep.ConfigureIis,
                            "Parent IIS site '" + request.Iis.ParentSiteName + "' does not exist.");
                    }

                    string appPath = request.Iis.NormalizedApplicationPath;
                    Application app = parent.Applications[appPath];
                    if (app == null)
                    {
                        throw new DeploymentStepException(DeploymentStep.ConfigureIis,
                            "IIS application '" + appPath + "' under site '" + parent.Name +
                            "' does not exist. Deploy once before rolling back.");
                    }

                    VirtualDirectory rootVdir = app.VirtualDirectories["/"];
                    if (rootVdir == null)
                    {
                        app.VirtualDirectories.Add("/", targetPath);
                    }
                    else
                    {
                        Log.Information("Rollback: changing application '{AppPath}' physical path: '{Old}' -> '{New}'",
                            appPath, rootVdir.PhysicalPath, targetPath);
                        rootVdir.PhysicalPath = targetPath;
                    }
                }
                else
                {
                    Site site = GetSite(serverManager, request.Iis.SiteName);
                    if (site == null)
                    {
                        throw new DeploymentStepException(DeploymentStep.ConfigureIis,
                            "IIS site '" + request.Iis.SiteName + "' does not exist. Deploy once before rolling back.");
                    }

                    Application rootApp = site.Applications["/"];
                    if (rootApp == null)
                    {
                        throw new DeploymentStepException(DeploymentStep.ConfigureIis,
                            "IIS site '" + site.Name + "' has no root application.");
                    }

                    VirtualDirectory rootVdir = rootApp.VirtualDirectories["/"];
                    if (rootVdir == null)
                    {
                        rootApp.VirtualDirectories.Add("/", targetPath);
                    }
                    else
                    {
                        Log.Information("Rollback: changing site '{Site}' physical path: '{Old}' -> '{New}'",
                            site.Name, rootVdir.PhysicalPath, targetPath);
                        rootVdir.PhysicalPath = targetPath;
                    }
                }

                serverManager.CommitChanges();
            }

            RecycleAndStart(request);
            Log.Information("Rollback: IIS retargeted to {Path} and recycled", targetPath);
        }

        /// <summary>
        /// Returns the current physical path of the IIS target for this request, or null
        /// when the site/app does not exist yet.
        /// </summary>
        public string GetCurrentPhysicalPath(DeploymentRequest request)
        {
            if (request == null || request.Iis == null)
            {
                return null;
            }

            using (ServerManager serverManager = new ServerManager())
            {
                if (request.Iis.Mode == IisMode.ApplicationUnderSite)
                {
                    Site parent = GetSite(serverManager, request.Iis.ParentSiteName);
                    if (parent == null)
                    {
                        return null;
                    }

                    Application app = parent.Applications[request.Iis.NormalizedApplicationPath];
                    return app?.VirtualDirectories["/"]?.PhysicalPath;
                }

                Site site = GetSite(serverManager, request.Iis.SiteName);
                if (site == null)
                {
                    return null;
                }

                Application root = site.Applications["/"];
                return root?.VirtualDirectories["/"]?.PhysicalPath;
            }
        }

        // ------------------------------------------------------------------
        // Start / stop / recycle
        // ------------------------------------------------------------------

        /// <summary>
        /// Stops the application being replaced: the pool of the target application and,
        /// in SeparateSite mode, the site itself. Used before an operator-confirmed
        /// same-version folder overwrite so the live folder is not deleted under a
        /// running application. Stopping is best effort; failures are logged and the
        /// most recent state is returned so the caller can decide.
        /// </summary>
        public ObjectState StopTargetApplication(DeploymentRequest request)
        {
            try
            {
                using (ServerManager serverManager = new ServerManager())
                {
                    ApplicationPool pool = GetPool(serverManager, request.Iis.ApplicationPoolName);
                    ObjectState poolState = ObjectState.Unknown;
                    if (pool != null && (pool.State == ObjectState.Started || pool.State == ObjectState.Starting))
                    {
                        Log.Information("Stopping application pool '{Pool}' before folder overwrite", pool.Name);
                        poolState = pool.Stop();
                        poolState = WaitForState(() => GetPool(serverManager, pool.Name)?.State ?? ObjectState.Unknown,
                            s => s != ObjectState.Started && s != ObjectState.Starting, "pool '" + pool.Name + "' stop");
                    }

                    if (request.Iis.Mode == IisMode.SeparateSite)
                    {
                        Site site = GetSite(serverManager, request.Iis.SiteName);
                        if (site != null && (site.State == ObjectState.Started || site.State == ObjectState.Starting))
                        {
                            Log.Information("Stopping site '{Site}' before folder overwrite", site.Name);
                            site.Stop();
                            WaitForState(() => GetSite(serverManager, site.Name)?.State ?? ObjectState.Unknown,
                                s => s != ObjectState.Started && s != ObjectState.Starting, "site '" + site.Name + "' stop");
                        }
                    }

                    return poolState;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not stop the target application before overwrite.");
                throw new DeploymentStepException(DeploymentStep.ConfigureIis,
                    "The running application could not be stopped before overwriting its deployment folder. " +
                    "Overwriting a live application folder is not safe; deployment was stopped. " +
                    "Details: " + ex.Message, ex);
            }
        }

        /// <summary>Starts the application again after a stop.</summary>
        public void StartTargetApplication(DeploymentRequest request)
        {
            using (ServerManager serverManager = new ServerManager())
            {
                ApplicationPool pool = GetPool(serverManager, request.Iis.ApplicationPoolName);
                if (pool != null && pool.State != ObjectState.Started)
                {
                    Log.Information("Starting application pool '{Pool}'", pool.Name);
                    pool.Start();
                }

                if (request.Iis.Mode == IisMode.SeparateSite)
                {
                    Site site = GetSite(serverManager, request.Iis.SiteName);
                    if (site != null && site.State != ObjectState.Started)
                    {
                        Log.Information("Starting site '{Site}'", site.Name);
                        site.Start();
                    }
                }
            }
        }

        /// <summary>
        /// Recycles the pool and ensures the target is serving. In ApplicationUnderSite mode
        /// the parent site is never stopped or started; only the application's pool is recycled.
        /// </summary>
        public void RecycleAndStart(DeploymentRequest request)
        {
            using (ServerManager serverManager = new ServerManager())
            {
                ApplicationPool pool = GetPool(serverManager, request.Iis.ApplicationPoolName);
                if (pool == null)
                {
                    throw new DeploymentStepException(DeploymentStep.StartAndRecycle,
                        "Application pool '" + request.Iis.ApplicationPoolName + "' could not be found.");
                }

                if (pool.State == ObjectState.Started || pool.State == ObjectState.Starting)
                {
                    Log.Information("Recycling application pool '{Pool}'", pool.Name);
                    ObjectState recycleState = pool.Recycle();
                    Log.Information("Recycle result for '{Pool}': {State}", pool.Name, recycleState);
                }
                else
                {
                    Log.Information("Starting application pool '{Pool}' (state was {State})", pool.Name, pool.State);
                    ObjectState startState = pool.Start();
                    Log.Information("Start result for '{Pool}': {State}", pool.Name, startState);
                }

                if (request.Iis.Mode == IisMode.SeparateSite)
                {
                    Site site = GetSite(serverManager, request.Iis.SiteName)
                        ?? throw new DeploymentStepException(DeploymentStep.StartAndRecycle,
                            "IIS site '" + request.Iis.SiteName + "' disappeared unexpectedly during deployment.");

                    if (site.State != ObjectState.Started)
                    {
                        Log.Information("Starting site '{Site}' (state was {State})", site.Name, site.State);
                        ObjectState siteState = site.Start();
                        Log.Information("Start result for site '{Site}': {State}", site.Name, siteState);
                    }
                    else
                    {
                        Log.Information("Site '{Site}' already started", site.Name);
                    }
                }
                else
                {
                    Site parent = GetSite(serverManager, request.Iis.ParentSiteName);
                    if (parent == null)
                    {
                        throw new DeploymentStepException(DeploymentStep.StartAndRecycle,
                            "Parent IIS site '" + request.Iis.ParentSiteName + "' disappeared unexpectedly during deployment.");
                    }
                    if (parent.State != ObjectState.Started)
                    {
                        Log.Warning("Parent site '{Site}' is not running (state {State}); the application will not " +
                                    "answer requests until the site is started.", parent.Name, parent.State);
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // URL construction
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds the URL the deployed application should answer on, derived from the
        /// deployment model and the ACTUAL IIS bindings (not from the operator's port field):
        ///
        ///   ApplicationUnderSite: parent-site binding + application path → http://localhost/BiCore
        ///   SeparateSite:         the site's own binding          → http://localhost:8099/
        ///
        /// Host header bindings are respected where present; otherwise "localhost" is used.
        /// </summary>
        public string BuildUrl(DeploymentRequest request)
        {
            using (ServerManager serverManager = new ServerManager())
            {
                Site site = request.Iis.Mode == IisMode.ApplicationUnderSite
                    ? GetSite(serverManager, request.Iis.ParentSiteName)
                    : GetSite(serverManager, request.Iis.SiteName);

                if (site == null)
                {
                    // Site vanished mid-run; fall back to the configured values rather than crashing.
                    return request.Iis.Mode == IisMode.ApplicationUnderSite
                        ? "http://localhost" + request.Iis.NormalizedApplicationPath
                        : "http://localhost:" + request.Iis.Port + "/";
                }

                Binding binding = SelectBestBinding(site);
                if (binding == null)
                {
                    return request.Iis.Mode == IisMode.ApplicationUnderSite
                        ? "http://localhost" + request.Iis.NormalizedApplicationPath
                        : "http://localhost:" + request.Iis.Port + "/";
                }

                string url = ComposeUrlFromBinding(binding);
                if (request.Iis.Mode == IisMode.ApplicationUnderSite)
                {
                    string path = request.Iis.NormalizedApplicationPath.TrimStart('/');
                    url = url.TrimEnd('/') + "/" + path;
                }
                return url;
            }
        }

        /// <summary>Prefers HTTP wildcard/localhost bindings; falls back to the first HTTP, then any binding.</summary>
        public static Binding SelectBestBinding(Site site)
        {
            List<Binding> http = site.Bindings.Where(b => b.Protocol == "http").ToList();
            if (http.Count > 0)
            {
                return http.FirstOrDefault(b => string.IsNullOrEmpty(GetHost(b)))
                    ?? http.First();
            }
            return site.Bindings.FirstOrDefault(b => b.Protocol == "https")
                ?? site.Bindings.FirstOrDefault();
        }

        /// <summary>Composes scheme://host[:port]/ from a binding; omits :80 for http and :443 for https.</summary>
        public static string ComposeUrlFromBinding(Binding binding)
        {
            string scheme = string.Equals(binding.Protocol, "https", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
            string host = GetHost(binding);
            if (string.IsNullOrWhiteSpace(host))
            {
                host = "localhost";
            }

            int port;
            int.TryParse((binding.BindingInformation ?? string.Empty).Split(':').ElementAtOrDefault(1), out port);
            bool omitPort = (scheme == "http" && port == 80) || (scheme == "https" && port == 443);

            return scheme + "://" + host + (omitPort ? string.Empty : ":" + port) + "/";
        }

        private static string GetHost(Binding binding)
        {
            string[] parts = (binding.BindingInformation ?? string.Empty).Split(':');
            return parts.Length >= 3 ? parts[2] : null;
        }

        private static string DescribeBindings(Site site)
        {
            var descriptions = site.Bindings.Select(b => b.Protocol + " " + b.BindingInformation);
            return string.Join(", ", descriptions);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static ObjectState WaitForState(Func<ObjectState> readState, Func<ObjectState, bool> done, string label)
        {
            const int maxAttempts = 20; // ~10 seconds
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                ObjectState state = readState();
                if (done(state))
                {
                    return state;
                }
                Thread.Sleep(500);
            }
            Log.Warning("Timed out waiting for {Label} to reach the expected state", label);
            return readState();
        }

        private static void EnsureHttpBinding(Site site, int port)
        {
            string wanted = "*:" + port + ":";
            Binding existingSamePort = site.Bindings.FirstOrDefault(b =>
                (b.Protocol == "http") &&
                string.Equals(b.BindingInformation, wanted, StringComparison.OrdinalIgnoreCase));

            if (existingSamePort != null)
            {
                Log.Information("Binding '{Binding}' already present on site '{Site}'", wanted, site.Name);
                return;
            }

            Binding conflict = site.Bindings.FirstOrDefault(b =>
                (b.Protocol == "http" || b.Protocol == "https") &&
                b.BindingInformation.Split(':').Length >= 2 &&
                b.BindingInformation.Split(':')[1] == port.ToString());

            if (conflict != null)
            {
                // Different binding string on the same port: leave it alone and report.
                Log.Warning("Site '{Site}' has binding '{Binding}' on port {Port}; wanted '{Wanted}'. " +
                            "Leaving existing binding untouched.",
                    site.Name, conflict.BindingInformation, port, wanted);
                throw new DeploymentStepException(DeploymentStep.ConfigureIis,
                    "Site '" + site.Name + "' already binds port " + port + " as '" + conflict.BindingInformation +
                    "'. A second binding on the same port with a different host header cannot be added. " +
                    "Choose another port or adjust the binding manually in IIS Manager.");
            }

            Log.Information("Adding binding '{Binding}' to site '{Site}'", wanted, site.Name);
            site.Bindings.Add(wanted, "http");
        }

        private static void EnsureOptionalHttpsBinding(Site site, IisSettings iis)
        {
            if (iis == null || !iis.HasHttpsBinding)
            {
                return;
            }

            EnsureHttpsBinding(site, iis.HttpsPort, iis.HttpsCertificateThumbprint, iis.ResolvedHttpsCertificateStoreName);
        }

        /// <summary>
        /// Ensures an HTTPS binding *:{port}: with the given LocalMachine certificate.
        /// Updates certificate hash on an existing matching binding when needed.
        /// </summary>
        public static void EnsureHttpsBinding(Site site, int httpsPort, string thumbprint, string storeName)
        {
            if (site == null)
            {
                throw new ArgumentNullException("site");
            }
            if (httpsPort < 1 || httpsPort > 65535)
            {
                throw new DeploymentStepException(DeploymentStep.ConfigureIis,
                    "HTTPS port must be between 1 and 65535.");
            }

            string normalized = NormalizeThumbprint(thumbprint);
            if (string.IsNullOrEmpty(normalized))
            {
                throw new DeploymentStepException(DeploymentStep.ConfigureIis,
                    "HTTPS binding requires a certificate thumbprint from LocalMachine\\" +
                    (string.IsNullOrWhiteSpace(storeName) ? "My" : storeName.Trim()) + ".");
            }

            if (!CertificateExists(normalized, storeName))
            {
                throw new DeploymentStepException(DeploymentStep.ConfigureIis,
                    "Certificate with thumbprint …" + ShortThumb(normalized) +
                    " was not found in LocalMachine\\" +
                    (string.IsNullOrWhiteSpace(storeName) ? "My" : storeName.Trim()) + ".");
            }

            byte[] hash = ThumbprintToBytes(normalized);
            string store = string.IsNullOrWhiteSpace(storeName) ? "My" : storeName.Trim();
            string wanted = "*:" + httpsPort + ":";

            Binding existingHttps = site.Bindings.FirstOrDefault(b =>
                b.Protocol == "https" &&
                string.Equals(b.BindingInformation, wanted, StringComparison.OrdinalIgnoreCase));

            if (existingHttps != null)
            {
                existingHttps.CertificateHash = hash;
                existingHttps.CertificateStoreName = store;
                Log.Information("HTTPS binding '{Binding}' already present on site '{Site}'; certificate refreshed (…{Tip})",
                    wanted, site.Name, ShortThumb(normalized));
                return;
            }

            Binding conflict = site.Bindings.FirstOrDefault(b =>
                (b.Protocol == "http" || b.Protocol == "https") &&
                b.BindingInformation.Split(':').Length >= 2 &&
                b.BindingInformation.Split(':')[1] == httpsPort.ToString());

            if (conflict != null)
            {
                throw new DeploymentStepException(DeploymentStep.ConfigureIis,
                    "Site '" + site.Name + "' already binds port " + httpsPort + " as '" +
                    conflict.Protocol + " " + conflict.BindingInformation +
                    "'. Choose another HTTPS port or adjust the binding in IIS Manager.");
            }

            Log.Information("Adding HTTPS binding '{Binding}' to site '{Site}' (store {Store}, cert …{Tip})",
                wanted, site.Name, store, ShortThumb(normalized));
            site.Bindings.Add(wanted, hash, store);
        }

        private static byte[] ThumbprintToBytes(string normalizedThumbprint)
        {
            if (normalizedThumbprint.Length % 2 != 0)
            {
                throw new DeploymentStepException(DeploymentStep.ConfigureIis,
                    "Certificate thumbprint has an invalid length.");
            }

            byte[] bytes = new byte[normalizedThumbprint.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(normalizedThumbprint.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        /// <summary>
        /// Creates the site; the root application pool is assigned explicitly afterwards so we
        /// do not depend on SiteCollection.Add overload differences between MWA builds.
        /// </summary>
        private static Site CreateSite(
            ServerManager serverManager,
            string siteName,
            string bindingInformation,
            string physicalPath,
            string poolName)
        {
            Site site = serverManager.Sites.Add(siteName, "http", bindingInformation, physicalPath);
            site.Applications[0].ApplicationPoolName = poolName;
            return site;
        }

        private static Site GetSite(ServerManager serverManager, string siteName)
        {
            return serverManager.Sites.FirstOrDefault(s =>
                string.Equals(s.Name, siteName, StringComparison.OrdinalIgnoreCase));
        }

        private static ApplicationPool GetPool(ServerManager serverManager, string poolName)
        {
            return serverManager.ApplicationPools.FirstOrDefault(p =>
                string.Equals(p.Name, poolName, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizePath(string path)
        {
            return path == null ? string.Empty : path.TrimEnd('\\', '/').TrimEnd().ToLowerInvariant();
        }
    }
}
