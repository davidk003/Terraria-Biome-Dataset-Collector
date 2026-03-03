using Terraria;
using Terraria.ModLoader;

namespace BiomeDatasetCollector.Content;

public sealed class CapturePlayer : ModPlayer
{
    private const bool DebugBiome = false;
    private const int DebugIntervalTicks = 120;

    private int _debugTimer;

    public override void PostUpdate()
    {
        if (!DebugBiome)
        {
            return;
        }

        if (Main.gameMenu || Player.whoAmI != Main.myPlayer)
        {
            return;
        }

        _debugTimer++;
        if (_debugTimer < DebugIntervalTicks)
        {
            return;
        }

        _debugTimer = 0;
        string biome = BiomeClassifier.GetBiome(Player);
        Main.NewText($"Biome: {biome}");
    }
}
