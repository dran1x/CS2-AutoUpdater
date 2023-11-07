using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2AutoUpdater
{
    [MinimumApiVersion(5)]
    public partial class CS2AutoUpdater : BasePlugin
    {
        public override string ModuleName => "CS2AutoUpdater";
        public override string ModuleAuthor => "DRANIX";
        public override string ModuleDescription => "Auto Updater for Counter-Strike 2.";
        public override string ModuleVersion => "1.0.0";
        private static readonly string steamAPIEndpoint = "http://api.steampowered.com/ISteamApps/UpToDateCheck/v0001/?appid=730&version={0}&format=json";
        private CounterStrikeSharp.API.Modules.Timers.Timer updateCheck = null!;
        private static Config config = null!;
        private static int requiredVersion;

        public override void Load(bool hotReload)
        {
            config = LoadConfig();
            updateCheck = AddTimer((float)config.UpdateCheckInterval, () => CheckUpdate(), TimerFlags.REPEAT);
        }

        private async void CheckUpdate()
        {
            try
            {
                string steamINFPatchVersion = GetSteamINFPatchVersion();

                if (string.IsNullOrEmpty(steamINFPatchVersion))
                {
                    Console.WriteLine("[AutoUpdater] Unable to get the current patch version of Counter-Strike 2. The server will not be checked for updates.");
                    return;
                }

                using HttpClient httpClient = new HttpClient();
                HttpResponseMessage response = await httpClient.GetAsync(string.Format(steamAPIEndpoint, steamINFPatchVersion));

                if (response.IsSuccessStatusCode)
                {
                    string responseString = await response.Content.ReadAsStringAsync();
                    UpToDateCheckResponse upToDateObject = JsonSerializer.Deserialize<UpToDateCheckResponse>(responseString)!;

                    if (upToDateObject.Response.Success && !upToDateObject.Response.UpToDate)
                    {
                        int totalSeconds = (int)config.RestartDelay;
                        string timeToRestart = totalSeconds >= 60 ? $"{totalSeconds / 60} minute{(totalSeconds >= 120 ? "s" : "")}" : $"{totalSeconds} second{(totalSeconds > 1 ? "s" : "")}";

                        List<CCSPlayerController> players = Utilities.GetPlayers().Where(player => !player.IsBot).ToList();

                        if (config.InstantRestartWhenEmpty && players.Count < 1) { QuitServer(); }

                        requiredVersion = upToDateObject.Response.RequiredVersion!;

                        foreach (var player in players)
                        {
                            player.PrintToChat($" {config.ChatTag} New Counter-Strike 2 update released (Build: {requiredVersion}) the server will restart in {timeToRestart}");
                        }

                        AddTimer((float)config.RestartDelay, () => ShutdownServer(), TimerFlags.REPEAT);

                        updateCheck?.Kill();
                    }
                }
                else
                {
                    Console.WriteLine($"[AutoUpdater] HTTP request failed with status code: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AutoUpdater] An error occurred: {ex.Message}");
            }
        }

        private void ShutdownServer()
        {
            foreach (var player in Utilities.GetPlayers())
            {
                switch (player.Connected)
                {
                    case PlayerConnectedState.PlayerConnected:
                    case PlayerConnectedState.PlayerConnecting:
                    case PlayerConnectedState.PlayerReconnecting:
                        Server.ExecuteCommand($"kickid {player.UserId} Due to the game update (Build: {requiredVersion}), the server is now restarting.");
                        break;
                }
            }

            AddTimer((float)config.ShutdownDelay, () => { QuitServer(); });
        }

        private static string GetSteamINFPatchVersion()
        {
            string steamInfPath = Path.Combine(Server.GameDirectory, "csgo", "steam.inf");

            if (File.Exists(steamInfPath))
            {
                try
                {
                    Match match = PatchVersion().Match(File.ReadAllText(steamInfPath));

                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                    else
                    {
                        Console.WriteLine("[AutoUpdater] The 'PatchVersion' key could not be located in the steam.inf file.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AutoUpdater] An error occurred while reading the 'steam.inf' file: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[AutoUpdater] The 'steam.inf' file was not found in the root directory of Counter-Strike 2. Path: \"{steamInfPath}\"");
            }

            return string.Empty;
        }

        private Config LoadConfig()
        {
            string configPath = Path.Combine(ModuleDirectory, "autoupdater.json");

            if (!File.Exists(configPath))
            {
                Config config = new Config
                {
                    UpdateCheckInterval = 300,
                    RestartDelay = 120,
                    ShutdownDelay = 5,
                    InstantRestartWhenEmpty = true,
                    ChatTag = $"{ChatColors.Green}[AutoUpdater]{ChatColors.White}"
                };

                string configContent = JsonSerializer.Serialize(config, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(configPath, configContent);
                Console.WriteLine($"[AutoUpdater] Configuration file \"{configPath}\" created successfully.");
            }

            return JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath))!;
        }

        private static void QuitServer()
        {
            Console.WriteLine($"[AutoUpdater] Restarting the server due to the new game update. (Build: {requiredVersion})");
            Server.ExecuteCommand("quit");
        }

        [GeneratedRegex("PatchVersion=(.+)")]
        private static partial Regex PatchVersion();
    }
}