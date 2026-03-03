using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

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

public static class CsvLogger
{
    private static readonly SemaphoreSlim AppendLock = new(1, 1);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

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
        finally
        {
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
}
