using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.Localization;
using Terraria.ModLoader;

namespace BiomeDatasetCollector.Content;

public sealed class DatasetCommand : ModCommand
{
    private const string KeyPrefix = "Mods.BiomeDatasetCollector.Commands.DatasetCommand";
    private static int _operationInProgress;
    private static bool _cleanConfirmationPending;

    public static bool IsOperationInProgress => Interlocked.CompareExchange(ref _operationInProgress, 0, 0) == 1;

    public static bool IsCleanConfirmationPending => _cleanConfirmationPending;

    public override string Command => "dataset";

    public override string Usage => Language.GetTextValue($"{KeyPrefix}.Usage");

    public override string Description => Language.GetTextValue($"{KeyPrefix}.Description");

    public override CommandType Type => CommandType.Chat;

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        if (args.Length == 0)
        {
            Main.NewText(T($"{KeyPrefix}.Help"));
            return;
        }

        string subcommand = args[0].Trim().ToLowerInvariant();
        switch (subcommand)
        {
            case "status":
                HandleStatus();
                return;

            case "clean":
                HandleClean(args);
                return;

            case "zip":
                HandleZip();
                return;

            case "merge":
                HandleMerge(args, input);
                return;

            case "ui":
                HandleUiToggle();
                return;

            default:
                Main.NewText(T($"{KeyPrefix}.UnknownSubcommand", subcommand));
                Main.NewText(T($"{KeyPrefix}.Help"));
                return;
        }
    }

    public static void RunStatusFromUi()
    {
        HandleStatus();
    }

    public static void RunCleanFromUi()
    {
        HandleClean(new[] { "clean" });
    }

    public static void RunCleanConfirmFromUi()
    {
        HandleClean(new[] { "clean", "confirm" });
    }

    public static void RunZipFromUi()
    {
        HandleZip();
    }

    public static void RunMergeFromUi(string mergePath)
    {
        if (string.IsNullOrWhiteSpace(mergePath))
        {
            Main.NewText(T($"{KeyPrefix}.MergeMissingPath"));
            return;
        }

        HandleMerge(new[] { "merge", mergePath }, $"dataset merge {mergePath}");
    }

    private static void HandleStatus()
    {
        try
        {
            string root = CaptureConfig.ResolveOutputRootDirectory();
            if (!Directory.Exists(root))
            {
                Main.NewText(T($"{KeyPrefix}.StatusNoDataset", root));
                return;
            }

            Dictionary<string, int> counts = BuildBiomeCounts(root);
            long totalBytes = GetDirectorySizeBytes(root);
            int totalImages = 0;
            foreach (KeyValuePair<string, int> pair in counts)
            {
                totalImages += pair.Value;
            }

            double totalMb = totalBytes / (1024d * 1024d);
            bool csvCountReady = TryGetCsvRowCount(out int csvRows, out string csvError);
            string csvRowsText = csvCountReady ? csvRows.ToString(CultureInfo.InvariantCulture) : "?";

            Main.NewText(T($"{KeyPrefix}.StatusHeader", root));
            Main.NewText(T($"{KeyPrefix}.StatusSummary", totalImages, csvRowsText, totalMb.ToString("F2", CultureInfo.InvariantCulture)));

            if (totalImages <= 0)
            {
                Main.NewText(T($"{KeyPrefix}.StatusNoCaptures"));
            }
            else
            {
                Main.NewText(T($"{KeyPrefix}.StatusBiomeHeader"));
            }

            foreach (KeyValuePair<string, int> pair in counts)
            {
                if (pair.Value > 0)
                {
                    Main.NewText(T($"{KeyPrefix}.StatusBiomeLine", pair.Key, pair.Value));
                }
            }

            if (csvCountReady)
            {
                if (csvRows == totalImages)
                {
                    Main.NewText(T($"{KeyPrefix}.StatusConsistencyOk"));
                }
                else
                {
                    Main.NewText(T($"{KeyPrefix}.StatusConsistencyMismatch", csvRows, totalImages));
                }
            }
            else
            {
                Main.NewText(T($"{KeyPrefix}.StatusCsvReadWarning", csvError));
            }
        }
        catch (Exception ex)
        {
            Main.NewText(T($"{KeyPrefix}.StatusError", ex.Message));
        }
    }

    private static void HandleUiToggle()
    {
        DatasetUiSystem.TogglePanel();
        Main.NewText(T(DatasetUiSystem.IsVisible ? $"{KeyPrefix}.UiOpened" : $"{KeyPrefix}.UiClosed"));
    }

    private static void HandleClean(string[] args)
    {
        if (Interlocked.CompareExchange(ref _operationInProgress, 0, 0) == 1)
        {
            Main.NewText(T($"{KeyPrefix}.OperationInProgress"));
            return;
        }

        if (args.Length >= 2 && string.Equals(args[1], "confirm", StringComparison.OrdinalIgnoreCase))
        {
            if (!_cleanConfirmationPending)
            {
                Main.NewText(T($"{KeyPrefix}.CleanConfirmMissing"));
                return;
            }

            try
            {
                string root = CaptureConfig.ResolveOutputRootDirectory();
                Directory.CreateDirectory(root);

                DeleteDatasetContents(root);
                CreateBiomeDirectories(root);
                ResetCsvViaLogger();

                _cleanConfirmationPending = false;
                Main.NewText(T($"{KeyPrefix}.CleanDone", root));
                DatasetUiSystem.NotifyDatasetMutated();
            }
            catch (Exception ex)
            {
                Main.NewText(T($"{KeyPrefix}.CleanError", ex.Message));
                DatasetUiSystem.NotifyDatasetMutated();
            }

            return;
        }

        _cleanConfirmationPending = true;
        Main.NewText(T($"{KeyPrefix}.CleanPending"));
    }

    private static void HandleZip()
    {
        if (!TryBeginOperation())
        {
            Main.NewText(T($"{KeyPrefix}.OperationInProgress"));
            return;
        }

        Main.NewText(T($"{KeyPrefix}.ZipStarted"));
        _ = Task.Run(() =>
        {
            try
            {
                string captureRoot = CaptureConfig.ResolveOutputRootDirectory();
                if (!Directory.Exists(captureRoot))
                {
                    QueueText($"{KeyPrefix}.ZipNoDataset", captureRoot);
                    return;
                }

                string parent = Directory.GetParent(Path.GetFullPath(captureRoot))?.FullName ?? Path.GetFullPath(captureRoot);
                long datasetBytes = GetDirectorySizeBytes(captureRoot);
                long estimatedRequired = datasetBytes + (datasetBytes / 5) + (32L * 1024L * 1024L);
                if (!HasEnoughDiskSpace(parent, estimatedRequired, out long availableBytes, out string diskError))
                {
                    QueueText($"{KeyPrefix}.ZipDiskSpaceError", diskError);
                    return;
                }

                string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string zipName = $"BiomeCaptures_{timestamp}.zip";
                string zipPath = Path.Combine(parent, zipName);

                ZipFile.CreateFromDirectory(captureRoot, zipPath, CompressionLevel.Optimal, false);

                double zipMb = new FileInfo(zipPath).Length / (1024d * 1024d);
                double freeMb = availableBytes / (1024d * 1024d);
                QueueText($"{KeyPrefix}.ZipSuccess", zipPath, zipMb.ToString("F2", CultureInfo.InvariantCulture), freeMb.ToString("F2", CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                QueueText($"{KeyPrefix}.ZipError", ex.Message);
            }
            finally
            {
                EndOperation();
                DatasetUiSystem.NotifyDatasetMutated();
            }
        });
    }

    private static void HandleMerge(string[] args, string input)
    {
        if (args.Length < 2)
        {
            Main.NewText(T($"{KeyPrefix}.MergeMissingPath"));
            return;
        }

        if (!TryBeginOperation())
        {
            Main.NewText(T($"{KeyPrefix}.OperationInProgress"));
            return;
        }

        string mergePath = ResolveMergePath(args, input);
        Main.NewText(T($"{KeyPrefix}.MergeStarted", mergePath));
        _ = Task.Run(() =>
        {
            try
            {
                string fullPath = Path.GetFullPath(mergePath);
                if (!File.Exists(fullPath))
                {
                    QueueText($"{KeyPrefix}.MergePathNotFound", fullPath);
                    return;
                }

                long sourceBytes = GetPathSizeBytes(fullPath);
                string outputRoot = CaptureConfig.ResolveOutputRootDirectory();
                long estimatedRequired = (sourceBytes * 2) + (16L * 1024L * 1024L);
                if (!HasEnoughDiskSpace(outputRoot, estimatedRequired, out _, out string diskError))
                {
                    QueueText($"{KeyPrefix}.MergeDiskSpaceError", diskError);
                    return;
                }

                (int added, int skipped) = CsvLogger.MergeFrom(fullPath);
                QueueText($"{KeyPrefix}.MergeSuccess", added, skipped);
            }
            catch (Exception ex)
            {
                QueueText($"{KeyPrefix}.MergeError", ex.Message);
            }
            finally
            {
                EndOperation();
                DatasetUiSystem.NotifyDatasetMutated();
            }
        });
    }

    private static Dictionary<string, int> BuildBiomeCounts(string root)
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (string biome in DatasetBiomes.Ordered)
        {
            counts[biome] = 0;
        }

        foreach (string directory in Directory.GetDirectories(root))
        {
            string biome = Path.GetFileName(directory);
            int count = 0;
            try
            {
                count = Directory.GetFiles(directory, "*.png", SearchOption.AllDirectories).Length;
            }
            catch
            {
                count = 0;
            }

            if (counts.ContainsKey(biome))
            {
                counts[biome] += count;
            }
            else
            {
                counts[biome] = count;
            }
        }

        return counts;
    }

    private static long GetDirectorySizeBytes(string root)
    {
        long total = 0;
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
            }
        }

        return total;
    }

    private static long GetPathSizeBytes(string path)
    {
        if (File.Exists(path))
        {
            return new FileInfo(path).Length;
        }

        if (Directory.Exists(path))
        {
            return GetDirectorySizeBytes(path);
        }

        return 0;
    }

    private static bool HasEnoughDiskSpace(string targetPath, long estimatedRequiredBytes, out long availableBytes, out string error)
    {
        availableBytes = 0;
        error = string.Empty;

        try
        {
            string full = Path.GetFullPath(targetPath);
            string root = Path.GetPathRoot(full) ?? full;
            DriveInfo driveInfo = new(root);
            availableBytes = driveInfo.AvailableFreeSpace;
            if (estimatedRequiredBytes <= 0)
            {
                estimatedRequiredBytes = 8L * 1024L * 1024L;
            }

            if (availableBytes >= estimatedRequiredBytes)
            {
                return true;
            }

            double needMb = estimatedRequiredBytes / (1024d * 1024d);
            double haveMb = availableBytes / (1024d * 1024d);
            error = T($"{KeyPrefix}.DiskSpaceDetail", needMb.ToString("F2", CultureInfo.InvariantCulture), haveMb.ToString("F2", CultureInfo.InvariantCulture));
            return false;
        }
        catch (Exception ex)
        {
            error = T($"{KeyPrefix}.DiskSpaceUnknown", ex.Message);
            return false;
        }
    }

    private static bool TryGetCsvRowCount(out int rowCount, out string error)
    {
        rowCount = 0;
        error = string.Empty;

        try
        {
            List<CaptureRecord> records = CsvLogger.ReadAll();
            rowCount = records.Count;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void DeleteDatasetContents(string root)
    {
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            File.Delete(file);
        }

        foreach (string directory in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            Directory.Delete(directory, true);
        }
    }

    private static void CreateBiomeDirectories(string root)
    {
        foreach (string biome in DatasetBiomes.Ordered)
        {
            Directory.CreateDirectory(Path.Combine(root, biome));
        }
    }

    private static void ResetCsvViaLogger()
    {
        CsvLogger.WriteAll(new List<CaptureRecord>());
    }

    private static string ResolveMergePath(string[] args, string input)
    {
        if (args.Length == 2)
        {
            return args[1];
        }

        string trimmed = (input ?? string.Empty).Trim();
        int mergeIndex = trimmed.IndexOf("merge", StringComparison.OrdinalIgnoreCase);
        if (mergeIndex < 0)
        {
            return string.Join(" ", args, 1, args.Length - 1);
        }

        string path = trimmed[(mergeIndex + "merge".Length)..].Trim();
        if (path.StartsWith("\"", StringComparison.Ordinal) && path.EndsWith("\"", StringComparison.Ordinal) && path.Length >= 2)
        {
            path = path[1..^1];
        }

        return path;
    }

    private static void QueueText(string key, params object[] args)
    {
        Main.QueueMainThreadAction(() => Main.NewText(T(key, args)));
    }

    private static bool TryBeginOperation()
    {
        return Interlocked.CompareExchange(ref _operationInProgress, 1, 0) == 0;
    }

    private static void EndOperation()
    {
        Interlocked.Exchange(ref _operationInProgress, 0);
    }

    private static string T(string key, params object[] args)
    {
        return args.Length == 0 ? Language.GetTextValue(key) : Language.GetTextValue(key, args);
    }
}
