using System;
using System.Collections.Generic;

namespace BiomeDatasetCollector.Content;

public static class DatasetBiomes
{
    public static readonly string[] Ordered =
    {
        "Hell",
        "Dungeon",
        "Mushroom",
        "Jungle",
        "Corruption",
        "Crimson",
        "Hallow",
        "Desert",
        "Snow",
        "Ocean",
        "Space",
        "Underground",
        "Forest",
    };

    public static Dictionary<string, int> CreateEmptyCounts()
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (string biome in Ordered)
        {
            counts[biome] = 0;
        }

        return counts;
    }
}
