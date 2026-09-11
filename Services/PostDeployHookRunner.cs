using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Serilog;
using IntraDeploy.Configuration;
using IntraDeploy.Models;

namespace IntraDeploy.Services
{
    /// <summary>
    /// Runs optional post-deploy hooks after health: local PowerShell (.ps1) scripts
    /// and/or HTTP(S) GET URLs. Never logs secrets from responses.
    /// </summary>
    public static class PostDeployHookRunner
    {
        /// <summary>
        /// Resolves the hook list string: request override when non-null (empty = none),
        /// otherwise App.config.
        /// </summary>
        public static string ResolveHookList(DeploymentRequest request)
        {
            if (request != null && request.PostDeployHooks != null)
            {
                return request.PostDeployHooks.Trim();
            }
            return AppConfig.PostDeployHooks ?? string.Empty;
        }

        /// <summary>Splits a semicolon/comma-separated hook list; trims empties.</summary>
        public static string[] ParseHookList(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new string[0];
            }

            char[] separators = { ';', ',' };
            return raw.Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
        }

        /// <summary>
        /// Runs all hooks sequentially. Aggregate exit code is 0 when all succeed;
        /// otherwise the first non-zero exit code (HTTP uses status code when &gt;= 400).
        /// </summary>
        public static (int ExitCode, string Summary) RunAll(IEnumerable<string> hooks, int timeoutSeconds)
        {
            var list = hooks == null
                ? new string[0]
                : hooks.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).ToArray();

            if (list.Length == 0)
            {
                return (0, null);
            }

            int timeoutMs = Math.Max(1, timeoutSeconds) * 1000;
            var sb = new StringBuilder();
            int aggregateExit = 0;

            for (int i = 0; i < list.Length; i++)
            {
                string hook = list[i];
                int exit;
                string line;
                if (IsHttpUrl(hook))
                {
                    var http = RunHttpHook(hook, timeoutMs);
                    exit = http.ExitCode;
                    line = http.Summary;
                }
                else
                {
                    var script = RunScriptHook(hook, timeoutMs);
                    exit = script.ExitCode;
                    line = script.Summary;
                }

                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }
                sb.Append(line);

                if (exit != 0 && aggregateExit == 0)
                {
                    aggregateExit = exit;
                }
            }

            return (aggregateExit, sb.ToString());
        }

        public static bool IsHttpUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static (int ExitCode, string Summary) RunHttpHook(string url, int timeoutMs)
        {
            try
            {
                HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(url);
                webRequest.Method = "GET";
                webRequest.Timeout = timeoutMs;
                webRequest.AllowAutoRedirect = true;
                webRequest.UserAgent = "IntraDeploy post-deploy hook";

                using (HttpWebResponse response = (HttpWebResponse)webRequest.GetResponse())
                {
                    int status = (int)response.StatusCode;
                    Log.Information("Post-deploy HTTP hook {Url} -> {Status}", url, status);
                    if (status >= 400)
                    {
                        return (status, "HTTP " + status + " from " + url);
                    }
                    return (0, "HTTP " + status + " from " + url);
                }
            }
            catch (WebException wex)
            {
                HttpWebResponse errorResponse = wex.Response as HttpWebResponse;
                if (errorResponse != null)
                {
                    int status = (int)errorResponse.StatusCode;
                    Log.Warning("Post-deploy HTTP hook {Url} -> {Status}", url, status);
                    return (status, "HTTP " + status + " from " + url);
                }

                Log.Warning(wex, "Post-deploy HTTP hook failed for {Url}", url);
                return (1, "HTTP unreachable " + url + ": " + wex.Message);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Post-deploy HTTP hook failed for {Url}", url);
                return (1, "HTTP error " + url + ": " + ex.Message);
            }
        }

        private static (int ExitCode, string Summary) RunScriptHook(string path, int timeoutMs)
        {
            if (!File.Exists(path))
            {
                Log.Warning("Post-deploy script not found: {Path}", path);
                return (1, "Script not found: " + path);
            }

            if (!path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("Post-deploy script is not a .ps1 file: {Path}", path);
                return (1, "Not a .ps1 script: " + path);
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + path + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        return (1, "Could not start PowerShell for " + path);
                    }

                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        Log.Warning("Post-deploy script timed out: {Path}", path);
                        return (1, "Script timed out (" + (timeoutMs / 1000) + "s): " + path);
                    }

                    int exit = process.ExitCode;
                    Log.Information("Post-deploy script {Path} exited {Exit}", path, exit);
                    if (exit == 0)
                    {
                        return (0, "Script OK (exit 0): " + path);
                    }
                    return (exit, "Script failed (exit " + exit + "): " + path);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Post-deploy script failed: {Path}", path);
                return (1, "Script error " + path + ": " + ex.Message);
            }
        }
    }
}
