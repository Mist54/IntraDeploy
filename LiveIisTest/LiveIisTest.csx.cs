using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Web.Administration;
using IntraDeploy.Models;
using IntraDeploy.Services;

namespace LiveIisTest
{
    /// <summary>
    /// Live IIS tests (require elevation). Creates temporary, clearly named IIS objects
    /// and removes them again at the end. Performs NO database operations.
    ///   TEST A: deploy app under Default Web Site, verify URL/physical path.
    ///   TEST B: redeploy version 1.1, verify in-place update and that NO new site appeared.
   ///   TEST C: separate site mode on a test port.
   ///   TEST F: ASP.NET Core app type creates a No Managed Code pool.
   ///   TEST G: SeparateSite HTTPS binding when a LocalMachine\My cert exists (else SKIP).
   /// </summary>
    internal static class Program
    {
        private static int _failures;
        private const string PoolName = "IntraDeployTestPool";
        private const string AppPath = "/IntraDeployTestApp";
        private const string SiteName = "IntraDeployTestSite";

        private static void Main()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "IntraDeployIisTest_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(tempRoot);

            try
            {
                TestA_CreateApplicationUnderSite(tempRoot);
                TestB_UpdateApplicationInPlace(tempRoot);
                TestC_SeparateSite(tempRoot);
                TestF_NoManagedCodePool(tempRoot);
                TestG_HttpsBindingIfCertAvailable(tempRoot);
            }
            finally
            {
                Cleanup();
                try { Directory.Delete(tempRoot, true); } catch { }
            }

            Console.WriteLine(_failures == 0 ? "ALL LIVE IIS TESTS PASSED" : _failures + " TEST(S) FAILED");
            Environment.ExitCode = _failures == 0 ? 0 : 1;
        }

        private static void TestA_CreateApplicationUnderSite(string root)
        {
            string source = CreateFrameworkApp(root, "A");
            var request = BuildAppUnderSiteRequest(source, root, "1.0");
            var iis = new IisService();

            iis.EnsureApplicationPool(request, ApplicationType.AspNetFramework);
            iis.EnsureApplicationUnderSite(request);

            using (var sm = new ServerManager())
            {
                Site parent = sm.Sites.FirstOrDefault(s => s.Name == "Default Web Site");
                if (parent == null)
                {
                    Console.WriteLine("SKIP: Default Web Site not present on this machine.");
                    return;
                }

                Application app = parent.Applications[AppPath];
                Check(app != null, "TEST A: application /IntraDeployTestApp exists under Default Web Site");
                if (app == null) return;

                string physical = app.VirtualDirectories["/"].PhysicalPath;
                Check(string.Equals(Normalize(physical), Normalize(request.TargetFolder), StringComparison.OrdinalIgnoreCase),
                    "TEST A: physical path is " + request.TargetFolder);
                Check(app.ApplicationPoolName == PoolName, "TEST A: application pool assigned");

                int sitesBefore = sm.Sites.Count;
                Check(!sm.Sites.Any(s => s.Name == "IntraDeployTestApp"), "TEST A: no new site named after the app");
            }

            string url = iis.BuildUrl(request);
            Check(url.EndsWith("/IntraDeployTestApp", StringComparison.OrdinalIgnoreCase),
                "TEST A: BuildUrl ends with the application path (" + url + ")");
        }

        private static void TestB_UpdateApplicationInPlace(string root)
        {
            string source = CreateFrameworkApp(root, "B");
            var request = BuildAppUnderSiteRequest(source, root, "1.1");
            var iis = new IisService();

            using (var sm = new ServerManager())
            {
                Site parent = sm.Sites.FirstOrDefault(s => s.Name == "Default Web Site");
                if (parent == null)
                {
                    Console.WriteLine("SKIP: Default Web Site not present on this machine.");
                    return;
                }
            }

            int siteCountBefore;
            using (var sm = new ServerManager()) { siteCountBefore = sm.Sites.Count; }

            iis.EnsureApplicationPool(request, ApplicationType.AspNetFramework);
            iis.EnsureApplicationUnderSite(request);

            using (var sm = new ServerManager())
            {
                Site parent = sm.Sites.FirstOrDefault(s => s.Name == "Default Web Site");
                Application app = parent.Applications[AppPath];
                Check(app != null, "TEST B: application still exists");
                if (app == null) return;

                string physical = app.VirtualDirectories["/"].PhysicalPath;
                Check(string.Equals(Normalize(physical), Normalize(request.TargetFolder), StringComparison.OrdinalIgnoreCase),
                    "TEST B: physical path updated to " + request.TargetFolder);
                Check(sm.Sites.Count == siteCountBefore, "TEST B: no new IIS site was created");
                Check(!sm.Sites.Any(s => s.Name == "IntraDeployTestApp"), "TEST B: no site named IntraDeployTestApp exists");
            }
        }

        private static void TestC_SeparateSite(string root)
        {
            string source = CreateFrameworkApp(root, "C");
            var request = new DeploymentRequest
            {
                ApplicationName = SiteName,
                Version = "1.0",
                SourceFolder = source,
                DeploymentRoot = Path.Combine(root, "Applications"),
                Iis = new IisSettings
                {
                    Mode = IisMode.SeparateSite,
                    SiteName = SiteName,
                    ApplicationPoolName = PoolName,
                    Port = 8099,
                    DeploymentRoot = Path.Combine(root, "Applications")
                },
                Database = new DatabaseSettings { Mode = DatabaseMode.UseExisting },
                RunId = "liveC"
            };

            var iis = new IisService();
            iis.EnsureApplicationPool(request, ApplicationType.AspNetFramework);
            iis.EnsureTarget(request);

            using (var sm = new ServerManager())
            {
                Site site = sm.Sites.FirstOrDefault(s => s.Name == SiteName);
                Check(site != null, "TEST C: site IntraDeployTestSite created");
                if (site == null) return;

                string physical = site.Applications["/"].VirtualDirectories["/"].PhysicalPath;
                Check(string.Equals(Normalize(physical), Normalize(request.TargetFolder), StringComparison.OrdinalIgnoreCase),
                    "TEST C: site physical path correct");
                Check(site.Bindings.Any(b => b.BindingInformation == "*:8099:"),
                    "TEST C: binding *:8099: present");

                string url = iis.BuildUrl(request);
                Check(url.StartsWith("http://localhost:8099", StringComparison.OrdinalIgnoreCase),
                    "TEST C: BuildUrl uses the site port (" + url + ")");
            }
        }

        private static void TestF_NoManagedCodePool(string root)
        {
            string source = CreateCoreApp(root, "F");
            var request = BuildAppUnderSiteRequest(source, root, "1.0-core");
            request.Iis.ApplicationPoolName = PoolName + "Core";

            var iis = new IisService();
            iis.EnsureApplicationPool(request, ApplicationType.AspNetCore);

            using (var sm = new ServerManager())
            {
                var pool = sm.ApplicationPools.FirstOrDefault(p => p.Name == PoolName + "Core");
                Check(pool != null, "TEST F: pool created for ASP.NET Core app");
                if (pool == null) return;

                Check(string.IsNullOrEmpty(pool.ManagedRuntimeVersion),
                    "TEST F: ASP.NET Core pool uses No Managed Code (CLR '" +
                    (pool.ManagedRuntimeVersion ?? "<none>") + "')");
                Check(pool.ManagedPipelineMode == ManagedPipelineMode.Integrated, "TEST F: integrated pipeline");
            }
        }

        /// <summary>
        /// Creates a SeparateSite with optional HTTPS when LocalMachine\My has a cert with a private key.
        /// Skips (does not fail) when no suitable certificate is available.
        /// </summary>
        private static void TestG_HttpsBindingIfCertAvailable(string root)
        {
            var iis = new IisService();
            IList<CertificateInfo> certs = iis.ListLocalMachineCertificates("My");
            if (certs == null || certs.Count == 0)
            {
                Console.WriteLine("SKIP: TEST G HTTPS binding — no LocalMachine\\My certificate with private key. " +
                                  "Install/import a cert (or create a self-signed one) and re-run elevated to exercise HTTPS.");
                return;
            }

            CertificateInfo cert = certs[0];
            const int httpPort = 8199;
            const int httpsPort = 8198;
            string source = CreateFrameworkApp(root, "G");
            var request = new DeploymentRequest
            {
                ApplicationName = SiteName,
                Version = "1.0-https",
                SourceFolder = source,
                DeploymentRoot = Path.Combine(root, "Applications"),
                Iis = new IisSettings
                {
                    Mode = IisMode.SeparateSite,
                    SiteName = SiteName,
                    ApplicationPoolName = PoolName,
                    Port = httpPort,
                    HttpsPort = httpsPort,
                    HttpsCertificateThumbprint = cert.Thumbprint,
                    HttpsCertificateStoreName = "My",
                    DeploymentRoot = Path.Combine(root, "Applications")
                },
                Database = new DatabaseSettings { Mode = DatabaseMode.UseExisting },
                RunId = "liveG"
            };

            // Ensure target folder exists for physical path.
            Directory.CreateDirectory(request.TargetFolder);
            File.WriteAllText(Path.Combine(request.TargetFolder, "index.html"), "<h1>G</h1>");

            iis.EnsureApplicationPool(request, ApplicationType.AspNetFramework);
            iis.EnsureTarget(request);

            using (var sm = new ServerManager())
            {
                Site site = sm.Sites.FirstOrDefault(s => s.Name == SiteName);
                Check(site != null, "TEST G: site created/updated with HTTPS");
                if (site == null)
                {
                    return;
                }

                Check(site.Bindings.Any(b => b.Protocol == "http" && b.BindingInformation == "*:" + httpPort + ":"),
                    "TEST G: HTTP binding present");
                Binding https = site.Bindings.FirstOrDefault(b =>
                    b.Protocol == "https" && b.BindingInformation == "*:" + httpsPort + ":");
                Check(https != null, "TEST G: HTTPS binding *:" + httpsPort + ": present");
                if (https != null && https.CertificateHash != null)
                {
                    Check(https.CertificateHash.Length > 0, "TEST G: HTTPS binding has certificate hash");
                }
            }
        }

        // ------------------------------------------------------------------

        private static DeploymentRequest BuildAppUnderSiteRequest(string source, string root, string version)
        {
            return new DeploymentRequest
            {
                ApplicationName = "IntraDeployTestApp",
                Version = version,
                SourceFolder = source,
                DeploymentRoot = Path.Combine(root, "Applications"),
                Iis = new IisSettings
                {
                    Mode = IisMode.ApplicationUnderSite,
                    ParentSiteName = "Default Web Site",
                    ApplicationPath = AppPath,
                    ApplicationPoolName = PoolName,
                    DeploymentRoot = Path.Combine(root, "Applications")
                },
                Database = new DatabaseSettings { Mode = DatabaseMode.UseExisting },
                RunId = "live"
            };
        }

        private static string CreateFrameworkApp(string root, string tag)
        {
            string folder = Path.Combine(root, "Fx" + tag + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "web.config"),
                "<configuration>\r\n  <system.web>\r\n    <httpRuntime targetFramework=\"4.8\" />\r\n  </system.web>\r\n</configuration>");
            File.WriteAllText(Path.Combine(folder, "index.html"), "<h1>IntraDeploy live test " + tag + "</h1>");
            return folder;
        }

        private static string CreateCoreApp(string root, string tag)
        {
            string folder = Path.Combine(root, "Core" + tag + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "App.runtimeconfig.json"), "{ \"runtimeOptions\": {} }");
            return folder;
        }

        private static void Cleanup()
        {
            try
            {
                using (var sm = new ServerManager())
                {
                    var poolCore = sm.ApplicationPools.FirstOrDefault(p => p.Name == PoolName + "Core");
                    if (poolCore != null)
                    {
                        sm.ApplicationPools.Remove(poolCore);
                    }

                    var pool = sm.ApplicationPools.FirstOrDefault(p => p.Name == PoolName);
                    if (pool != null)
                    {
                        sm.ApplicationPools.Remove(pool);
                    }

                    Site testSite = sm.Sites.FirstOrDefault(s => s.Name == SiteName);
                    if (testSite != null)
                    {
                        sm.Sites.Remove(testSite);
                    }

                    Site parent = sm.Sites.FirstOrDefault(s => s.Name == "Default Web Site");
                    if (parent != null)
                    {
                        Application app = parent.Applications[AppPath];
                        if (app != null)
                        {
                            parent.Applications.Remove(app);
                        }
                    }

                    sm.CommitChanges();
                    Console.WriteLine("Cleanup: removed test application, site and pools.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Cleanup warning (remove manually if needed): " + ex.Message);
            }
        }

        private static string Normalize(string path)
        {
            return path == null ? string.Empty : path.TrimEnd('\\', '/').ToLowerInvariant();
        }

        private static void Check(bool condition, string label)
        {
            Console.WriteLine((condition ? "PASS: " : "FAIL: ") + label);
            if (!condition) _failures++;
        }
    }
}
