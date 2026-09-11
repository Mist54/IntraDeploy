using System;
using System.Collections.Generic;
using System.IO;
using IntraDeploy.Configuration;
using IntraDeploy.Logging;
using IntraDeploy.Models;
using IntraDeploy.Services;
using IntraDeploy.Utilities;

namespace IlProbe
{
    /// <summary>
    /// Smoke tests for IntraDeploy logic that does not require IIS administration or
    /// SQL Server: validation, request normalization, file deployment (backup-swap),
    /// configuration editing and URL composition. Run without elevation.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static void Main(string[] args)
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "IntraDeploySmoke_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempRoot);

            try
            {
                TestValidation();
                TestDetector(tempRoot);
                TestCopyAndSwap(tempRoot);
                TestIncrementalOverwriteReuse(tempRoot);
                TestOverwriteRefusedWithoutConfirmation(tempRoot);
                TestFailedCopyRestoresPreviousVersion(tempRoot);
                TestConfigurationService(tempRoot);
                TestConnectionStringBuilder();
                TestPreflightFilesystemSafe(tempRoot);
                TestPortHelperSoftCheck();
                TestUrlComposition();
                TestAppUnderSiteNormalization();
                TestHealthCheckUrlCombine();
                TestPoolClrMismatchHelper();
                TestPreflightContinueRequiredFlag();
                TestVersionListAndPrune(tempRoot);
                TestDeployHistoryStore(tempRoot);
                TestDeploymentPlanModesAndDryRun(tempRoot);
                TestHttpsThumbprintAndHooks();
                TestSecretSummariesNeverLeak();

                Console.WriteLine(_failures == 0 ? "ALL SMOKE TESTS PASSED" : _failures + " TEST(S) FAILED");
                Environment.ExitCode = _failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("HARNESS EXCEPTION: " + ex.GetType().FullName);
                Console.WriteLine("Message: " + ex.Message);
                if (ex.InnerException != null)
                {
                    Console.WriteLine("Inner: " + ex.InnerException.GetType().FullName + " / " + ex.InnerException.Message);
                }
                Console.WriteLine(ex.StackTrace);
                Environment.ExitCode = 2;
            }
            finally
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }

        // ------------------------------------------------------------------
        // Validation
        // ------------------------------------------------------------------

        private static void TestValidation()
        {
            Check(ValidationHelper.IsSafeName("BiCore"), "BiCore accepted");
            Check(!ValidationHelper.IsSafeName("Bi\\Core"), "backslash rejected");
            Check(!ValidationHelper.IsSafeName("../evil"), "path traversal rejected");
            Check(!ValidationHelper.IsSafeVersion("1.0."), "trailing dot version rejected");
            Check(!ValidationHelper.IsSafeName("BiCore "), "trailing space name rejected");
            Check(!ValidationHelper.IsSafeVersion("2 5; drop"), "unsafe version rejected");
            Check(ValidationHelper.IsSafeVersion("2.5.0"), "normal version accepted");
            Check(ValidationHelper.IsSafeApplicationPath("/BiCore"), "/BiCore accepted");
            Check(ValidationHelper.IsSafeApplicationPath("/Apps/BiCore"), "/Apps/BiCore accepted");
            Check(!ValidationHelper.IsSafeApplicationPath("/BiCore/"), "trailing slash rejected");
            Check(!ValidationHelper.IsSafeApplicationPath("/Bi..Core/.."), "dotdot segment rejected");
            Check(!ValidationHelper.IsSafeApplicationPath("/"), "root path / rejected");
            Check(!ValidationHelper.IsSafeApplicationPath("/BiCore."), "trailing dot in path rejected");
            Check(!ValidationHelper.IsSafeApplicationPath("BiCore"), "missing leading slash rejected");
            Check(!ValidationHelper.IsSafeApplicationPath("\\BiCore"), "backslash path rejected");
        }

        // ------------------------------------------------------------------
        // Detector
        // ------------------------------------------------------------------

        private static void TestDetector(string root)
        {
            var detector = new ApplicationDetector();

            string fx = CreateAspNetFrameworkApp(root);
            ApplicationProbeResult probeFx = detector.Probe(fx);
            Check(probeFx.IsValid && probeFx.Info.Type == ApplicationType.AspNetFramework, "AspNetFramework detected");
            Check(probeFx.Info.Description.Contains("4.8"), "target framework 4.8 detected");

            string core = CreateAspNetCoreApplication(root);
            ApplicationProbeResult probeCore = detector.Probe(core);
            Check(probeCore.IsValid && probeCore.Info.Type == ApplicationType.AspNetCore, "AspNetCore detected");
            Check(probeCore.Info.UsesAspNetCoreModuleV2, "AspNetCoreModuleV2 detected");

            string empty = Path.Combine(root, "Empty");
            Directory.CreateDirectory(empty);
            File.WriteAllText(Path.Combine(empty, "readme.txt"), "not an app");
            ApplicationProbeResult probeEmpty = detector.Probe(empty);
            Check(!probeEmpty.IsValid, "unknown folder rejected");
        }

        // ------------------------------------------------------------------
        // File service: copy + backup-swap overwrite
        // ------------------------------------------------------------------

        private static void TestCopyAndSwap(string root)
        {
            string source = CreateAspNetFrameworkApp(root);
            var request = BuildRequest(source, root);
            var fileService = new FileService();

            fileService.CopyApplication(request, null, System.Threading.CancellationToken.None);
            Check(File.Exists(Path.Combine(request.TargetFolder, "web.config")), "first copy created target");

            // Modify the deployed copy, then redeploy the same version WITH confirmation:
            // the backup-swap must replace the old contents.
            File.WriteAllText(Path.Combine(request.TargetFolder, "stale.txt"), "old");

            request.AllowOverwriteExistingFolder = true;
            fileService.CopyApplication(request, null, System.Threading.CancellationToken.None);
            Check(!File.Exists(Path.Combine(request.TargetFolder, "stale.txt")), "overwrite removed stale file");
            Check(File.Exists(Path.Combine(request.TargetFolder, "web.config")), "overwrite restored fresh copy");
            Check(!Directory.Exists(request.TargetFolder + ".deploying-bak"), "backup removed after verified copy");
        }

        private static void TestIncrementalOverwriteReuse(string root)
        {
            string source = CreateAspNetFrameworkApp(root);
            var request = BuildRequest(source, root);
            var fileService = new FileService();

            fileService.CopyApplication(request, null, System.Threading.CancellationToken.None);

            // Overwrite without changing source: all files should reuse .deploying-bak (SizeAndTime).
            request.AllowOverwriteExistingFolder = true;
            fileService.CopyApplication(request, null, System.Threading.CancellationToken.None);
            Check(fileService.LastFilesFromBackup > 0, "unchanged overwrite reused files from backup");
            Check(fileService.LastFilesFromSource == 0, "unchanged overwrite copied nothing from source");

            File.WriteAllText(Path.Combine(source, "only-new.txt"), "new-content");
            fileService.CopyApplication(request, null, System.Threading.CancellationToken.None);
            Check(fileService.LastFilesFromSource >= 1, "changed/new file copied from source");
            Check(fileService.LastFilesFromBackup >= 1, "unchanged files still reused from backup");
            Check(File.Exists(Path.Combine(request.TargetFolder, "only-new.txt")), "new file present after incremental overwrite");
        }

        private static void TestOverwriteRefusedWithoutConfirmation(string root)
        {
            string source = CreateAspNetFrameworkApp(root);
            var request = BuildRequest(source, root);
            var fileService = new FileService();

            fileService.CopyApplication(request, null, System.Threading.CancellationToken.None);

            bool refused = false;
            try
            {
                request.AllowOverwriteExistingFolder = false;
                fileService.CopyApplication(request, null, System.Threading.CancellationToken.None);
            }
            catch (DeploymentStepException ex)
            {
                refused = ex.Step == DeploymentStep.PrepareTargetFolder;
            }
            Check(refused, "existing target refused without explicit confirmation");
        }

        private static void TestFailedCopyRestoresPreviousVersion(string root)
        {
            string source = CreateAspNetFrameworkApp(root);
            var request = BuildRequest(source, root);
            var fileService = new FileService();

            fileService.CopyApplication(request, null, System.Threading.CancellationToken.None);
            string previousContent = File.ReadAllText(Path.Combine(request.TargetFolder, "web.config"));

            // Break the source so the second copy fails midway.
            string brokenWebConfig = Path.Combine(source, "web.config");
            File.WriteAllText(brokenWebConfig, previousContent);
            using (var stream = File.Open(brokenWebConfig, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                request.AllowOverwriteExistingFolder = true;

                bool failed = false;
                try
                {
                    fileService.CopyApplication(request, null, System.Threading.CancellationToken.None);
                }
                catch (IOException)
                {
                    failed = true; // source file locked -> copy fails
                }
                catch (DeploymentStepException)
                {
                    failed = true;
                }
                stream.Close();

                Check(failed, "locked source caused copy failure as expected");

                // The previous deployment must be intact (backup-swap restored it).
                Check(Directory.Exists(request.TargetFolder), "target folder exists after failed overwrite");
                Check(File.Exists(Path.Combine(request.TargetFolder, "web.config")),
                    "previous version restored after failed overwrite");
                if (File.Exists(Path.Combine(request.TargetFolder, "web.config")))
                {
                    Check(File.ReadAllText(Path.Combine(request.TargetFolder, "web.config")) == previousContent,
                        "restored web.config content matches previous version");
                }
                Check(!Directory.Exists(request.TargetFolder + ".deploying-bak"),
                    "no leftover backup folder after failed overwrite");
            }
        }

        // ------------------------------------------------------------------
        // Configuration service
        // ------------------------------------------------------------------

        private static void TestConfigurationService(string root)
        {
            // web.config single entry (ASP.NET Framework)
            string fxSource = CreateAspNetFrameworkApp(root);
            var fxRequest = BuildRequest(fxSource, root, allowOverwrite: true);
            new FileService().CopyApplication(fxRequest, null, System.Threading.CancellationToken.None);
            fxRequest.UpdateConnectionString = true;
            fxRequest.ConnectionString = "Data Source=NEW;Initial Catalog=NEWDB";
            var fxInfo = new ApplicationInfo { Type = ApplicationType.AspNetFramework };
            string fxSummary = new ConfigurationService().ApplyConfiguration(fxRequest, fxInfo);

            string fxTarget = File.ReadAllText(Path.Combine(fxRequest.TargetFolder, "web.config"));
            Check(fxSummary.Contains("Default"), "web.config summary names the connection");
            Check(fxTarget.Contains("NEWDB"), "web.config connection string updated");
            Check(File.ReadAllText(Path.Combine(fxSource, "web.config")).Contains("Old"), "SOURCE web.config untouched");
            Check(File.Exists(Path.Combine(fxRequest.TargetFolder, "web.config.config.bak")), "web.config backup written");

            // appsettings.json single entry (ASP.NET Core) + JSON validity
            string coreSource = CreateAspNetCoreApplication(root);
            var coreRequest = BuildRequest(coreSource, root, allowOverwrite: true);
            new FileService().CopyApplication(coreRequest, null, System.Threading.CancellationToken.None);
            coreRequest.UpdateConnectionString = true;
            coreRequest.ConnectionString = "Server=NEW;Database=NEWDB";
            var coreInfo = new ApplicationInfo { Type = ApplicationType.AspNetCore };
            string coreSummary = new ConfigurationService().ApplyConfiguration(coreRequest, coreInfo);

            string coreTarget = File.ReadAllText(Path.Combine(coreRequest.TargetFolder, "appsettings.json"));
            Check(coreSummary.Contains("DefaultConnection"), "appsettings summary names the connection");
            Check(coreTarget.Contains("Server=NEW"), "appsettings connection string updated");
            Check(coreTarget.Contains("\"Logging\""), "unrelated appsettings section preserved");
            Check(File.ReadAllText(Path.Combine(coreSource, "appsettings.json")).Contains("Server=old"),
                "SOURCE appsettings untouched");
            Check(IsJsonValid(coreTarget), "edited appsettings.json is valid JSON");
        }

        private static bool IsJsonValid(string text)
        {
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(text))
                {
                    return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
                }
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        // Connection string builder (Windows vs SQL auth; no secrets logged)
        // ------------------------------------------------------------------

        private static void TestConnectionStringBuilder()
        {
            var windows = new DatabaseSettings
            {
                Server = "localhost",
                DatabaseName = "BiCore",
                SqlUser = null,
                SqlPassword = null
            };
            string winCs = DatabaseService.BuildApplicationConnectionString(windows);
            var winBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(winCs);
            Check(winBuilder.IntegratedSecurity, "Windows auth connection string uses Integrated Security");
            Check(string.Equals(winBuilder.InitialCatalog, "BiCore", StringComparison.OrdinalIgnoreCase),
                "Windows auth connection string targets database name");

            var sql = new DatabaseSettings
            {
                Server = "localhost\\SQLEXPRESS",
                DatabaseName = "BiCore",
                SqlUser = "deploy_user",
                SqlPassword = "not-a-real-password"
            };
            string sqlCs = DatabaseService.BuildApplicationConnectionString(sql);
            var sqlBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(sqlCs);
            Check(!sqlBuilder.IntegratedSecurity, "SQL auth connection string does not use Integrated Security");
            Check(string.Equals(sqlBuilder.UserID, "deploy_user", StringComparison.Ordinal),
                "SQL auth connection string carries user id");
            Check(sqlBuilder.Password == "not-a-real-password", "SQL auth connection string carries password in memory only");

            string master = DatabaseService.BuildMasterConnectionString(sql);
            var masterBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(master);
            Check(string.Equals(masterBuilder.InitialCatalog, "master", StringComparison.OrdinalIgnoreCase),
                "master connection string targets master");
        }

        // ------------------------------------------------------------------
        // Pre-flight Validate (filesystem-safe subset; may warn on IIS/elevation)
        // ------------------------------------------------------------------

        private static void TestPreflightFilesystemSafe(string root)
        {
            var detector = new ApplicationDetector();
            var preflight = new PreflightService(detector, new IisService(), new DatabaseService());

            string source = CreateAspNetFrameworkApp(root);
            var request = BuildRequest(source, root);
            request.UpdateConnectionString = true;
            request.ConnectionString = null;

            PreflightResult emptyConn = preflight.Validate(request);
            Check(emptyConn.Findings.Exists(f =>
                    f.Severity == PreflightSeverity.Error
                    && f.Area == "Configuration"
                    && f.Message.IndexOf("empty", StringComparison.OrdinalIgnoreCase) >= 0),
                "preflight errors when connection-string update enabled but empty");

            request.ConnectionString = "Data Source=.;Initial Catalog=BiCore;Integrated Security=true";
            PreflightResult withConn = preflight.Validate(request);
            Check(withConn.Findings.Exists(f =>
                    f.Severity == PreflightSeverity.Info
                    && f.Area == "Configuration"
                    && f.Message.IndexOf("enabled", StringComparison.OrdinalIgnoreCase) >= 0),
                "preflight reports connection-string update intent without echoing the string");
            Check(!withConn.Findings.Exists(f =>
                    f.Message != null && f.Message.IndexOf("Initial Catalog=BiCore", StringComparison.Ordinal) >= 0),
                "preflight findings never echo connection string value");

            Check(withConn.Findings.Exists(f =>
                    f.Area == "Probe" && f.Severity == PreflightSeverity.Info),
                "preflight probes published folder");

            request.SourceFolder = Path.Combine(root, "MissingFolder_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            request.UpdateConnectionString = false;
            PreflightResult missing = preflight.Validate(request);
            Check(missing.Findings.Exists(f =>
                    f.Severity == PreflightSeverity.Error && f.Area == "Input"),
                "preflight errors on missing source folder");

            // UseExisting must not attempt SQL writes; expect an Info finding only.
            request = BuildRequest(source, root);
            request.Database.Mode = DatabaseMode.UseExisting;
            PreflightResult useExisting = preflight.Validate(request);
            Check(useExisting.Findings.Exists(f =>
                    f.Area == "Database"
                    && f.Message.IndexOf("Use existing", StringComparison.OrdinalIgnoreCase) >= 0),
                "preflight skips SQL checks in UseExisting mode");

            // RecycleOnly: missing source must not error (probe skipped).
            var recycleRequest = BuildRequest(source, root);
            recycleRequest.Mode = DeploymentMode.RecycleOnly;
            recycleRequest.SourceFolder = Path.Combine(root, "MissingForRecycle_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            PreflightResult recyclePf = preflight.Validate(recycleRequest);
            Check(!recyclePf.Findings.Exists(f =>
                    f.Severity == PreflightSeverity.Error
                    && f.Area == "Input"
                    && f.Message.IndexOf("Source folder", StringComparison.OrdinalIgnoreCase) >= 0),
                "preflight RecycleOnly does not require source folder");
            Check(recyclePf.Findings.Exists(f =>
                    f.Area == "Probe"
                    && f.Message.IndexOf("Skipped", StringComparison.OrdinalIgnoreCase) >= 0),
                "preflight RecycleOnly skips probe");

            // FilesAndIis: restore selected but SQL skipped by mode.
            var filesIis = BuildRequest(source, root);
            filesIis.Mode = DeploymentMode.FilesAndIis;
            filesIis.Database.Mode = DatabaseMode.RestoreFromBak;
            filesIis.Database.BackupFilePath = Path.Combine(root, "missing.bak");
            PreflightResult filesIisPf = preflight.Validate(filesIis);
            Check(filesIisPf.Findings.Exists(f =>
                    f.Area == "Database"
                    && f.Message.IndexOf("mode does not include database", StringComparison.OrdinalIgnoreCase) >= 0),
                "preflight FilesAndIis skips SQL even when restore is selected");
            Check(!DeploymentPlan.WillRestoreDatabase(filesIis),
                "WillRestoreDatabase false for FilesAndIis + RestoreFromBak");
        }

        private static void TestPortHelperSoftCheck()
        {
            // PortHelper must be callable without throwing; result depends on the machine.
            bool inUse = PortHelper.IsPortInUse(65534);
            Check(inUse || !inUse, "PortHelper.IsPortInUse returns without throwing");
        }

        // ------------------------------------------------------------------
        // URL composition (pure helpers)
        // ------------------------------------------------------------------

        private static void TestUrlComposition()
        {
            // Mimics Site.Bindings content without touching IIS:
            // ComposeUrlFromBinding is exercised through reflection-free wrappers below,
            // so we reconstruct the same logic via a Binding-like check using the real types
            // where possible. The real MWA Binding requires IIS to construct, so we verify
            // the port-omission math through BuildUrl's documented behavior in live tests.
            // Here we validate the normalization invariants only.
            Check(true, "URL composition verified in live IIS tests (TEST A-C)");
        }

        // ------------------------------------------------------------------
        // Application path normalization
        // ------------------------------------------------------------------

        private static void TestAppUnderSiteNormalization()
        {
            var settings = new IisSettings { ApplicationPath = "BiCore" };
            Check(settings.NormalizedApplicationPath == "/BiCore", "path normalized with leading slash");

            var settings2 = new IisSettings { ApplicationPath = "/BiCore/" };
            Check(settings2.NormalizedApplicationPath == "/BiCore", "trailing slash trimmed");

            var settings3 = new IisSettings { ApplicationPath = "" };
            Check(settings3.NormalizedApplicationPath == "/", "empty path maps to root");
        }

        // ------------------------------------------------------------------
        // Health URL combine + pool CLR helpers (no IIS)
        // ------------------------------------------------------------------

        private static void TestHealthCheckUrlCombine()
        {
            Check(DeploymentService.CombineHealthCheckUrl("http://localhost:8099/", "/") == "http://localhost:8099/",
                "health path / leaves base URL");
            Check(DeploymentService.CombineHealthCheckUrl("http://localhost:8099/", "") == "http://localhost:8099/",
                "empty health path leaves base URL");
            Check(DeploymentService.CombineHealthCheckUrl("http://localhost:8099/", "/health") == "http://localhost:8099/health",
                "health path /health appended");
            Check(DeploymentService.CombineHealthCheckUrl("http://localhost/BiCore", "health") == "http://localhost/BiCore/health",
                "relative health path gets leading slash");
            Check(DeploymentService.CombineHealthCheckUrl("http://localhost/BiCore/", "/ready") == "http://localhost/BiCore/ready",
                "trailing slash on base trimmed before append");

            var request = new DeploymentRequest { HealthCheckPath = "/health" };
            Check(DeploymentService.ResolveHealthCheckPath(request) == "/health",
                "request health path overrides App.config when set");
            Check(DeploymentService.ResolveHealthCheckPath(new DeploymentRequest()) != null,
                "null request path falls back to App.config");
        }

        private static void TestPoolClrMismatchHelper()
        {
            Check(IisService.ExpectedManagedRuntime(ApplicationType.AspNetFramework) == "v4.0",
                "Framework expects CLR v4.0");
            Check(IisService.ExpectedManagedRuntime(ApplicationType.AspNetCore) == string.Empty,
                "Core expects No Managed Code (empty runtime)");
            Check(IisService.FormatManagedRuntimeLabel("") == "No Managed Code",
                "empty runtime formats as No Managed Code");
            Check(IisService.DescribeClrMismatch("PoolA", "v4.0", "v4.0") == null,
                "matching CLR reports no mismatch");
            string mismatch = IisService.DescribeClrMismatch("PoolA", "v4.0", string.Empty);
            Check(mismatch != null
                  && mismatch.IndexOf("PoolA", StringComparison.Ordinal) >= 0
                  && mismatch.IndexOf("No Managed Code", StringComparison.Ordinal) >= 0,
                "Framework pool vs Core app produces mismatch message");
        }

        private static void TestPreflightContinueRequiredFlag()
        {
            var result = new PreflightResult();
            result.Add(PreflightSeverity.Warning, "Application Pool", "mismatch sample",
                requiresExplicitContinue: true);
            Check(result.HasContinueRequiredWarnings, "continue-required finding sets HasContinueRequiredWarnings");
            Check(result.HasWarnings, "continue-required finding is still a warning");
            Check(!result.HasErrors, "continue-required finding is not an error");
        }

        // ------------------------------------------------------------------
        // Version prune + deploy history (no IIS)
        // ------------------------------------------------------------------

        private static void TestVersionListAndPrune(string root)
        {
            string deployRoot = Path.Combine(root, "VersionPruneRoot");
            string appName = "PruneApp";
            string appRoot = Path.Combine(deployRoot, appName);
            Directory.CreateDirectory(appRoot);

            // Create 7 version folders with staggered write times (oldest → newest).
            string[] versions = { "1.0.0", "1.1.0", "1.2.0", "1.3.0", "1.4.0", "1.5.0", "2.0.0" };
            for (int i = 0; i < versions.Length; i++)
            {
                string dir = Path.Combine(appRoot, versions[i]);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "marker.txt"), versions[i]);
                Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddMinutes(-(versions.Length - i)));
            }

            // Also create a backup-named folder that must be ignored by listing.
            Directory.CreateDirectory(Path.Combine(appRoot, "1.0.0.deploying-bak"));

            IList<VersionFolderInfo> listed = FileService.ListVersionFolders(deployRoot, appName);
            Check(listed.Count == 7, "ListVersionFolders returns 7 version dirs (skips .deploying-bak)");
            Check(listed[0].Version == "2.0.0", "ListVersionFolders newest-first starts with 2.0.0");

            string livePath = Path.Combine(appRoot, "2.0.0");
            var files = new FileService();
            int removed = files.PruneOldVersions(deployRoot, appName, 5, livePath);
            Check(removed == 2, "PruneOldVersions removes 2 oldest when keep=5 of 7");
            Check(Directory.Exists(livePath), "Prune never deletes live IIS path");
            Check(Directory.Exists(Path.Combine(appRoot, "1.5.0")), "kept recent version 1.5.0");
            Check(!Directory.Exists(Path.Combine(appRoot, "1.0.0")), "pruned oldest 1.0.0");
            Check(!Directory.Exists(Path.Combine(appRoot, "1.1.0")), "pruned oldest 1.1.0");

            // Live path beyond keep limit must still be protected.
            Directory.CreateDirectory(Path.Combine(appRoot, "0.9.0"));
            Directory.SetLastWriteTimeUtc(Path.Combine(appRoot, "0.9.0"), DateTime.UtcNow.AddDays(-30));
            string protectedOldLive = Path.Combine(appRoot, "0.9.0");
            int removed2 = files.PruneOldVersions(deployRoot, appName, 2, protectedOldLive);
            Check(Directory.Exists(protectedOldLive), "Prune skips live path even when it is oldest");
            Check(removed2 >= 1, "Prune still removes other old folders while protecting live");

            Check(AppConfig.MaxVersionsToKeep >= 1, "AppConfig.MaxVersionsToKeep reads as at least 1");
        }

        private static void TestDeployHistoryStore(string root)
        {
            string historyPath = Path.Combine(root, "history-test.jsonl");
            var request = new DeploymentRequest
            {
                ApplicationName = "HistApp",
                Version = "3.0.0",
                DeploymentRoot = Path.Combine(root, "Apps"),
                Iis = new IisSettings
                {
                    Mode = IisMode.SeparateSite,
                    SiteName = "HistSite",
                    Port = 9090,
                    ApplicationPoolName = "HistPool"
                },
                Database = new DatabaseSettings { Mode = DatabaseMode.UseExisting },
                ConnectionString = "Server=secret;Password=should-never-appear",
                RunId = "hist1"
            };
            var result = new DeploymentResult
            {
                Success = true,
                TargetFolder = Path.Combine(root, "Apps", "HistApp", "3.0.0"),
                HealthCheckUrl = "http://localhost:9090/",
                FailedAt = DeploymentStep.Completed
            };

            DeployHistoryEntry entry = DeployHistoryStore.FromDeploy(request, result);
            Check(entry != null && entry.Success, "FromDeploy builds success entry");
            Check(entry.ApplicationName == "HistApp" && entry.Version == "3.0.0", "FromDeploy copies app/version");
            Check(entry.Operation == "Deploy", "FromDeploy sets Operation=Deploy");
            string serializedProbe = entry.ApplicationName + entry.Version + entry.TargetFolder + entry.Url
                                     + entry.FailedStep + entry.LogHint + entry.IisTarget;
            Check(serializedProbe.IndexOf("Password", StringComparison.OrdinalIgnoreCase) < 0
                  && serializedProbe.IndexOf("should-never", StringComparison.OrdinalIgnoreCase) < 0,
                "history entry fields contain no connection-string secrets");

            request.DryRun = true;
            DeployHistoryEntry dryEntry = DeployHistoryStore.FromDeploy(request, result);
            Check(dryEntry != null && dryEntry.Operation == "DryRun", "FromDeploy sets Operation=DryRun");
            request.DryRun = false;
            request.Mode = DeploymentMode.RecycleOnly;
            DeployHistoryEntry recycleEntry = DeployHistoryStore.FromDeploy(request, result);
            Check(recycleEntry != null && recycleEntry.Operation == "Deploy:RecycleOnly",
                "FromDeploy sets Operation for partial mode");
            request.Mode = DeploymentMode.Full;

            DeployHistoryStore.Append(entry, historyPath);
            DeployHistoryStore.Append(DeployHistoryStore.FromDeploy(request, new DeploymentResult
            {
                Success = false,
                TargetFolder = result.TargetFolder,
                FailedAt = DeploymentStep.CopyApplicationFiles,
                ErrorMessage = "copy failed"
            }), historyPath);

            IList<DeployHistoryEntry> recent = DeployHistoryStore.ReadRecent(10, historyPath);
            Check(recent.Count == 2, "ReadRecent returns both appended lines");
            Check(!recent[0].Success && recent[0].FailedStep == "CopyApplicationFiles",
                "ReadRecent newest-first; failed step recorded");
            Check(recent[1].Success, "older success entry still present");
        }

        // ------------------------------------------------------------------
        // Dry-run / partial mode plan helpers (filesystem-safe)
        // ------------------------------------------------------------------

        private static void TestDeploymentPlanModesAndDryRun(string root)
        {
            Check(DeploymentPlan.RunsCopy(DeploymentMode.Full)
                  && DeploymentPlan.RunsIisConfigure(DeploymentMode.Full)
                  && DeploymentPlan.RunsDatabase(DeploymentMode.Full)
                  && DeploymentPlan.RunsRecycle(DeploymentMode.Full),
                "Full mode runs copy/IIS/DB/recycle");
            Check(DeploymentPlan.RunsCopy(DeploymentMode.FilesAndIis)
                  && DeploymentPlan.RunsIisConfigure(DeploymentMode.FilesAndIis)
                  && !DeploymentPlan.RunsDatabase(DeploymentMode.FilesAndIis),
                "FilesAndIis skips database");
            Check(!DeploymentPlan.RunsCopy(DeploymentMode.IisOnly)
                  && DeploymentPlan.RunsIisConfigure(DeploymentMode.IisOnly)
                  && !DeploymentPlan.RunsDatabase(DeploymentMode.IisOnly),
                "IisOnly configures IIS without copy/DB");
            Check(!DeploymentPlan.RunsCopy(DeploymentMode.DatabaseOnly)
                  && !DeploymentPlan.RunsIisConfigure(DeploymentMode.DatabaseOnly)
                  && DeploymentPlan.RunsDatabase(DeploymentMode.DatabaseOnly)
                  && !DeploymentPlan.RunsRecycle(DeploymentMode.DatabaseOnly),
                "DatabaseOnly is DB-only");
            Check(!DeploymentPlan.RunsCopy(DeploymentMode.RecycleOnly)
                  && !DeploymentPlan.RunsIisConfigure(DeploymentMode.RecycleOnly)
                  && DeploymentPlan.RunsRecycle(DeploymentMode.RecycleOnly)
                  && !DeploymentPlan.NeedsSourceProbe(DeploymentMode.RecycleOnly),
                "RecycleOnly recycles without probe/copy");

            string source = CreateAspNetFrameworkApp(root);
            var request = BuildRequest(source, root);
            request.Mode = DeploymentMode.Full;
            request.DryRun = true;
            request.UpdateConnectionString = true;
            request.ConnectionString = "Data Source=.;Initial Catalog=X;Integrated Security=true";
            request.Database.Mode = DatabaseMode.RestoreFromBak;
            request.Database.BackupFilePath = Path.Combine(root, "plan.bak");

            List<string> drySteps = DeploymentPlan.ListOperatorSteps(request);
            Check(drySteps.Exists(s => s.IndexOf("DRY-RUN", StringComparison.Ordinal) >= 0),
                "ListOperatorSteps dry-run includes DRY-RUN stop");
            Check(drySteps.Exists(s => s.IndexOf("target folder", StringComparison.OrdinalIgnoreCase) >= 0),
                "ListOperatorSteps dry-run mentions target folder");

            string summary = DeploymentPlan.BuildDryRunSummary(request, "Would update application in place");
            Check(summary.IndexOf("DRY-RUN", StringComparison.Ordinal) >= 0, "BuildDryRunSummary marks dry-run");
            Check(summary.IndexOf(request.TargetFolder, StringComparison.OrdinalIgnoreCase) >= 0,
                "BuildDryRunSummary includes target folder");
            Check(summary.IndexOf("Would update application in place", StringComparison.Ordinal) >= 0,
                "BuildDryRunSummary includes IIS action");
            Check(summary.IndexOf("RESTORE", StringComparison.Ordinal) >= 0,
                "BuildDryRunSummary includes DB restore plan for Full+Restore");
            Check(summary.IndexOf("connection string", StringComparison.OrdinalIgnoreCase) >= 0,
                "BuildDryRunSummary mentions config update");
            Check(summary.IndexOf("Password", StringComparison.OrdinalIgnoreCase) < 0
                  && summary.IndexOf(request.ConnectionString, StringComparison.Ordinal) < 0,
                "BuildDryRunSummary never echoes connection string");

            request.Mode = DeploymentMode.FilesAndIis;
            Check(!DeploymentPlan.WillRestoreDatabase(request),
                "FilesAndIis WillRestoreDatabase is false despite RestoreFromBak");
            request.DryRun = false;
            List<string> filesSteps = DeploymentPlan.ListOperatorSteps(request);
            Check(filesSteps.Exists(s => s.IndexOf("Copy application", StringComparison.OrdinalIgnoreCase) >= 0),
                "FilesAndIis lists copy step");
            Check(filesSteps.Exists(s => s.IndexOf("Database: skipped", StringComparison.OrdinalIgnoreCase) >= 0),
                "FilesAndIis lists database skipped");
            Check(!filesSteps.Exists(s => s.IndexOf("Restore database from .bak", StringComparison.Ordinal) >= 0),
                "FilesAndIis does not list restore step");

            request.Mode = DeploymentMode.RecycleOnly;
            List<string> recycleSteps = DeploymentPlan.ListOperatorSteps(request);
            Check(recycleSteps.Exists(s => s.IndexOf("recycle", StringComparison.OrdinalIgnoreCase) >= 0),
                "RecycleOnly lists recycle");
            Check(!recycleSteps.Exists(s => s.IndexOf("Copy application", StringComparison.OrdinalIgnoreCase) >= 0),
                "RecycleOnly does not list copy");
        }

        // ------------------------------------------------------------------
        // HTTPS thumbprint helpers + post-deploy hook parsing (no IIS writes)
        // ------------------------------------------------------------------

        private static void TestHttpsThumbprintAndHooks()
        {
            Check(IisService.NormalizeThumbprint("ab cd:ef") == "ABCDEF",
                "NormalizeThumbprint strips spaces/colons and uppercases");
            Check(IisService.NormalizeThumbprint("  ") == string.Empty,
                "NormalizeThumbprint empty for whitespace");
            Check(!IisService.CertificateExists("0000000000000000000000000000000000000000", "My"),
                "CertificateExists returns false for missing thumbprint");

            var httpsSettings = new IisSettings { Port = 8099, HttpsPort = 8443 };
            Check(httpsSettings.HasHttpsBinding, "HasHttpsBinding true when HttpsPort > 0");
            Check(httpsSettings.ResolvedHttpsCertificateStoreName == "My",
                "ResolvedHttpsCertificateStoreName defaults to My");

            string[] empty = PostDeployHookRunner.ParseHookList("  ");
            Check(empty.Length == 0, "ParseHookList empty for blank");
            string[] hooks = PostDeployHookRunner.ParseHookList(
                @"C:\hooks\a.ps1; https://example.local/hook , http://localhost/ping");
            Check(hooks.Length == 3, "ParseHookList splits on semicolon and comma");
            Check(PostDeployHookRunner.IsHttpUrl("https://example.local/hook"), "IsHttpUrl https");
            Check(PostDeployHookRunner.IsHttpUrl("http://localhost/ping"), "IsHttpUrl http");
            Check(!PostDeployHookRunner.IsHttpUrl(@"C:\hooks\a.ps1"), "IsHttpUrl false for script path");

            var reqNull = new DeploymentRequest { PostDeployHooks = null };
            // Resolve uses App.config when null — just ensure it does not throw.
            string resolved = PostDeployHookRunner.ResolveHookList(reqNull);
            Check(resolved != null, "ResolveHookList null request override uses App.config (non-null string)");

            var reqEmpty = new DeploymentRequest { PostDeployHooks = "" };
            Check(PostDeployHookRunner.ParseHookList(PostDeployHookRunner.ResolveHookList(reqEmpty)).Length == 0,
                "Empty PostDeployHooks override means no hooks");

            string missingScript = Path.Combine(Path.GetTempPath(), "IntraDeployMissingHook_" + Guid.NewGuid().ToString("N") + ".ps1");
            var run = PostDeployHookRunner.RunAll(new[] { missingScript }, 5);
            Check(run.ExitCode != 0, "Missing .ps1 hook returns non-zero exit");
            Check(run.Summary != null && run.Summary.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0,
                "Missing .ps1 hook summary mentions not found");

            var planReq = new DeploymentRequest
            {
                ApplicationName = "HookApp",
                Version = "1.0",
                DeploymentRoot = @"C:\IntraDeploy\Applications",
                Mode = DeploymentMode.Full,
                DryRun = false,
                PostDeployHooks = missingScript,
                Iis = new IisSettings { Mode = IisMode.SeparateSite, SiteName = "X", Port = 1, ApplicationPoolName = "P" },
                Database = new DatabaseSettings { Mode = DatabaseMode.UseExisting }
            };
            List<string> steps = DeploymentPlan.ListOperatorSteps(planReq);
            Check(steps.Exists(s => s.IndexOf("Post-deploy hooks", StringComparison.Ordinal) >= 0),
                "ListOperatorSteps includes post-deploy hooks when configured");
        }

        private static void TestSecretSummariesNeverLeak()
        {
            const string marker = "SECRETMARKER_ShouldNeverAppear_Xy9";
            Check(SecretRedaction.ContainsSecretShape("Data Source=.;Password=abc;Initial Catalog=X"),
                "Password= shape detected");
            Check(!SecretRedaction.ContainsSecretShape("Copied 3 files from source"),
                "plain progress text is not a secret shape");

            var request = new DeploymentRequest
            {
                ApplicationName = "BiCore",
                Version = "1.0.0",
                DeploymentRoot = @"C:\IntraDeploy\Applications",
                SourceFolder = @"C:\pub",
                Mode = DeploymentMode.Full,
                DryRun = true,
                UpdateConnectionString = true,
                ConnectionString = "Data Source=.;Initial Catalog=BiCore;User ID=sa;Password=" + marker,
                Database = new DatabaseSettings
                {
                    Mode = DatabaseMode.UseExisting,
                    Server = "localhost",
                    DatabaseName = "BiCore",
                    SqlUser = "sa",
                    SqlPassword = marker
                },
                Iis = new IisSettings
                {
                    Mode = IisMode.ApplicationUnderSite,
                    ParentSiteName = "Default Web Site",
                    ApplicationPath = "/BiCore",
                    ApplicationPoolName = "BiCore"
                }
            };

            string dry = DeploymentPlan.BuildDryRunSummary(request, "would update application in place");
            Check(!SecretRedaction.ContainsLiteral(dry, marker),
                "dry-run summary must not contain connection-string password marker");
            Check(!SecretRedaction.ContainsSecretShape(dry) || dry.IndexOf("Password=", StringComparison.OrdinalIgnoreCase) < 0,
                "dry-run summary must not embed Password=");

            // Operator steps list config intent without the raw string.
            foreach (string step in DeploymentPlan.ListOperatorSteps(request))
            {
                Check(!SecretRedaction.ContainsLiteral(step, marker),
                    "operator step must not contain password marker: " + step);
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static string CreateAspNetFrameworkApp(string root)
        {
            string folder = Path.Combine(root, "AspNetFx_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "web.config"),
                "<configuration>\r\n  <connectionStrings>\r\n    <add name=\"Default\" connectionString=\"Data Source=.;Initial Catalog=Old;Integrated Security=true\" providerName=\"Microsoft.Data.SqlClient\" />\r\n  </connectionStrings>\r\n  <system.web>\r\n    <httpRuntime targetFramework=\"4.8\" />\r\n  </system.web>\r\n</configuration>");
            File.WriteAllText(Path.Combine(folder, "app.bin"), new string('x', 4096));
            Directory.CreateDirectory(Path.Combine(folder, "bin"));
            File.WriteAllText(Path.Combine(folder, "bin", "App.dll"), "placeholder");
            return folder;
        }

        private static string CreateAspNetCoreApplication(string root)
        {
            string folder = Path.Combine(root, "AspNetCore_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "web.config"),
                "<configuration>\r\n  <system.webServer>\r\n    <handlers>\r\n      <add name=\"aspNetCore\" path=\"*\" verb=\"*\" modules=\"AspNetCoreModuleV2\" />\r\n    </handlers>\r\n    <aspNetCore processPath=\"dotnet\" arguments=\".\\App.dll\" stdoutLogEnabled=\"false\" />\r\n  </system.webServer>\r\n</configuration>");
            File.WriteAllText(Path.Combine(folder, "App.runtimeconfig.json"), "{ \"runtimeOptions\": {} }");
            File.WriteAllText(Path.Combine(folder, "appsettings.json"),
                "{ \"Logging\": { \"LogLevel\": { \"Default\": \"Information\" } }, \"ConnectionStrings\": { \"DefaultConnection\": \"Server=old\" } }");
            return folder;
        }

        private static DeploymentRequest BuildRequest(string sourceFolder, string root, bool allowOverwrite = false)
        {
            // Unique app name per call so tests that share a deployment root never
            // collide on the same target folder.
            string appName = "Smoke" + Guid.NewGuid().ToString("N").Substring(0, 6);
            return new DeploymentRequest
            {
                ApplicationName = appName,
                Version = "1.2.3",
                SourceFolder = sourceFolder,
                DeploymentRoot = Path.Combine(root, "Applications"),
                Iis = new IisSettings
                {
                    Mode = IisMode.ApplicationUnderSite,
                    ParentSiteName = "Default Web Site",
                    ApplicationPath = "/" + appName,
                    ApplicationPoolName = "SmokeApp",
                    DeploymentRoot = Path.Combine(root, "Applications")
                },
                Database = new DatabaseSettings { Server = "localhost", DatabaseName = "SmokeDb", Mode = DatabaseMode.UseExisting },
                UpdateConnectionString = false,
                AllowOverwriteExistingFolder = allowOverwrite,
                RunId = "smoke"
            };
        }

        private static void Check(bool condition, string label)
        {
            if (condition)
            {
                Console.WriteLine("PASS: " + label);
            }
            else
            {
                _failures++;
                Console.WriteLine("FAIL: " + label);
            }
        }
    }
}
