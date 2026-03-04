using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace BiomeDatasetCollector.Content;

public sealed class KeybindMigrationSystem : ModSystem
{
    private const string MarkerFileName = "keybind-defaults-migration-v1.done";

    private bool _migrationChecked;

    public override void UpdateUI(GameTime gameTime)
    {
        if (Main.dedServ || _migrationChecked)
        {
            return;
        }

        if (MigrationMarkerExists())
        {
            _migrationChecked = true;
            return;
        }

        if (BiomeDatasetCollector.CaptureKeybind is null || BiomeDatasetCollector.ToggleAutoKeybind is null)
        {
            return;
        }

        if (!TryGetAssignedKeys(BiomeDatasetCollector.CaptureKeybind, InputMode.Keyboard, out IList<string> captureKeys)
            || !TryGetAssignedKeys(BiomeDatasetCollector.ToggleAutoKeybind, InputMode.Keyboard, out IList<string> autoKeys))
        {
            return;
        }

        bool captureUnbound = IsUnbound(captureKeys);
        bool autoUnbound = IsUnbound(autoKeys);
        bool migrated = false;
        if (captureUnbound && autoUnbound)
        {
            migrated |= AssignDefaultIfUnbound(BiomeDatasetCollector.CaptureKeybind, "F9", InputMode.Keyboard);
            migrated |= AssignDefaultIfUnbound(BiomeDatasetCollector.CaptureKeybind, "F9", InputMode.KeyboardUI);
            migrated |= AssignDefaultIfUnbound(BiomeDatasetCollector.ToggleAutoKeybind, "F10", InputMode.Keyboard);
            migrated |= AssignDefaultIfUnbound(BiomeDatasetCollector.ToggleAutoKeybind, "F10", InputMode.KeyboardUI);
        }

        if (migrated)
        {
            try
            {
                PlayerInput.Save();
            }
            catch
            {
            }
        }

        if (migrated || (!captureUnbound && !autoUnbound))
        {
            TryWriteMigrationMarker();
        }

        _migrationChecked = true;
    }

    public override void Unload()
    {
        _migrationChecked = false;
    }

    private static bool TryGetAssignedKeys(ModKeybind keybind, InputMode mode, out IList<string> keys)
    {
        keys = new List<string>();
        try
        {
            keys = keybind.GetAssignedKeys(mode);
            return keys is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool AssignDefaultIfUnbound(ModKeybind keybind, string defaultKey, InputMode mode)
    {
        if (!TryGetAssignedKeys(keybind, mode, out IList<string> keys))
        {
            return false;
        }

        if (!IsUnbound(keys))
        {
            return false;
        }

        try
        {
            keys.Clear();
            keys.Add(defaultKey);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnbound(IList<string> keys)
    {
        if (keys is null || keys.Count == 0)
        {
            return true;
        }

        foreach (string key in keys)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MigrationMarkerExists()
    {
        try
        {
            return File.Exists(GetMarkerPath());
        }
        catch
        {
            return false;
        }
    }

    private static void TryWriteMigrationMarker()
    {
        try
        {
            string markerPath = GetMarkerPath();
            string? directory = Path.GetDirectoryName(markerPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(markerPath, "1");
        }
        catch
        {
        }
    }

    private static string GetMarkerPath()
    {
        string basePath = Main.SavePath;
        if (string.IsNullOrWhiteSpace(basePath))
        {
            string fallbackOutputRoot = CaptureConfig.GetDefaultOutputRootDirectory();
            basePath = Directory.GetParent(fallbackOutputRoot)?.FullName ?? fallbackOutputRoot;
        }

        return Path.Combine(basePath, "BiomeDatasetCollector", MarkerFileName);
    }
}
