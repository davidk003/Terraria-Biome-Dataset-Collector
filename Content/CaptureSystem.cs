using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ModLoader;

namespace BiomeDatasetCollector.Content;

public sealed class CaptureSystem : ModSystem
{
    private static readonly SemaphoreSlim FileWriteSemaphore = new(3, 3);
    private static readonly object RequestLock = new();
    private static readonly object UuidLock = new();

    private static HashSet<string>? _knownUuids;
    private static string _pendingBiome;
    private static long _lastErrorTickMs;

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

        if (!TryReadCurrentFrame(out Color[] pixels, out int width, out int height, out string sourceKind, out Exception error))
        {
            ReportCaptureError($"Frame read failed ({sourceKind})", error);
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

        EncodeAndQueueSave(pixels, width, height, outputPath, biomeDirectory, record);
    }

    public override void OnWorldUnload()
    {
        lock (RequestLock)
        {
            _pendingBiome = null;
        }
    }

    public override void Unload()
    {
        _knownUuids = null;
        _pendingBiome = null;
    }

    private static void EncodeAndQueueSave(Color[] pixels, int width, int height, string outputPath, string biomeDirectory, CaptureRecord record)
    {
        byte[] pngBytes;
        try
        {
            using Texture2D texture = new(Main.instance.GraphicsDevice, width, height, false, SurfaceFormat.Color);
            texture.SetData(pixels);

            using MemoryStream memoryStream = new();
            texture.SaveAsPng(memoryStream, width, height);
            pngBytes = memoryStream.ToArray();
        }
        catch (Exception ex)
        {
            ReportCaptureError("PNG encode failed", ex);
            return;
        }

        _ = Task.Run(async () =>
        {
            await FileWriteSemaphore.WaitAsync();
            try
            {
                Directory.CreateDirectory(biomeDirectory);
                await File.WriteAllBytesAsync(outputPath, pngBytes);
                CsvLogger.Append(record);
                Main.QueueMainThreadAction(() => Main.NewText($"Captured {record.Biome}: {record.Uuid}"));
            }
            catch (Exception ex)
            {
                ReportCaptureError("Capture file write failed", ex);
            }
            finally
            {
                FileWriteSemaphore.Release();
            }
        });
    }

    private static bool TryReadCurrentFrame(out Color[] pixels, out int width, out int height, out string sourceKind, out Exception error)
    {
        pixels = null;
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
                pixels = new Color[width * height];
                Main.screenTarget.GetData(pixels);
                sourceKind = "screenTarget";
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }

        try
        {
            width = Main.screenWidth;
            height = Main.screenHeight;
            pixels = new Color[width * height];
            Main.instance.GraphicsDevice.GetBackBufferData(pixels);
            sourceKind = "backbuffer";
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            sourceKind = "backbuffer";
            error = ex;
            return false;
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
        if (_knownUuids is not null)
        {
            return;
        }

        lock (UuidLock)
        {
            if (_knownUuids is not null)
            {
                return;
            }

            try
            {
                _knownUuids = CsvLogger.ReadAllUuids();
            }
            catch
            {
                _knownUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

    private static void ReportCaptureError(string context, Exception ex)
    {
        try
        {
            ModContent.GetInstance<BiomeDatasetCollector>().Logger.Warn($"{context}: {ex.Message}");
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
        Main.QueueMainThreadAction(() => Main.NewText($"Capture failed: {context}"));
    }
}
