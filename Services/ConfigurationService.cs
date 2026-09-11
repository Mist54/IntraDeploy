using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Serilog;
using IntraDeploy.Models;

namespace IntraDeploy.Services
{
    /// <summary>
    /// Applies only explicitly supported configuration changes to the DEPLOYED copy
    /// (the published source folder is never modified).
    ///
    /// Supported:
    ///  - web.config <connectionStrings><add ... connectionString="..."/> (first entry, name kept)
    ///  - appsettings.json "ConnectionStrings": { "Default": "..." } (any single entry)
    ///
    /// Anything unexpected (missing nodes, multiple ambiguous entries, unparsable XML/JSON)
    /// is reported instead of guessed, so we never corrupt the developer's configuration.
    /// A .bak copy of the original file is left next to the deployed file.
    /// </summary>
    public class ConfigurationService
    {
        /// <summary>
        /// Updates the connection string in the deployed application when requested.
        /// Returns an operator facing summary. Throws DeploymentStepException when the
        /// requested change cannot be applied safely.
        /// </summary>
        public string ApplyConfiguration(DeploymentRequest request, ApplicationInfo appInfo)
        {
            if (!request.UpdateConnectionString || string.IsNullOrWhiteSpace(request.ConnectionString))
            {
                Log.Information("Connection string update not requested; deployed configuration left untouched.");
                return "No configuration changes (connection string update not requested).";
            }

            string targetFolder = request.TargetFolder;
            string webConfigPath = Path.Combine(targetFolder, "web.config");
            string appSettingsPath = Path.Combine(targetFolder, "appsettings.json");

            // ASP.NET Core apps carry a web.config only for the IIS reverse proxy
            // (it has no connectionStrings section); their connection strings live in
            // appsettings.json. Prefer appsettings.json for that application type.
            bool isAspNetCore = appInfo != null && appInfo.Type == ApplicationType.AspNetCore;

            if (isAspNetCore && File.Exists(appSettingsPath))
            {
                return UpdateAppSettingsConnectionString(appSettingsPath, request.ConnectionString);
            }

            if (File.Exists(webConfigPath))
            {
                return UpdateWebConfigConnectionString(webConfigPath, request.ConnectionString);
            }

            if (File.Exists(appSettingsPath))
            {
                return UpdateAppSettingsConnectionString(appSettingsPath, request.ConnectionString);
            }

            throw new DeploymentStepException(DeploymentStep.ApplyConfiguration,
                "Connection string update was requested but neither web.config nor appsettings.json was found in " +
                targetFolder + ".");
        }

        private string UpdateWebConfigConnectionString(string webConfigPath, string connectionString)
        {
            XDocument document;
            try
            {
                document = XDocument.Load(webConfigPath, LoadOptions.PreserveWhitespace);
            }
            catch (Exception ex)
            {
                throw new DeploymentStepException(DeploymentStep.ApplyConfiguration,
                    "web.config could not be parsed; connection string was NOT changed: " + ex.Message, ex);
            }

            XElement root = document.Root;
            XElement section = root?.Element("connectionStrings");
            if (section == null)
            {
                throw new DeploymentStepException(DeploymentStep.ApplyConfiguration,
                    "web.config has no <connectionStrings> section; connection string was NOT changed. " +
                    "Update the configuration manually.");
            }

            XElement firstEntry = section.Elements("add").FirstOrDefault();
            if (firstEntry == null)
            {
                throw new DeploymentStepException(DeploymentStep.ApplyConfiguration,
                    "web.config <connectionStrings> contains no <add> entries; connection string was NOT changed.");
            }

            XElement secondEntry = section.Elements("add").Skip(1).FirstOrDefault();
            if (secondEntry != null)
            {
                throw new DeploymentStepException(DeploymentStep.ApplyConfiguration,
                    "web.config <connectionStrings> contains multiple entries ('" +
                    (string)firstEntry.Attribute("name") + "', '" + (string)secondEntry.Attribute("name") +
                    "' ...). Updating the wrong one could break the application; connection string was NOT changed. " +
                    "Update the configuration manually.");
            }

            string entryName = (string)firstEntry.Attribute("name") ?? "(unnamed)";
            string previous = (string)firstEntry.Attribute("connectionString") ?? string.Empty;

            CreateBackup(webConfigPath);

            firstEntry.SetAttributeValue("connectionString", connectionString);
            document.Save(webConfigPath);

            Log.Information("web.config connection string '{Name}' updated in deployed app (previous value replaced, backup written)",
                entryName);
            return "web.config: connection string '" + entryName + "' updated (backup .config.bak written).";
        }

        private string UpdateAppSettingsConnectionString(string appSettingsPath, string connectionString)
        {
            string json;
            try
            {
                json = File.ReadAllText(appSettingsPath);
            }
            catch (Exception ex)
            {
                throw new DeploymentStepException(DeploymentStep.ApplyConfiguration,
                    "appsettings.json could not be read; connection string was NOT changed: " + ex.Message, ex);
            }

            // Minimal, dependency free handling of the well known ConnectionStrings block:
            //   "ConnectionStrings": { "AnyName": "..." }
            // Any other JSON shape is reported, not guessed.
            int index = json.IndexOf("\"ConnectionStrings\"", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                throw new DeploymentStepException(DeploymentStep.ApplyConfiguration,
                    "appsettings.json has no ConnectionStrings section; connection string was NOT changed. " +
                    "Update the configuration manually.");
            }

            int braceStart = json.IndexOf('{', index);
            int braceEnd = json.IndexOf('}', braceStart);
            if (braceStart < 0 || braceEnd < 0)
            {
                throw new DeploymentStepException(DeploymentStep.ApplyConfiguration,
                    "appsettings.json ConnectionStrings block is malformed; connection string was NOT changed.");
            }

            string block = json.Substring(braceStart + 1, braceEnd - braceStart - 1);
            var nameMatches = System.Text.RegularExpressions.Regex.Matches(block, "\"([^\"]+)\"\\s*:");
            if (nameMatches.Count == 0)
            {
                throw new DeploymentStepException(DeploymentStep.ApplyConfiguration,
                    "appsettings.json ConnectionStrings block is empty; connection string was NOT changed.");
            }
            if (nameMatches.Count > 1)
            {
                throw new DeploymentStepException(DeploymentStep.ApplyConfiguration,
                    "appsettings.json ConnectionStrings contains multiple entries (" +
                    string.Join(", ", nameMatches.Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value)) +
                    "). Connection string was NOT changed; update the configuration manually.");
            }

            string name = nameMatches[0].Groups[1].Value;
            CreateBackup(appSettingsPath);

            string escaped = connectionString
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");

            string replaced = json.Substring(0, braceStart + 1)
                + " \"" + name + "\": \"" + escaped + "\" "
                + json.Substring(braceEnd);

            // The edit above is string surgery; never leave a broken JSON file behind.
            // (System.Text.Json is already referenced by the project for other deps.)
            if (!IsValidJson(replaced))
            {
                RestoreBackup(appSettingsPath);
                throw new DeploymentStepException(DeploymentStep.ApplyConfiguration,
                    "The edited appsettings.json is not valid JSON; the original file was restored and the " +
                    "connection string was NOT changed. Update the configuration manually.");
            }

            File.WriteAllText(appSettingsPath, replaced, Encoding.UTF8);

            Log.Information("appsettings.json connection string '{Name}' updated in deployed app (backup written)", name);
            return "appsettings.json: connection string '" + name + "' updated (backup written).";
        }

        private static void CreateBackup(string filePath)
        {
            string backupPath = filePath + ".config.bak";
            File.Copy(filePath, backupPath, true);
            Log.Information("Configuration backup written: {Backup}", backupPath);
        }

        private static void RestoreBackup(string filePath)
        {
            string backupPath = filePath + ".config.bak";
            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, filePath, true);
                Log.Information("Original configuration restored from {Backup}", backupPath);
            }
        }

        /// <summary>Structural JSON validity check using the already-referenced System.Text.Json.</summary>
        private static bool IsValidJson(string text)
        {
            try
            {
                using (var document = System.Text.Json.JsonDocument.Parse(text))
                {
                    return document.RootElement.ValueKind == JsonValueKind.Object;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
