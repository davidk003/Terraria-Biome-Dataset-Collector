using Terraria;

namespace BiomeDatasetCollector.Content;

public static class BiomeClassifier
{
    public static string GetBiome(Player player)
    {
        // Priority matters because several Zone* flags can be true at once.
        // Highest -> Lowest:
        // Hell > Dungeon > Mushroom > Jungle > Corruption > Crimson > Hallow > Desert > Snow > Ocean > Space > Underground > Forest
        // Key edge cases:
        // - Jungle over Underground: underground jungle should label Jungle, not Underground.
        // - Corruption over Snow: corrupted snow should label Corruption, not Snow.
        // - Hallow over Desert: hallowed desert should label Hallow, not Desert.

        if (player.ZoneUnderworldHeight)
        {
            return "Hell";
        }

        if (player.ZoneDungeon)
        {
            return "Dungeon";
        }

        if (player.ZoneGlowshroom)
        {
            return "Mushroom";
        }

        if (player.ZoneJungle)
        {
            return "Jungle";
        }

        if (player.ZoneCorrupt)
        {
            return "Corruption";
        }

        if (player.ZoneCrimson)
        {
            return "Crimson";
        }

        if (player.ZoneHallow)
        {
            return "Hallow";
        }

        if (player.ZoneDesert)
        {
            return "Desert";
        }

        if (player.ZoneSnow)
        {
            return "Snow";
        }

        if (player.ZoneBeach)
        {
            return "Ocean";
        }

        if (player.ZoneSkyHeight)
        {
            return "Space";
        }

        if (player.ZoneDirtLayerHeight || player.ZoneRockLayerHeight)
        {
            return "Underground";
        }

        return "Forest";
    }
}
