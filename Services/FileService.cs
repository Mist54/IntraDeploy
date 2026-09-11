using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using IntraDeploy.Configuration;
using IntraDeploy.Models;

namespace IntraDeploy.Services
{
    /// <summary>
    /// Copies the published application folder into the managed deployment directory.
    /// Keeps previous versions intact during copy; after a successful deploy,
    /// <see cref="PruneOldVersions"/> can trim oldest siblings while never deleting
    /// the live IIS physical path.
    /// </summary>
    public class FileService
    {
        /// <summary>Files copied from source on the last <see cref="CopyApplication"/> (for SmokeTest).</summary>
        public int LastFilesFromSource { get; private set; }

        /// <summary>Files reused from .deploying-bak on the last overwrite copy (for SmokeTest).</summary>
        public int LastFilesFromBackup { get; private set; }

        /// <summary>
        /// Lists version folder names under DeploymentRoot\ApplicationName\, newest first
        /// (by last write time). Missing app folder → empty list.
        /// </summary>
        public static IList<VersionFolderInfo> ListVersionFolders(string deploymentRoot, string applicationName)
        {
            var results = new List<VersionFolderInfo>();
            if (string.IsNullOrWhiteSpace(deploymentRoot) || string.IsNullOrWhiteSpace(applicationName))
            {
                return results;
            }

            string appRoot = Path.Combine(deploymentRoot.Trim(), applicationName.Trim());
            if (!Directory.Exists(appRoot))
            {
                return results;
            }

            foreach (string dir in Directory.EnumerateDirectories(appRoot))
            {
                string name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name) || name.EndsWith(".deploying-bak", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DateTime lastWrite;
                try
                {
                    lastWrite = Directory.GetLastWriteTimeUtc(dir);
                }
                catch (Exception)
                {
                    lastWrite = DateTime.MinValue;
                }

                results.Add(new VersionFolderInfo
                {
                    Version = name,
                    FullPath = dir,
                    LastWriteTimeUtc = lastWrite
                });
            }

            return results
                .OrderByDescending(v => v.LastWriteTimeUtc)
                .ThenByDescending(v => v.Version, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// After a successful deploy, deletes oldest version folders under the app root
        /// beyond <paramref name="maxToKeep"/>. Never deletes <paramref name="livePhysicalPath"/>
        /// (the folder IIS currently serves). Best effort: individual delete failures are logged.
        /// Returns the number of folders removed.
        /// </summary>
        public int PruneOldVersions(
            string deploymentRoot,
            string applicationName,
            int maxToKeep,
            string livePhysicalPath)
        {
            if (maxToKeep < 1)
            {
                maxToKeep = 1;
            }

            IList<VersionFolderInfo> versions = ListVersionFolders(deploymentRoot, applicationName);
            if (versions.Count <= maxToKeep)
            {
                Log.Information(
                    "Version prune: {Count} folder(s) under {App} — within MaxVersionsToKeep={Max}; nothing to remove",
                    versions.Count, applicationName, maxToKeep);
                return 0;
            }

            string liveNormalized = NormalizeFolderPath(livePhysicalPath);
            var toPrune = versions.Skip(maxToKeep).ToList();
            int removed = 0;

            foreach (VersionFolderInfo folder in toPrune)
            {
                if (!string.IsNullOrEmpty(liveNormalized)
                    && string.Equals(NormalizeFolderPath(folder.FullPath), liveNormalized, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information(
                        "Version prune: skipping live IIS path {Path} (would have been beyond keep limit)",
                        folder.FullPath);
                    continue;
                }

                try
                {
                    Log.Information("Version prune: removing old folder {Path}", folder.FullPath);
                    DeleteDirectoryRobust(folder.FullPath);
                    removed++;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Version prune: could not remove {Path}: {Message}", folder.FullPath, ex.Message);
                }
            }

            Log.Information(
                "Version prune complete for {App}: removed {Removed}, kept up to {Max} (plus any protected live path)",
                applicationName, removed, maxToKeep);
            return removed;
        }

        private static string NormalizeFolderPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }
            try
            {
                return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();
            }
            catch (Exception)
            {
                return path.TrimEnd('\\', '/').Trim().ToLowerInvariant();
            }
        }

        /// <summary>
        /// Copies sourceFolder to targetFolder. The target must not exist unless the operator
        /// confirmed overwriting it (AllowOverwriteExistingFolder).
        /// </summary>
        public void CopyApplication(
            DeploymentRequest request,
            Action<ProgressEventArgs> progress,
            CancellationToken token)
        {
            string source = request.SourceFolder;
            string target = request.TargetFolder;

            if (!Directory.Exists(source))
            {
                throw new DeploymentStepException(DeploymentStep.CopyApplicationFiles,
                    "Source folder does not exist: " + source);
            }

            if (Directory.Exists(target) && !request.AllowOverwriteExistingFolder)
            {
                throw new DeploymentStepException(DeploymentStep.PrepareTargetFolder,
                    "Target deployment folder already exists: " + target);
            }

            Log.Information("Copying application files: {Source} -> {Target}", source, target);
            progress?.Invoke(new ProgressEventArgs(DeploymentStep.CopyApplicationFiles,
                "Copying application files...", 0, true));

            bool isOverwrite = Directory.Exists(target);
            string backupPath = isOverwrite ? target + ".deploying-bak" : null;

            if (isOverwrite)
            {
                // Move the live folder aside first. If copy fails we put it back
                // so the previous version is not left half-deleted.
                Log.Information("Overwrite confirmed: moving existing folder to backup {Backup}", backupPath);
                if (Directory.Exists(backupPath))
                {
                    DeleteDirectoryRobust(backupPath);
                }
                try
                {
                    Directory.Move(target, backupPath);
                }
                catch (Exception ex)
                {
                    throw new DeploymentStepException(DeploymentStep.PrepareTargetFolder,
                        "Could not move the existing deployment folder aside for the overwrite (the application " +
                        "may still be running or files may be locked): " + ex.Message, ex);
                }
            }

            Directory.CreateDirectory(target);
            LastFilesFromSource = 0;
            LastFilesFromBackup = 0;

            try
            {
                long totalBytes = GetDirectorySize(source, token);
                int sourceFileCount = CountFiles(source, token);
                CopyDirectory(source, target, backupPath, totalBytes, progress, token);
                int targetFileCount = CountFiles(target, token);
                if (targetFileCount != sourceFileCount)
                {
                    throw new DeploymentStepException(DeploymentStep.CopyApplicationFiles,
                        "Copy verification failed: file count differs. Source: " + sourceFileCount +
                        ", target: " + targetFileCount + ".");
                }
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Copy cancelled by operator; restoring previous folder for {Target}", target);
                RestoreFromBackup(target, backupPath);
                throw;
            }
            catch (DeploymentStepException dex)
            {
                Log.Error("Copy verification failed; restoring previous folder for {Target}", target);
                dex.RollbackNote = RestoreFromBackup(target, backupPath) ?? dex.RollbackNote;
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Copy failed; restoring previous folder for {Target}", target);
                throw new DeploymentStepException(DeploymentStep.CopyApplicationFiles,
                    "Copying the application files failed: " + ex.Message)
                {
                    RollbackNote = RestoreFromBackup(target, backupPath)
                };
            }

            long copiedBytes = GetDirectorySize(target, token);
            if (copiedBytes != GetDirectorySize(source, token))
            {
                string rollbackNote = RestoreFromBackup(target, backupPath);
                throw new DeploymentStepException(DeploymentStep.CopyApplicationFiles,
                    "Copy verification failed: copied size differs from source size. " +
                    "Source: " + GetDirectorySize(source, token) + " bytes, target: " + copiedBytes + " bytes.")
                {
                    RollbackNote = rollbackNote
                };
            }

            if (isOverwrite)
            {
                // Copy verified; the backup of the previous version can go.
                Log.Information("Copy verified; removing backup {Backup}", backupPath);
                try
                {
                    DeleteDirectoryRobust(backupPath);
                }
                catch (Exception ex)
                {
                    Log.Warning("Could not remove backup folder {Backup}: {Message}", backupPath, ex.Message);
                }
            }

            Log.Information(
                "Copy completed and verified. {Bytes} bytes at {Target} (fromSource={FromSource}, fromBackup={FromBackup})",
                copiedBytes, target, LastFilesFromSource, LastFilesFromBackup);
            progress?.Invoke(new ProgressEventArgs(DeploymentStep.CopyApplicationFiles,
                "Application files copied and verified.", 100, false));
        }

        /// <summary>Deletes a directory tree, retrying briefly when files are still locked.</summary>
        public static void DeleteDirectoryRobust(string path)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, true);
                    }
                    return;
                }
                catch (IOException) when (attempt < 5)
                {
                    Log.Warning("Delete of {Path} failed (attempt {Attempt}), retrying in 500 ms", path, attempt);
                    Thread.Sleep(500);
                }
                catch (UnauthorizedAccessException) when (attempt < 5)
                {
                    SetAttributesNormal(path);
                    Log.Warning("Delete of {Path} unauthorized (attempt {Attempt}); cleared attributes, retrying", path, attempt);
                }
            }
        }

        /// <summary>
        /// Restores the previous deployment folder after a failed overwrite copy:
        /// removes the partial copy and moves the backup back into place.
        /// Returns an operator facing note about what happened (null when there was
        /// nothing to roll back).
        /// </summary>
        private static string RestoreFromBackup(string target, string backupPath)
        {
            try
            {
                if (Directory.Exists(target))
                {
                    DeleteDirectoryRobust(target);
                }
                if (backupPath != null && Directory.Exists(backupPath))
                {
                    Directory.Move(backupPath, target);
                    Log.Information("Previous version restored to {Target}", target);
                    return "Previous deployment restored automatically. The application is still pointing at " +
                           "the previous version.";
                }
                return null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not restore the previous version for {Target}. " +
                              "A backup may exist at {Backup}; restore it manually.", target, backupPath);
                return "The previous deployment could NOT be restored automatically." +
                       (backupPath != null ? " A backup may exist at " + backupPath + "." : string.Empty);
            }
        }

        private static void TryDeletePartial(string target)
        {
            try
            {
                if (Directory.Exists(target))
                {
                    DeleteDirectoryRobust(target);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not remove partial target folder {Target}. Remove it manually.", target);
            }
        }

        private static void SetAttributesNormal(string path)
        {
            var directoryInfo = new DirectoryInfo(path) { Attributes = FileAttributes.Normal };
            foreach (var info in directoryInfo.GetFileSystemInfos("*", SearchOption.AllDirectories))
            {
                info.Attributes = FileAttributes.Normal;
            }
        }

        private static int CountFiles(string path, CancellationToken token)
        {
            int count = 0;
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                count++;
            }
            return count;
        }

        private static long GetDirectorySize(string path, CancellationToken token)
        {
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                total += new FileInfo(file).Length;
            }
            return total;
        }

        /// <summary>
        /// Copies source → target. When <paramref name="backupPath"/> exists and SizeAndTime mode
        /// is on, unchanged files (same length + LastWriteTimeUtc) are copied from bak instead of source.
        /// Parallelism from AppConfig.CopyMaxDegreeOfParallelism; cancel checked per file / every 50 files.
        /// </summary>
        private void CopyDirectory(
            string source,
            string target,
            string backupPath,
            long totalBytes,
            Action<ProgressEventArgs> progress,
            CancellationToken token)
        {
            Directory.CreateDirectory(target);

            foreach (string directoryPath in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                string relative = directoryPath.Substring(source.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(target, relative));
            }

            var files = new List<string>();
            foreach (string filePath in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                files.Add(filePath);
            }

            bool reuseFromBak = backupPath != null
                && Directory.Exists(backupPath)
                && string.Equals(AppConfig.CopyCompareMode, "SizeAndTime", StringComparison.OrdinalIgnoreCase);

            int degree = AppConfig.CopyMaxDegreeOfParallelism;
            long copiedBytes = 0;
            long lastReported = 0;
            int fromSource = 0;
            int fromBackup = 0;
            int processed = 0;
            object progressLock = new object();
            Exception firstError = null;

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = degree,
                CancellationToken = token
            };

            try
            {
                Parallel.ForEach(files, options, (filePath, state) =>
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();

                        string relative = filePath.Substring(source.Length)
                            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        string destination = Path.Combine(target, relative);
                        string destDir = Path.GetDirectoryName(destination);
                        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                        {
                            Directory.CreateDirectory(destDir);
                        }

                        var sourceInfo = new FileInfo(filePath);
                        bool usedBackup = false;

                        if (reuseFromBak)
                        {
                            string bakFile = Path.Combine(backupPath, relative);
                            if (File.Exists(bakFile))
                            {
                                var bakInfo = new FileInfo(bakFile);
                                if (bakInfo.Length == sourceInfo.Length
                                    && bakInfo.LastWriteTimeUtc == sourceInfo.LastWriteTimeUtc)
                                {
                                    File.Copy(bakFile, destination, true);
                                    usedBackup = true;
                                }
                            }
                        }

                        if (!usedBackup)
                        {
                            File.Copy(filePath, destination, true);
                        }

                        long fileLen = sourceInfo.Length;
                        long nowCopied = Interlocked.Add(ref copiedBytes, fileLen);
                        if (usedBackup)
                        {
                            Interlocked.Increment(ref fromBackup);
                        }
                        else
                        {
                            Interlocked.Increment(ref fromSource);
                        }

                        int done = Interlocked.Increment(ref processed);
                        if (done % 50 == 0)
                        {
                            token.ThrowIfCancellationRequested();
                        }

                        if (totalBytes > 0 && progress != null)
                        {
                            lock (progressLock)
                            {
                                if (nowCopied - lastReported > 4 * 1024 * 1024)
                                {
                                    lastReported = nowCopied;
                                    int percent = (int)Math.Min(99, nowCopied * 100 / totalBytes);
                                    progress(new ProgressEventArgs(DeploymentStep.CopyApplicationFiles,
                                        "Copying application files... " + percent + "% (cancel after current file)",
                                        percent, false));
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        state.Stop();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref firstError, ex, null);
                        state.Stop();
                    }
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AggregateException aex)
            {
                AggregateException flat = aex.Flatten();
                foreach (Exception inner in flat.InnerExceptions)
                {
                    if (inner is OperationCanceledException)
                    {
                        throw (OperationCanceledException)inner;
                    }
                }
                throw flat.InnerExceptions.Count > 0 ? flat.InnerExceptions[0] : flat;
            }

            if (firstError != null)
            {
                throw firstError;
            }

            token.ThrowIfCancellationRequested();

            LastFilesFromSource = fromSource;
            LastFilesFromBackup = fromBackup;
            Log.Information(
                "Copied {FileCount} files ({Bytes} bytes) to {Target}: {FromSource} from source, {FromBackup} reused from backup (parallelism={Degree})",
                files.Count, copiedBytes, target, fromSource, fromBackup, degree);
        }
    }

    /// <summary>One sibling version folder under DeploymentRoot\App\.</summary>
    public class VersionFolderInfo
    {
        public string Version { get; set; }
        public string FullPath { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
    }

    /// <summary>
    /// Exception carrying the pipeline step that failed, used by DeploymentService
    /// to stop safely and report the exact step.
    /// </summary>
    public class DeploymentStepException : Exception
    {
        public DeploymentStepException(DeploymentStep step, string message)
            : base(message)
        {
            Step = step;
        }

        public DeploymentStepException(DeploymentStep step, string message, Exception inner)
            : base(message, inner)
        {
            Step = step;
        }

        public DeploymentStep Step { get; private set; }

        /// <summary>
        /// Optional operator facing note about rollback/restore actions taken after the
        /// failure (e.g. "Previous deployment restored."). Propagated to the result.
        /// </summary>
        public string RollbackNote { get; set; }
    }
}
