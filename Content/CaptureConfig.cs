using System;
using System.ComponentModel;
using System.IO;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace BiomeDatasetCollector.Content;

public sealed class CaptureConfig : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ClientSide;

    [Label("Auto Capture Interval (Seconds)")]
    [Tooltip("How often auto capture triggers while enabled.")]
    [DefaultValue(5f)]
    [Range(0.5f, 60f)]
    [Increment(0.5f)]
    public float AutoCaptureIntervalSeconds { get; set; } = 5f;

    [Label("Auto Capture Enabled")]
    [Tooltip("If enabled, captures are taken automatically on the configured interval.")]
    [DefaultValue(false)]
    public bool AutoCaptureEnabled { get; set; } = false;

    [Label("Capture Cooldown (ms)")]
    [Tooltip("Minimum delay between captures to avoid rapid repeat captures.")]
    [DefaultValue(500)]
    public int CaptureCooldownMs { get; set; } = 500;

    [Label("Output Directory")]
    [Tooltip("Optional custom output path. Leave empty to use the default BiomeCaptures folder.")]
    [DefaultValue("")]
    public string OutputDirectory { get; set; } = string.Empty;

    [Label("Restrict Capture Resolution")]
    [Tooltip("If enabled, captures only save when the current resolution matches one of the allowed values.")]
    [DefaultValue(false)]
    public bool RestrictCaptureResolution { get; set; } = false;

    [Label("Allowed Resolutions")]
    [Tooltip("Comma-separated WxH values. Example: 1920x1080,1280x720")]
    [DefaultValue("1920x1080")]
    public string AllowedResolutions { get; set; } = "1920x1080";

    public static string ResolveOutputRootDirectory()
    {
        try
        {
            CaptureConfig config = ModContent.GetInstance<CaptureConfig>();
            if (!string.IsNullOrWhiteSpace(config.OutputDirectory))
            {
                return Path.GetFullPath(config.OutputDirectory.Trim());
            }
        }
        catch
        {
        }

        return GetDefaultOutputRootDirectory();
    }

    public static string GetDefaultOutputRootDirectory()
    {
        if (!string.IsNullOrWhiteSpace(Main.SavePath))
        {
            return Path.Combine(Main.SavePath, "BiomeCaptures");
        }

        string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documentsPath, "My Games", "Terraria", "tModLoader", "BiomeCaptures");
    }
}
