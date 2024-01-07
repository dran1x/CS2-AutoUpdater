using CounterStrikeSharp.API.Modules.Utils;
using System.Text.Json;

namespace CS2AutoUpdater;

internal abstract class Configuration
{
    public static Config Config = new();
    
    public static void LoadConfig(string moduleDirectory)
    {
        var path = Path.Combine(moduleDirectory, "autoupdater.json");
        
        if (File.Exists(path))
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var sr = new StreamReader(fs);
            Config = JsonSerializer.Deserialize<Config>(sr.ReadToEnd())!;
        }
        else
        {
            Config = CreateConfig(path);
        }
    }

    private static Config CreateConfig(string configPath)
    {
        var configurationObject = new Config
        {
            UpdateCheckInterval = 300,
            RestartDelay = 120,
            ShutdownDelay = 5,
            MinimumPlayersBeforeInstantRestart = 1,
            ChatTag = $"{ChatColors.Green}[AutoUpdater]{ChatColors.White}"
        };
        
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        
        File.WriteAllText(configPath, JsonSerializer.Serialize(configurationObject, jsonOptions));
        
        return configurationObject;
    }
}

internal class Config
{
    public int UpdateCheckInterval { get; set; }
    public int RestartDelay { get; set; }
    public int ShutdownDelay { get; set; }
    public int MinimumPlayersBeforeInstantRestart { get; set; }
    public string? ChatTag { get; set; }
}
