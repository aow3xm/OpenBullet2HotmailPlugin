using RuriLib.Logging;
using RuriLib.Models.Bots;
using RuriLib.Models.Configs;
using RuriLib.Models.Data;
using RuriLib.Models.Environment;
using RuriLib.Models.Proxies;

namespace Hotmail.Tests;

public static class BotDataFactory
{
    public static BotData Create(IBotLogger logger,
        string email = "user@example.com",
        string password = "hunter2",
        string refreshToken = "refresh-token-1",
        string clientId = "client-id-1")
        => new(
            new Providers(settings: null),
            new ConfigSettings(),
            logger,
            new DataLine($"{email}:{password}:{refreshToken}:{clientId}", new WordlistType()));
}
