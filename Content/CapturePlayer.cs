using System;
using Terraria;
using Terraria.GameInput;
using Terraria.Localization;
using Terraria.ModLoader;

namespace BiomeDatasetCollector.Content;

public sealed class CapturePlayer : ModPlayer
{
    private const bool DebugBiome = false;
    private const int DebugIntervalTicks = 120;

    private bool _autoCaptureEnabled;
    private bool _autoCaptureInitialized;
    private bool _lastConfigAutoCaptureEnabled;
    private int _autoCaptureTimer;
    private int _debugTimer;
    private ulong _lastCaptureTick;
    private bool _hasCaptured;

    public override void Initialize()
    {
        _autoCaptureInitialized = false;
        _autoCaptureEnabled = false;
        _lastConfigAutoCaptureEnabled = false;
        _autoCaptureTimer = 0;
    }

    public override void ProcessTriggers(TriggersSet triggersSet)
    {
        if (!IsLocalActivePlayer())
        {
            return;
        }

        CaptureConfig config = ModContent.GetInstance<CaptureConfig>();
        EnsureAutoCaptureInitialized(config);
        SyncAutoCaptureFromConfig(config);

        if (BiomeDatasetCollector.CaptureKeybind?.JustPressed == true)
        {
            _ = TryRequestCapture(config);
        }

        if (BiomeDatasetCollector.ToggleAutoKeybind?.JustPressed == true)
        {
            _autoCaptureEnabled = !_autoCaptureEnabled;
            _lastConfigAutoCaptureEnabled = _autoCaptureEnabled;
            config.AutoCaptureEnabled = _autoCaptureEnabled;
            _autoCaptureTimer = 0;
            Main.NewText(Language.GetTextValue(_autoCaptureEnabled
                ? "Mods.BiomeDatasetCollector.Capture.Chat.AutoCaptureEnabled"
                : "Mods.BiomeDatasetCollector.Capture.Chat.AutoCaptureDisabled"));
        }
    }

    public override void PostUpdate()
    {
        if (!IsLocalActivePlayer())
        {
            return;
        }

        CaptureConfig config = ModContent.GetInstance<CaptureConfig>();
        EnsureAutoCaptureInitialized(config);
        SyncAutoCaptureFromConfig(config);

        if (DebugBiome)
        {
            _debugTimer++;
            if (_debugTimer >= DebugIntervalTicks)
            {
                _debugTimer = 0;
                string debugBiome = BiomeClassifier.GetBiome(Player);
                Main.NewText(Language.GetTextValue("Mods.BiomeDatasetCollector.Capture.Chat.DebugBiome", debugBiome));
            }
        }

        if (!_autoCaptureEnabled)
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

    private void EnsureAutoCaptureInitialized(CaptureConfig config)
    {
        if (_autoCaptureInitialized)
        {
            return;
        }

        _autoCaptureEnabled = false;
        _lastConfigAutoCaptureEnabled = false;
        _autoCaptureTimer = 0;
        _autoCaptureInitialized = true;

        if (config.AutoCaptureEnabled)
        {
            config.AutoCaptureEnabled = false;
        }
    }

    private void SyncAutoCaptureFromConfig(CaptureConfig config)
    {
        if (config.AutoCaptureEnabled == _lastConfigAutoCaptureEnabled)
        {
            return;
        }

        _lastConfigAutoCaptureEnabled = config.AutoCaptureEnabled;
        _autoCaptureEnabled = config.AutoCaptureEnabled;
        _autoCaptureTimer = 0;
        Main.NewText(Language.GetTextValue(_autoCaptureEnabled
            ? "Mods.BiomeDatasetCollector.Capture.Chat.AutoCaptureEnabled"
            : "Mods.BiomeDatasetCollector.Capture.Chat.AutoCaptureDisabled"));
    }

    private bool IsLocalActivePlayer()
    {
        if (Main.gameMenu)
        {
            _autoCaptureInitialized = false;
            return false;
        }

        return Player.whoAmI == Main.myPlayer;
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
