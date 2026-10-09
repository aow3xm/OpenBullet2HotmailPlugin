using RuriLib.Attributes;
using RuriLib.Models.Bots;

namespace RuriLib.Blocks.PluginTemplate;

[BlockCategory("Plugin Template", "Example plugin blocks")]
public static class TemplateBlocks
{
    [Block("Returns a greeting", name = "Greeting")]
    public static string TemplateGreeting(BotData data,
        [BlockParam("name", "Name to greet (supports <variable>)")][Interpolated] string name = "World")
        => $"Hello, {name}!";
}
