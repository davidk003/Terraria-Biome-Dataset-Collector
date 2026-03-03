using Terraria.ModLoader;

namespace BiomeDatasetCollector.Content;

public sealed class DatasetCommand : ModCommand
{
    public override string Command => "dataset";

    public override string Usage => "/dataset";

    public override string Description => "Placeholder command.";

    public override CommandType Type => CommandType.Chat;

    public override void Action(CommandCaller caller, string input, string[] args)
    {
    }
}
