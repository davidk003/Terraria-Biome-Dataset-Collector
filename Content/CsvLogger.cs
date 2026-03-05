using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Terraria.Localization;

namespace BiomeDatasetCollector.Content;

public struct CaptureRecord
{
    public string Filename;
    public string Biome;
    public string Uuid;
    public double TimeOfDay;
    public bool IsDaytime;
    public string WorldSeed;
    public string WorldName;
    public float WorldX;
    public float WorldY;
    public DateTime Timestamp;
    public int ScreenWidth;
    public int ScreenHeight;
}

public readonly struct CsvSyncResult
{
    public int RemainingRows { get; }

    public int RemovedRows { get; }

    public int MalformedRowsDropped { get; }

    public IReadOnlyDictionary<string, int> RemovedRowsByBiome { get; }

    public int OrphanImageCount { get; }

    public IReadOnlyDictionary<string, int> OrphanImagesByBiome { get; }

    public CsvSyncResult(
        int remainingRows,
        int removedRows,
        int malformedRowsDropped,
        IReadOnlyDictionary<string, int> removedRowsByBiome,
        int orphanImageCount,
        IReadOnlyDictionary<string, int> orphanImagesByBiome)
    {
        RemainingRows = remainingRows;
        RemovedRows = removedRows;
        MalformedRowsDropped = malformedRowsDropped;
        RemovedRowsByBiome = removedRowsByBiome;
        OrphanImageCount = orphanImageCount;
        OrphanImagesByBiome = orphanImagesByBiome;
    }
}

public static class CsvLogger
{
    private const string KeyPrefix = "Mods.BiomeDatasetCollector.CsvLogger";
    private static readonly SemaphoreSlim AppendLock = new(1, 1);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private const int ExpectedColumnCount = 12;

    private const string ExpectedHeader = "filename,biome,uuid,time_of_day,is_daytime,world_seed,world_name,world_x,world_y,timestamp,screen_width,screen_height";

    public static void Append(CaptureRecord record)
    {
        AppendLock.Wait();
        try
        {
            string csvPath = GetCsvPath();
            EnsureCsvReady(csvPath);

            using FileStream stream = new(csvPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using StreamWriter writer = new(stream, Utf8NoBom);
            writer.WriteLine(SerializeRecord(record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(T("AppendFailed"), ex);
        }
        finally
        {
            AppendLock.Release();
        }
    }

    public static HashSet<string> ReadAllUuids()
    {
        AppendLock.Wait();
        try
        {
            string csvPath = GetCsvPath();
            if (!File.Exists(csvPath))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            EnsureCsvReady(csvPath);
            HashSet<string> uuids = new(StringComparer.OrdinalIgnoreCase);

            using FileStream stream = new(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream, Utf8NoBom, true);

            _ = reader.ReadLine();
            while (!reader.EndOfStream)
            {
                string? line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                List<string> columns = ParseCsvLine(line);
                if (columns.Count <= 2)
                {
                    continue;
                }

                string uuid = columns[2].Trim();
                if (uuid.Length == 0)
                {
                    continue;
                }

                uuids.Add(uuid);
            }

            return uuids;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(T("ReadUuidsFailed"), ex);
        }
        finally
        {
            AppendLock.Release();
        }
    }

    public static List<CaptureRecord> ReadAll()
    {
        AppendLock.Wait();
        try
        {
            string csvPath = GetCsvPath();
            if (!File.Exists(csvPath))
            {
                return new List<CaptureRecord>();
            }

            EnsureCsvReady(csvPath);
            return ReadRecordsFromCsv(csvPath, requireExpectedHeader: false, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(T("ReadAllFailed"), ex);
        }
        finally
        {
            AppendLock.Release();
        }
    }

    public static void WriteAll(List<CaptureRecord> records)
    {
        AppendLock.Wait();
        try
        {
            string csvPath = GetCsvPath();
            WriteRecordsAtomic(csvPath, records ?? new List<CaptureRecord>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(T("WriteAllFailed"), ex);
        }
        finally
        {
            AppendLock.Release();
        }
    }

    public static CsvSyncResult SyncWithDisk()
    {
        AppendLock.Wait();
        try
        {
            string root = CaptureConfig.ResolveOutputRootDirectory();
            Directory.CreateDirectory(root);

            string csvPath = Path.Combine(root, "captures.csv");
            EnsureCsvReady(csvPath);

            List<CaptureRecord> records = ReadRecordsFromCsv(csvPath, requireExpectedHeader: false, out int malformedRows);
            List<CaptureRecord> keptRecords = new(records.Count);
            Dictionary<string, int> removedRowsByBiome = new(StringComparer.OrdinalIgnoreCase);

            foreach (CaptureRecord record in records)
            {
                if (TryBuildPathUnderRoot(root, record.Filename, out string imagePath) && File.Exists(imagePath))
                {
                    keptRecords.Add(record);
                    continue;
                }

                IncrementCount(removedRowsByBiome, ResolveBiomeNameForRecord(record));
            }

            int removedRows = records.Count - keptRecords.Count;
            if (removedRows > 0 || malformedRows > 0)
            {
                WriteRecordsAtomic(csvPath, keptRecords);
            }

            HashSet<string> csvImagePaths = new(StringComparer.OrdinalIgnoreCase);
            foreach (CaptureRecord record in keptRecords)
            {
                string normalizedPath = NormalizeCsvPath(record.Filename);
                if (normalizedPath.Length > 0)
                {
                    csvImagePaths.Add(normalizedPath);
                }
            }

            Dictionary<string, int> orphanImagesByBiome = new(StringComparer.OrdinalIgnoreCase);
            int orphanImageCount = 0;
            foreach (string imagePath in Directory.GetFiles(root, "*.png", SearchOption.AllDirectories))
            {
                string normalizedRelativePath;
                try
                {
                    normalizedRelativePath = NormalizeCsvPath(Path.GetRelativePath(root, imagePath));
                }
                catch
                {
                    continue;
                }

                if (normalizedRelativePath.Length == 0 || csvImagePaths.Contains(normalizedRelativePath))
                {
                    continue;
                }

                orphanImageCount++;
                IncrementCount(orphanImagesByBiome, ResolveBiomeNameFromRelativePath(normalizedRelativePath));
            }

            return new CsvSyncResult(
                remainingRows: keptRecords.Count,
                removedRows: removedRows,
                malformedRowsDropped: malformedRows,
                removedRowsByBiome: new Dictionary<string, int>(removedRowsByBiome, StringComparer.OrdinalIgnoreCase),
                orphanImageCount: orphanImageCount,
                orphanImagesByBiome: new Dictionary<string, int>(orphanImagesByBiome, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(T("SyncFailed"), ex);
        }
        finally
        {
            AppendLock.Release();
        }
    }

    public static (int added, int skipped) MergeFrom(string csvPath)
    {
        AppendLock.Wait();
        string? tempExtractionDirectory = null;
        try
        {
            if (string.IsNullOrWhiteSpace(csvPath))
            {
                throw new IOException(T("MergeSourceEmpty"));
            }

            string sourcePath = Path.GetFullPath(csvPath);
            if (!File.Exists(sourcePath))
            {
                throw new IOException(T("MergeSourceMissing", sourcePath));
            }

            string sourceCsvPath;
            string sourceDatasetRoot;
            if (string.Equals(Path.GetExtension(sourcePath), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                tempExtractionDirectory = Path.Combine(Path.GetTempPath(), $"BiomeDatasetCollector_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempExtractionDirectory);
                ExtractZipSafely(sourcePath, tempExtractionDirectory);

                string[] csvCandidates = Directory.GetFiles(tempExtractionDirectory, "captures.csv", SearchOption.AllDirectories);
                if (csvCandidates.Length == 0)
                {
                    throw new IOException(T("MergeArchiveMissingCsv", sourcePath));
                }

                Array.Sort(csvCandidates, StringComparer.OrdinalIgnoreCase);
                sourceCsvPath = csvCandidates[0];
                sourceDatasetRoot = Path.GetDirectoryName(sourceCsvPath) ?? tempExtractionDirectory;
            }
            else
            {
                sourceCsvPath = sourcePath;
                sourceDatasetRoot = Path.GetDirectoryName(sourceCsvPath) ?? string.Empty;
            }

            List<CaptureRecord> sourceRecords = ReadRecordsFromCsv(sourceCsvPath, requireExpectedHeader: true, out int sourceMalformedRows);

            string destinationRoot = CaptureConfig.ResolveOutputRootDirectory();
            Directory.CreateDirectory(destinationRoot);
            string destinationCsvPath = Path.Combine(destinationRoot, "captures.csv");
            EnsureCsvReady(destinationCsvPath);

            List<CaptureRecord> destinationRecords = ReadRecordsFromCsv(destinationCsvPath, requireExpectedHeader: false, out _);
            HashSet<string> existingUuids = new(StringComparer.OrdinalIgnoreCase);
            foreach (CaptureRecord existing in destinationRecords)
            {
                if (!string.IsNullOrWhiteSpace(existing.Uuid))
                {
                    existingUuids.Add(existing.Uuid.Trim());
                }
            }

            int added = 0;
            int skipped = sourceMalformedRows;
            foreach (CaptureRecord candidate in sourceRecords)
            {
                string uuid = (candidate.Uuid ?? string.Empty).Trim();
                if (uuid.Length == 0 || existingUuids.Contains(uuid))
                {
                    skipped++;
                    continue;
                }

                if (!TryBuildPathUnderRoot(sourceDatasetRoot, candidate.Filename, out string sourceImagePath) ||
                    !TryBuildPathUnderRoot(destinationRoot, candidate.Filename, out string destinationImagePath))
                {
                    skipped++;
                    continue;
                }

                if (!File.Exists(sourceImagePath) || File.Exists(destinationImagePath))
                {
                    skipped++;
                    continue;
                }

                string destinationDirectory = Path.GetDirectoryName(destinationImagePath) ?? destinationRoot;
                Directory.CreateDirectory(destinationDirectory);
                File.Copy(sourceImagePath, destinationImagePath, overwrite: false);

                CaptureRecord accepted = candidate;
                accepted.Filename = NormalizeCsvPath(candidate.Filename);
                destinationRecords.Add(accepted);
                existingUuids.Add(uuid);
                added++;
            }

            if (added > 0)
            {
                WriteRecordsAtomic(destinationCsvPath, destinationRecords);
            }

            return (added, skipped);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new IOException(T("MergeFailed", csvPath), ex);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempExtractionDirectory))
            {
                try
                {
                    if (Directory.Exists(tempExtractionDirectory))
                    {
                        Directory.Delete(tempExtractionDirectory, recursive: true);
                    }
                }
                catch
                {
                }
            }

            AppendLock.Release();
        }
    }

    private static string GetCsvPath()
    {
        string root = CaptureConfig.ResolveOutputRootDirectory();
        Directory.CreateDirectory(root);
        return Path.Combine(root, "captures.csv");
    }

    private static void EnsureCsvReady(string csvPath)
    {
        try
        {
            if (!File.Exists(csvPath))
            {
                File.WriteAllText(csvPath, ExpectedHeader + Environment.NewLine, Utf8NoBom);
                return;
            }

            string existingHeader;
            using (FileStream stream = new(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new(stream, Utf8NoBom, true))
            {
                existingHeader = (reader.ReadLine() ?? string.Empty).Trim();
            }

            if (string.Equals(existingHeader, ExpectedHeader, StringComparison.Ordinal))
            {
                return;
            }

            string backupPath = BuildCorruptBackupPath(csvPath);
            File.Move(csvPath, backupPath);
            File.WriteAllText(csvPath, ExpectedHeader + Environment.NewLine, Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(T("SchemaValidationFailed", csvPath), ex);
        }
    }

    private static string BuildCorruptBackupPath(string csvPath)
    {
        string directory = Path.GetDirectoryName(csvPath) ?? string.Empty;
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(csvPath);
        string extension = Path.GetExtension(csvPath);
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

        string candidate = Path.Combine(directory, $"{fileNameWithoutExtension}.corrupt.{timestamp}{extension}");
        int counter = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{fileNameWithoutExtension}.corrupt.{timestamp}.{counter}{extension}");
            counter++;
        }

        return candidate;
    }

    private static string SerializeRecord(CaptureRecord record)
    {
        return string.Join(",",
            Escape(record.Filename ?? string.Empty),
            Escape(record.Biome ?? string.Empty),
            Escape(record.Uuid ?? string.Empty),
            Escape(record.TimeOfDay.ToString(CultureInfo.InvariantCulture)),
            Escape(record.IsDaytime.ToString(CultureInfo.InvariantCulture)),
            Escape(record.WorldSeed ?? string.Empty),
            Escape(record.WorldName ?? string.Empty),
            Escape(record.WorldX.ToString(CultureInfo.InvariantCulture)),
            Escape(record.WorldY.ToString(CultureInfo.InvariantCulture)),
            Escape(record.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
            Escape(record.ScreenWidth.ToString(CultureInfo.InvariantCulture)),
            Escape(record.ScreenHeight.ToString(CultureInfo.InvariantCulture)));
    }

    private static List<CaptureRecord> ReadRecordsFromCsv(string csvPath, bool requireExpectedHeader, out int malformedRows)
    {
        List<CaptureRecord> records = new();
        malformedRows = 0;

        using FileStream stream = new(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream, Utf8NoBom, true);

        string header = (reader.ReadLine() ?? string.Empty).Trim();
        if (requireExpectedHeader && !string.Equals(header, ExpectedHeader, StringComparison.Ordinal))
        {
            throw new IOException(T("UnexpectedHeader", csvPath));
        }

        while (!reader.EndOfStream)
        {
            string? line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            List<string> columns = ParseCsvLine(line);
            if (!TryParseRecord(columns, out CaptureRecord record))
            {
                malformedRows++;
                continue;
            }

            records.Add(record);
        }

        return records;
    }

    private static bool TryParseRecord(List<string> columns, out CaptureRecord record)
    {
        record = default;
        if (columns.Count != ExpectedColumnCount)
        {
            return false;
        }

        string filename = NormalizeCsvPath(columns[0]);
        string uuid = (columns[2] ?? string.Empty).Trim();
        if (filename.Length == 0 || uuid.Length == 0)
        {
            return false;
        }

        if (!double.TryParse(columns[3], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double timeOfDay) ||
            !bool.TryParse(columns[4], out bool isDaytime) ||
            !float.TryParse(columns[7], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float worldX) ||
            !float.TryParse(columns[8], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float worldY) ||
            !DateTime.TryParseExact(columns[9], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime timestamp) ||
            !int.TryParse(columns[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out int screenWidth) ||
            !int.TryParse(columns[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out int screenHeight))
        {
            return false;
        }

        record = new CaptureRecord
        {
            Filename = filename,
            Biome = columns[1] ?? string.Empty,
            Uuid = uuid,
            TimeOfDay = timeOfDay,
            IsDaytime = isDaytime,
            WorldSeed = columns[5] ?? string.Empty,
            WorldName = columns[6] ?? string.Empty,
            WorldX = worldX,
            WorldY = worldY,
            Timestamp = timestamp,
            ScreenWidth = screenWidth,
            ScreenHeight = screenHeight,
        };

        return true;
    }

    private static void WriteRecordsAtomic(string csvPath, List<CaptureRecord> records)
    {
        string tempPath = csvPath + ".tmp";
        try
        {
            using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new(stream, Utf8NoBom))
            {
                writer.WriteLine(ExpectedHeader);
                foreach (CaptureRecord record in records)
                {
                    writer.WriteLine(SerializeRecord(record));
                }
            }

            if (File.Exists(csvPath))
            {
                File.Replace(tempPath, csvPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, csvPath);
            }
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }

            throw;
        }
    }

    private static void ExtractZipSafely(string zipPath, string extractionRoot)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        string fullRoot = EnsureTrailingSeparator(Path.GetFullPath(extractionRoot));

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string destinationPath = Path.GetFullPath(Path.Combine(extractionRoot, entry.FullName));
            if (!destinationPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(T("ZipSlipBlocked", entry.FullName));
            }
        }

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string destinationPath = Path.GetFullPath(Path.Combine(extractionRoot, entry.FullName));
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            string destinationDirectory = Path.GetDirectoryName(destinationPath) ?? extractionRoot;
            Directory.CreateDirectory(destinationDirectory);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static bool TryBuildPathUnderRoot(string root, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        string normalizedPath = NormalizeCsvPath(relativePath);
        if (normalizedPath.Length == 0)
        {
            return false;
        }

        string[] segments = normalizedPath.Split('/');
        foreach (string segment in segments)
        {
            if (segment == "." || segment == "..")
            {
                return false;
            }
        }

        string fullRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private static string ResolveBiomeNameForRecord(CaptureRecord record)
    {
        string biome = (record.Biome ?? string.Empty).Trim();
        if (biome.Length > 0)
        {
            return biome;
        }

        return ResolveBiomeNameFromRelativePath(record.Filename);
    }

    private static string ResolveBiomeNameFromRelativePath(string relativePath)
    {
        string normalizedPath = NormalizeCsvPath(relativePath);
        if (normalizedPath.Length == 0)
        {
            return "Unknown";
        }

        int separatorIndex = normalizedPath.IndexOf('/');
        if (separatorIndex <= 0)
        {
            return "Unknown";
        }

        return normalizedPath[..separatorIndex];
    }

    private static void IncrementCount(Dictionary<string, int> counts, string biome)
    {
        string key = string.IsNullOrWhiteSpace(biome) ? "Unknown" : biome.Trim();
        counts.TryGetValue(key, out int current);
        counts[key] = current + 1;
    }

    private static string NormalizeCsvPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized[1..];
        }

        return normalized;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }

    private static string Escape(string value)
    {
        if (!value.Contains('"') && !value.Contains(',') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static List<string> ParseCsvLine(string line)
    {
        List<string> values = new();
        StringBuilder current = new();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (c == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (inQuotes)
        {
            return new List<string>();
        }

        values.Add(current.ToString());
        return values;
    }

    private static string T(string key, params object[] args)
    {
        string fullKey = $"{KeyPrefix}.{key}";
        return args.Length == 0 ? Language.GetTextValue(fullKey) : Language.GetTextValue(fullKey, args);
    }
}
