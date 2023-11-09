using CounterStrikeSharp.API.Modules.Utils;
using System.Text.Json;

namespace CS2AutoUpdater
{
    internal abstract class Configuration
    {
        public static Config config = new();
        
        public static void LoadConfig(string moduleDirectory)
        {
            string path = Path.Combine(moduleDirectory, "autoupdater.json");

            if (File.Exists(path))
            {
                using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using StreamReader sr = new StreamReader(fs);
                config = JsonSerializer.Deserialize<Config>(sr.ReadToEnd())!;
            }
            else
            {
                config = CreateConfig(path);
            }
        }

        private static Config CreateConfig(string configPath)
        {
            var configurationObject = new Config()
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
}

internal class Config
{
    public int UpdateCheckInterval { get; set; }
    public int RestartDelay { get; set; }
    public int ShutdownDelay { get; set; }
    public int MinimumPlayersBeforeInstantRestart { get; set; }
    public string? ChatTag { get; set; }
}