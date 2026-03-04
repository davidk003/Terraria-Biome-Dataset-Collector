using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Localization;
using Terraria.ModLoader;

namespace BiomeDatasetCollector.Content;

public sealed class CaptureSystem : ModSystem
{
    private const int MaxPendingSaveJobs = 6;

    private static readonly SemaphoreSlim FileWriteSemaphore = new(3, 3);
    private static readonly SemaphoreSlim SaveQueueSignal = new(0);
    private static readonly object RequestLock = new();
    private static readonly object UuidLock = new();
    private static readonly object CountLock = new();
    private static readonly object WarmupLock = new();
    private static readonly object SaveQueueLock = new();
    private static readonly Queue<PendingSaveJob> SaveQueue = new();

    private static HashSet<string>? _knownUuids;
    private static Dictionary<string, int>? _captureCountsByBiome;
    private static string _knownUuidsRoot = string.Empty;
    private static string _captureCountsRoot = string.Empty;
    private static string _warmupRoot = string.Empty;
    private static int _warmupGeneration;
    private static bool _warmupStarted;
    private static CancellationTokenSource? _saveWorkerCts;
    private static Task? _saveWorkerTask;
    private static string _pendingBiome;
    private static long _lastErrorTickMs;
    private static long _lastBlockedTickMs;

    private sealed class PendingSaveJob
    {
        public Color[] Pixels = Array.Empty<Color>();
        public int PixelCount;
        public int Width;
        public int Height;
        public string OutputPath = string.Empty;
        public string BiomeDirectory = string.Empty;
        public CaptureRecord Record;
    }

    public static bool RequestCapture(string biome)
    {
        if (Main.gameMenu || Main.dedServ || string.IsNullOrWhiteSpace(biome))
        {
            return false;
        }

        lock (RequestLock)
        {
            if (!string.IsNullOrEmpty(_pendingBiome))
            {
                return false;
            }

            _pendingBiome = biome.Trim();
            return true;
        }
    }

    public override void PostDrawTiles()
    {
        if (Main.gameMenu || Main.dedServ || !TryTakePendingBiome(out string biome))
        {
            return;
        }

        CaptureConfig config = ModContent.GetInstance<CaptureConfig>();
        int requestedWidth = Main.screenWidth;
        int requestedHeight = Main.screenHeight;
        if (!IsResolutionAllowed(config, requestedWidth, requestedHeight, out string blockedKey, out object[] blockedArgs))
        {
            ReportCaptureBlocked(blockedKey, blockedArgs);
            return;
        }

        if (!TryReadCurrentFrame(out Color[] pixels, out int pixelCount, out int width, out int height, out string sourceKind, out Exception error))
        {
            ReportCaptureError("Capture.Chat.Failure.FrameRead", error, sourceKind);
            return;
        }

        string safeBiome = SanitizePathSegment(biome);
        string uuid = CreateShortUuid();
        string fileName = $"{safeBiome}_{uuid}.png";
        string outputRoot = CaptureConfig.ResolveOutputRootDirectory();
        string biomeDirectory = Path.Combine(outputRoot, safeBiome);
        string outputPath = Path.Combine(biomeDirectory, fileName);

        CaptureRecord record = new()
        {
            Filename = $"{safeBiome}/{fileName}",
            Biome = biome,
            Uuid = uuid,
            TimeOfDay = Main.time,
            IsDaytime = Main.dayTime,
            WorldSeed = Main.ActiveWorldFileData?.SeedText ?? string.Empty,
            WorldName = Main.worldName ?? string.Empty,
            WorldX = Main.screenPosition.X,
            WorldY = Main.screenPosition.Y,
            Timestamp = DateTime.UtcNow,
            ScreenWidth = width,
            ScreenHeight = height,
        };

        EncodeAndQueueSave(pixels, pixelCount, width, height, outputPath, biomeDirectory, record);
    }

    public override void OnWorldLoad()
    {
        if (Main.gameMenu || Main.dedServ)
        {
            return;
        }

        EnsureSaveWorkerStarted();
        InvalidateMetadataCache();
        BeginMetadataWarmup();
    }

    public override void OnWorldUnload()
    {
        lock (RequestLock)
        {
            _pendingBiome = null;
        }

        InvalidateMetadataCache();
    }

    public override void Unload()
    {
        _pendingBiome = null;
        StopSaveWorkerAndClearQueue();
        InvalidateMetadataCache();
    }

    public static void InvalidateMetadataCache()
    {
        lock (WarmupLock)
        {
            _warmupGeneration++;
            _warmupStarted = false;
            _warmupRoot = string.Empty;
        }

        lock (UuidLock)
        {
            _knownUuids = null;
            _knownUuidsRoot = string.Empty;
        }

        lock (CountLock)
        {
            _captureCountsByBiome = null;
            _captureCountsRoot = string.Empty;
        }
    }

    private static void EncodeAndQueueSave(Color[] pixels, int pixelCount, int width, int height, string outputPath, string biomeDirectory, CaptureRecord record)
    {
        PendingSaveJob job = new()
        {
            Pixels = pixels,
            PixelCount = pixelCount,
            Width = width,
            Height = height,
            OutputPath = outputPath,
            BiomeDirectory = biomeDirectory,
            Record = record,
        };

        if (!TryEnqueueSaveJob(job))
        {
            ArrayPool<Color>.Shared.Return(job.Pixels);
            ReportCaptureBlocked("Capture.Chat.Blocked.SaveQueueFull", MaxPendingSaveJobs);
        }
    }

    private static bool TryReadCurrentFrame(out Color[] pixels, out int pixelCount, out int width, out int height, out string sourceKind, out Exception error)
    {
        pixels = Array.Empty<Color>();
        pixelCount = 0;
        width = 0;
        height = 0;
        sourceKind = "unknown";
        error = null;

        if (!Main.drawToScreen && Main.screenTarget is not null)
        {
            try
            {
                width = Main.screenTarget.Width;
                height = Main.screenTarget.Height;
                pixelCount = width * height;
                Color[] buffer = ArrayPool<Color>.Shared.Rent(pixelCount);
                pixels = buffer;
                Main.screenTarget.GetData(buffer, 0, pixelCount);
                sourceKind = "screenTarget";
                return true;
            }
            catch (Exception ex)
            {
                if (pixels.Length > 0)
                {
                    ArrayPool<Color>.Shared.Return(pixels);
                    pixels = Array.Empty<Color>();
                }

                error = ex;
            }
        }

        try
        {
            width = Main.screenWidth;
            height = Main.screenHeight;
            pixelCount = width * height;
            Color[] buffer = ArrayPool<Color>.Shared.Rent(pixelCount);
            pixels = buffer;
            Main.instance.GraphicsDevice.GetBackBufferData(buffer, 0, pixelCount);
            sourceKind = "backbuffer";
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            if (pixels.Length > 0)
            {
                ArrayPool<Color>.Shared.Return(pixels);
                pixels = Array.Empty<Color>();
            }

            pixelCount = 0;
            sourceKind = "backbuffer";
            error = ex;
            return false;
        }
    }

    private static bool TryEnqueueSaveJob(PendingSaveJob job)
    {
        EnsureSaveWorkerStarted();

        lock (SaveQueueLock)
        {
            if (SaveQueue.Count >= MaxPendingSaveJobs)
            {
                return false;
            }

            SaveQueue.Enqueue(job);
        }

        SaveQueueSignal.Release();
        return true;
    }

    private static void EnsureSaveWorkerStarted()
    {
        if (_saveWorkerTask is not null && !_saveWorkerTask.IsCompleted)
        {
            return;
        }

        lock (SaveQueueLock)
        {
            if (_saveWorkerTask is not null && !_saveWorkerTask.IsCompleted)
            {
                return;
            }

            CancellationTokenSource cts = new();
            _saveWorkerCts = cts;
            _saveWorkerTask = Task.Run(() => RunSaveWorkerAsync(cts.Token));
        }
    }

    private static async Task RunSaveWorkerAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await SaveQueueSignal.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (TryDequeueSaveJob(out PendingSaveJob? job))
            {
                await ProcessSaveJobAsync(job);
            }
        }
    }

    private static bool TryDequeueSaveJob(out PendingSaveJob? job)
    {
        lock (SaveQueueLock)
        {
            if (SaveQueue.Count == 0)
            {
                job = null;
                return false;
            }

            job = SaveQueue.Dequeue();
            return true;
        }
    }

    private static async Task ProcessSaveJobAsync(PendingSaveJob job)
    {
        try
        {
            await FileWriteSemaphore.WaitAsync();
            try
            {
                try
                {
                    Directory.CreateDirectory(job.BiomeDirectory);
                }
                catch (Exception ex)
                {
                    ReportCaptureError("Capture.Chat.Failure.FileWrite", ex);
                    return;
                }

                try
                {
                    using FileStream stream = new(job.OutputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: false);
                    FramePngEncoder.EncodeToPng(stream, job.Pixels, job.PixelCount, job.Width, job.Height);
                }
                catch (Exception ex)
                {
                    ReportCaptureError("Capture.Chat.Failure.PngEncode", ex);
                    return;
                }

                try
                {
                    CsvLogger.Append(job.Record);
                }
                catch (Exception ex)
                {
                    ReportCaptureError("Capture.Chat.Failure.CsvAppend", ex);
                    return;
                }

                int count = IncrementCaptureCount(job.Record.Biome);
                Main.QueueMainThreadAction(() =>
                {
                    DatasetUiSystem.NotifyCaptureSaved(job.Record.Biome);
                    Main.NewText(Language.GetTextValue("Mods.BiomeDatasetCollector.Capture.Chat.SuccessWithCount", job.Record.Biome, count));
                });
            }
            finally
            {
                FileWriteSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            ReportCaptureError("Capture.Chat.Failure.Unknown", ex);
        }
        finally
        {
            ArrayPool<Color>.Shared.Return(job.Pixels);
        }
    }

    private static void StopSaveWorkerAndClearQueue()
    {
        CancellationTokenSource? cts;
        Task? workerTask;
        lock (SaveQueueLock)
        {
            cts = _saveWorkerCts;
            workerTask = _saveWorkerTask;
            _saveWorkerCts = null;
            _saveWorkerTask = null;
        }

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
                SaveQueueSignal.Release();
            }
            catch
            {
            }

            try
            {
                workerTask?.Wait(1000);
            }
            catch
            {
            }

            cts.Dispose();
        }

        lock (SaveQueueLock)
        {
            while (SaveQueue.Count > 0)
            {
                PendingSaveJob job = SaveQueue.Dequeue();
                ArrayPool<Color>.Shared.Return(job.Pixels);
            }
        }
    }

    private static bool TryTakePendingBiome(out string biome)
    {
        lock (RequestLock)
        {
            if (string.IsNullOrEmpty(_pendingBiome))
            {
                biome = string.Empty;
                return false;
            }

            biome = _pendingBiome;
            _pendingBiome = null;
            return true;
        }
    }

    private static bool IsResolutionAllowed(CaptureConfig config, int width, int height, out string blockedKey, out object[] blockedArgs)
    {
        blockedKey = string.Empty;
        blockedArgs = Array.Empty<object>();
        if (!config.RestrictCaptureResolution)
        {
            return true;
        }

        string allowedText = config.AllowedResolutions ?? string.Empty;
        string[] tokens = allowedText.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            blockedKey = "Capture.Chat.Blocked.AllowedResolutionListEmpty";
            return false;
        }

        foreach (string token in tokens)
        {
            if (TryParseResolutionToken(token, out int allowedWidth, out int allowedHeight) && allowedWidth == width && allowedHeight == height)
            {
                return true;
            }
        }

        blockedKey = "Capture.Chat.Blocked.ResolutionNotAllowed";
        blockedArgs = new object[] { width, height, allowedText };
        return false;
    }

    private static bool TryParseResolutionToken(string token, out int width, out int height)
    {
        width = 0;
        height = 0;
        string trimmed = token.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        int separatorIndex = trimmed.IndexOf('x');
        if (separatorIndex < 0)
        {
            separatorIndex = trimmed.IndexOf('X');
        }

        if (separatorIndex <= 0 || separatorIndex >= trimmed.Length - 1)
        {
            return false;
        }

        string widthPart = trimmed[..separatorIndex];
        string heightPart = trimmed[(separatorIndex + 1)..];
        if (!int.TryParse(widthPart, out width) || !int.TryParse(heightPart, out height))
        {
            return false;
        }

        return width > 0 && height > 0;
    }

    private static string CreateShortUuid()
    {
        EnsureKnownUuidsLoaded();

        lock (UuidLock)
        {
            string candidate;
            do
            {
                candidate = Guid.NewGuid().ToString("N").Substring(0, 8);
            }
            while (!_knownUuids.Add(candidate));

            return candidate;
        }
    }

    private static void EnsureKnownUuidsLoaded()
    {
        string root = CaptureConfig.ResolveOutputRootDirectory();
        if (_knownUuids is not null && string.Equals(_knownUuidsRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (UuidLock)
        {
            if (_knownUuids is not null && string.Equals(_knownUuidsRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                _knownUuids = CsvLogger.ReadAllUuids();
                _knownUuidsRoot = root;
            }
            catch
            {
                _knownUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _knownUuidsRoot = root;
            }
        }
    }

    private static string SanitizePathSegment(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[value.Length];

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            buffer[i] = Array.IndexOf(invalid, c) >= 0 ? '_' : c;
        }

        return new string(buffer).Trim();
    }

    private static int IncrementCaptureCount(string biome)
    {
        lock (CountLock)
        {
            EnsureCaptureCountsLoaded();
            _captureCountsByBiome.TryGetValue(biome, out int current);
            int next = current + 1;
            _captureCountsByBiome[biome] = next;
            return next;
        }
    }

    private static void EnsureCaptureCountsLoaded()
    {
        string root = CaptureConfig.ResolveOutputRootDirectory();
        if (_captureCountsByBiome is not null && string.Equals(_captureCountsRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            List<CaptureRecord> records = CsvLogger.ReadAll();
            foreach (CaptureRecord record in records)
            {
                string key = record.Biome ?? string.Empty;
                if (key.Length == 0)
                {
                    continue;
                }

                counts.TryGetValue(key, out int value);
                counts[key] = value + 1;
            }
        }
        catch
        {
            counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        _captureCountsByBiome = counts;
        _captureCountsRoot = root;
    }

    private static void BeginMetadataWarmup()
    {
        string root;
        try
        {
            root = CaptureConfig.ResolveOutputRootDirectory();
        }
        catch
        {
            return;
        }

        int generation;
        lock (WarmupLock)
        {
            if (_warmupStarted && string.Equals(_warmupRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _warmupStarted = true;
            _warmupRoot = root;
            generation = _warmupGeneration;
        }

        _ = Task.Run(() =>
        {
            HashSet<string> uuids;
            Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

            try
            {
                uuids = CsvLogger.ReadAllUuids();
            }
            catch
            {
                uuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                List<CaptureRecord> records = CsvLogger.ReadAll();
                foreach (CaptureRecord record in records)
                {
                    string key = record.Biome ?? string.Empty;
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    counts.TryGetValue(key, out int value);
                    counts[key] = value + 1;
                }
            }
            catch
            {
                counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            lock (WarmupLock)
            {
                if (generation != _warmupGeneration || !string.Equals(_warmupRoot, root, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            lock (UuidLock)
            {
                _knownUuids = uuids;
                _knownUuidsRoot = root;
            }

            lock (CountLock)
            {
                _captureCountsByBiome = counts;
                _captureCountsRoot = root;
            }
        });
    }

    private static void ReportCaptureError(string messageKey, Exception ex, params object[] args)
    {
        try
        {
            ModContent.GetInstance<BiomeDatasetCollector>().Logger.Warn($"{messageKey}: {ex.Message}");
        }
        catch
        {
        }

        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastErrorTickMs);
        if (now - last < 2000)
        {
            return;
        }

        Interlocked.Exchange(ref _lastErrorTickMs, now);
        Main.QueueMainThreadAction(() => Main.NewText(Language.GetTextValue($"Mods.BiomeDatasetCollector.{messageKey}", args)));
    }

    private static void ReportCaptureBlocked(string messageKey, params object[] args)
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastBlockedTickMs);
        if (now - last < 2000)
        {
            return;
        }

        Interlocked.Exchange(ref _lastBlockedTickMs, now);
        Main.NewText(Language.GetTextValue($"Mods.BiomeDatasetCollector.{messageKey}", args));
    }
}
