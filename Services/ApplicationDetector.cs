using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Serilog;
using IntraDeploy.Models;

namespace IntraDeploy.Services
{
    /// <summary>
    /// Looks at a published folder and decides what kind of IIS app it is:
    ///   web.config              → ASP.NET Framework
    ///   *.runtimeconfig.json    → ASP.NET Core (hosted in IIS)
    /// Neither marker            → reject (not a safe publish output)
    /// </summary>
    public class ApplicationDetector
    {
        /// <summary>Probes the source folder and returns deployability plus detected type.</summary>
        public ApplicationProbeResult Probe(string sourceFolder)
        {
            var result = new ApplicationProbeResult();

            if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
            {
                result.ErrorMessage = "Source folder does not exist: " + (sourceFolder ?? "<empty>");
                return result;
            }

            ApplicationInfo info = new ApplicationInfo();
            info.HasWebConfig = File.Exists(Path.Combine(sourceFolder, "web.config"));

            string runtimeConfig = Directory
                .EnumerateFiles(sourceFolder, "*.runtimeconfig.json", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();

            info.HasRuntimeConfig = runtimeConfig != null;

            if (info.HasRuntimeConfig)
            {
                info.Type = ApplicationType.AspNetCore;
                info.Description = "Modern .NET application (" + Path.GetFileName(runtimeConfig) + ")";
                if (info.HasWebConfig)
                {
                    info.AspNetCoreModuleName = ReadAspNetCoreModule(Path.Combine(sourceFolder, "web.config"));
                    if (info.UsesAspNetCoreModuleV2)
                    {
                        info.Description = "ASP.NET Core application (ASP.NET Core Module V2)";
                    }
                }
            }
            else if (info.HasWebConfig)
            {
                info.Type = ApplicationType.AspNetFramework;
                string targetFramework = ReadTargetFramework(Path.Combine(sourceFolder, "web.config"));
                info.Description = string.IsNullOrEmpty(targetFramework)
                    ? "ASP.NET Framework application (web.config)"
                    : "ASP.NET Framework " + targetFramework + " (web.config)";
            }
            else
            {
                // Folder with neither marker. Refuse by default; the operator can still
                // proceed only if we later add an explicit override (v1 keeps it safe).
                info.Type = ApplicationType.Unknown;
                info.Description = "No web.config and no *.runtimeconfig.json found";
                result.ErrorMessage = "The folder does not look like a published IIS application " +
                                      "(no web.config and no *.runtimeconfig.json found).";
                result.Info = info;
                return result;
            }

            result.IsValid = true;
            result.Info = info;
            Log.Information("Application probe: {Folder} -> {Type} ({Description})",
                sourceFolder, info.Type, info.Description);
            return result;
        }

        /// <summary>
        /// Reports prerequisites relevant to the detected type. Never tries to install anything.
        /// ASP.NET Core checks only look for the ASP.NET Core Module registration in IIS.
        /// </summary>
        public string GetPrerequisiteWarning(ApplicationInfo info)
        {
            if (info == null || info.Type != ApplicationType.AspNetCore)
            {
                return null;
            }

            // If the app references AspNetCoreModuleV2 but IIS has not registered it,
            // the site would fail with a 500.19/500.21 style error. We cannot reliably
            // read the global module list without IIS access here, so this warning is
            // raised conservatively when we are on a machine that also has no sign of
            // the hosting bundle (registry free check of Program Files).
            if (info.UsesAspNetCoreModuleV2)
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string moduleDll = Path.Combine(programFiles, "IIS", "Asp.Net Core Module", "V2");
                if (!Directory.Exists(moduleDll))
                {
                    return "The application references the ASP.NET Core Module V2 but the ASP.NET Core " +
                           "hosting bundle does not appear to be installed on this machine. " +
                           "Install the .NET Core Hosting Bundle before requesting the site, otherwise IIS will return HTTP 500.19.";
                }
            }
            return null;
        }

        private static string ReadAspNetCoreModule(string webConfigPath)
        {
            try
            {
                XDocument document = XDocument.Load(webConfigPath);
                XElement handler = document.Root?
                    .Element("system.webServer")?
                    .Element("handlers")?
                    .Elements("add")
                    .FirstOrDefault(e =>
                        string.Equals((string)e.Attribute("name"), "aspNetCore", StringComparison.OrdinalIgnoreCase));
                return handler?.Attribute("modules")?.Value;
            }
            catch (Exception ex)
            {
                Log.Warning("Could not parse web.config for ASP.NET Core module detection: {Message}", ex.Message);
                return null;
            }
        }

        private static string ReadTargetFramework(string webConfigPath)
        {
            try
            {
                XDocument document = XDocument.Load(webConfigPath);
                XElement httpRuntime = document.Root?
                    .Element("system.web")?
                    .Element("httpRuntime");
                string targetFramework = httpRuntime?.Attribute("targetFramework")?.Value;
                return string.IsNullOrWhiteSpace(targetFramework) ? null : targetFramework.Trim();
            }
            catch (Exception ex)
            {
                Log.Warning("Could not parse web.config for target framework detection: {Message}", ex.Message);
                return null;
            }
        }
    }
}
