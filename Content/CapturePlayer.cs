using System;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace BiomeDatasetCollector.Content;

public sealed class CapturePlayer : ModPlayer
{
    private const bool DebugBiome = false;
    private const int DebugIntervalTicks = 120;

    private int _autoCaptureTimer;
    private int _debugTimer;
    private ulong _lastCaptureTick;
    private bool _hasCaptured;

    public override void ProcessTriggers(TriggersSet triggersSet)
    {
        if (!IsLocalActivePlayer())
        {
            return;
        }

        CaptureConfig config = ModContent.GetInstance<CaptureConfig>();

        if (BiomeDatasetCollector.CaptureKeybind?.JustPressed == true)
        {
            _ = TryRequestCapture(config);
        }

        if (BiomeDatasetCollector.ToggleAutoKeybind?.JustPressed == true)
        {
            config.AutoCaptureEnabled = !config.AutoCaptureEnabled;
            _autoCaptureTimer = 0;
            Main.NewText(config.AutoCaptureEnabled ? "Auto-capture enabled" : "Auto-capture disabled");
        }
    }

    public override void PostUpdate()
    {
        if (!IsLocalActivePlayer())
        {
            return;
        }

        CaptureConfig config = ModContent.GetInstance<CaptureConfig>();

        if (DebugBiome)
        {
            _debugTimer++;
            if (_debugTimer >= DebugIntervalTicks)
            {
                _debugTimer = 0;
                string debugBiome = BiomeClassifier.GetBiome(Player);
                Main.NewText($"Biome: {debugBiome}");
            }
        }

        if (!config.AutoCaptureEnabled)
        {
            return;
        }

        float intervalSeconds = Math.Max(0f, config.AutoCaptureIntervalSeconds);
        if (intervalSeconds <= 0f)
        {
            return;
        }

        int intervalTicks = Math.Max(1, (int)Math.Round(intervalSeconds * 60f));

        _autoCaptureTimer++;
        if (_autoCaptureTimer < intervalTicks)
        {
            return;
        }

        _autoCaptureTimer = 0;
        _ = TryRequestCapture(config);
    }

    private bool IsLocalActivePlayer()
    {
        return !Main.gameMenu && Player.whoAmI == Main.myPlayer;
    }

    private bool TryRequestCapture(CaptureConfig config)
    {
        if (IsCoolingDown(config.CaptureCooldownMs))
        {
            return false;
        }

        string biome = BiomeClassifier.GetBiome(Player);
        if (!CaptureSystem.RequestCapture(biome))
        {
            return false;
        }

        _hasCaptured = true;
        _lastCaptureTick = Main.GameUpdateCount;
        return true;
    }

    private bool IsCoolingDown(int cooldownMs)
    {
        if (!_hasCaptured)
        {
            return false;
        }

        int sanitizedCooldown = Math.Max(0, cooldownMs);
        int cooldownTicks = (int)Math.Ceiling(sanitizedCooldown / (1000.0 / 60.0));
        if (cooldownTicks <= 0)
        {
            return false;
        }

        ulong elapsed = Main.GameUpdateCount - _lastCaptureTick;
        return elapsed < (ulong)cooldownTicks;
    }
}
