using Terraria.ModLoader;
using Terraria.Localization;

namespace BiomeDatasetCollector;

public sealed class BiomeDatasetCollector : Mod
{
    public static ModKeybind CaptureKeybind { get; private set; }

    public static ModKeybind ToggleAutoKeybind { get; private set; }

    public override void Load()
    {
        CaptureKeybind = KeybindLoader.RegisterKeybind(this, Language.GetTextValue("Mods.BiomeDatasetCollector.Keybinds.CaptureDatasetScreenshot"), "F9");
        ToggleAutoKeybind = KeybindLoader.RegisterKeybind(this, Language.GetTextValue("Mods.BiomeDatasetCollector.Keybinds.ToggleAutoCapture"), "F10");
    }

    public override void Unload()
    {
        CaptureKeybind = null;
        ToggleAutoKeybind = null;
    }
}
