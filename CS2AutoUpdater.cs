using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Timers;

namespace CS2AutoUpdater
{
    [MinimumApiVersion(14)]
    public partial class CS2AutoUpdater : BasePlugin
    {
        public override string ModuleName => "CS2AutoUpdater";
        public override string ModuleAuthor => "DRANIX";
        public override string ModuleDescription => "Auto Updater for Counter-Strike 2.";
        public override string ModuleVersion => "1.0.1";
        private const string steamApiEndpoint = "http://api.steampowered.com/ISteamApps/UpToDateCheck/v0001/?appid=730&version={0}&format=json";
        private static CounterStrikeSharp.API.Modules.Timers.Timer updateCheck = null!;
        private static readonly bool[] playersNotified = new bool[Server.MaxPlayers];
        private static float updateFoundTime;
        private static bool updateAvailable;
        private static int requiredVersion;
        private static bool isMapLoading;

        public override void Load(bool hotReload)
        {
            Configuration.LoadConfig(this.ModuleDirectory);
            
            RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
            
            this.RegisterListener<Listeners.OnGameServerSteamAPIActivated>(OnGameServerSteamAPIActivated);
            this.RegisterListener<Listeners.OnMapStart>(OnMapStart);
            this.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
            this.RegisterListener<Listeners.OnClientConnected>(playerSlot => { playersNotified[playerSlot + 1] = false; });
            this.RegisterListener<Listeners.OnClientDisconnectPost>(playerSlot => { playersNotified[playerSlot + 1] = false; });
            
            updateCheck = AddTimer((float)Configuration.config.UpdateCheckInterval, CheckForServerUpdate, TimerFlags.REPEAT);
        }
        
        [GameEventHandler]
        private static HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
        {
            if (!updateAvailable) return HookResult.Continue;
            
            CCSPlayerController player = @event.Userid;

            if (!player.IsValid || player.IsBot) return HookResult.Continue;
            if (playersNotified[player.EntityIndex!.Value.Value]) return HookResult.Continue;
            
            NotifyPlayer(player);

            return HookResult.Continue;
        }

        private async void CheckForServerUpdate()
        {
            if (!await IsUpdateAvailable()) return;
            
            updateFoundTime = Server.CurrentTime;

            if (!isMapLoading)
            {
                List<CCSPlayerController> players = Utilities.GetPlayers().Where(player => !player.IsBot).ToList();
                
                if (players.Count <= Configuration.config.MinimumPlayersBeforeInstantRestart)
                {
                    PrepareServerShutdown();
                    return;
                }
                
                foreach (var player in players) NotifyPlayer(player);
            }

            AddTimer((float)Configuration.config.RestartDelay, PrepareServerShutdown);

            updateAvailable = true;

            updateCheck?.Kill();
        }

        private static void NotifyPlayer(CCSPlayerController player)
        {
            int remainingTime = Configuration.config.RestartDelay - (int)(Server.CurrentTime - updateFoundTime);

            if (remainingTime < 0) remainingTime = 1;
    
            string timeToRestart = remainingTime >= 60 ? $"{remainingTime / 60} minute{(remainingTime >= 120 ? "s" : "")}" : $"{remainingTime} second{(remainingTime > 1 ? "s" : "")}";
            player.PrintToChat($" {Configuration.config.ChatTag} New Counter-Strike 2 update released (Build: {requiredVersion}) the server will restart in {timeToRestart}");
    
            playersNotified[player.EntityIndex!.Value.Value] = true;
        }

        private void OnGameServerSteamAPIActivated()
        {
            ConsoleLog("Steam API activated. Server will be checked for updates.");
        }
        
        private static void OnMapStart(string mapName)
        {
            isMapLoading = false;
        }
        
        private static void OnMapEnd()
        {
            isMapLoading = true;
        }

        private async Task<bool> IsUpdateAvailable()
        {
            string steamInfPatchVersion = GetSteamInfPatchVersion();

            if (string.IsNullOrEmpty(steamInfPatchVersion))
            {
                ConsoleLog("Unable to get the current patch version of Counter-Strike 2. The server will not be checked for updates.", ConsoleColor.Red);
                Server.ExecuteCommand($"css_plugins stop {this.ModuleName}");
                return false;
            }

            try
            {
                using HttpClient httpClient = new HttpClient();
                HttpResponseMessage response = await httpClient.GetAsync(string.Format(steamApiEndpoint, steamInfPatchVersion));

                if (response.IsSuccessStatusCode)
                {
                    string responseString = await response.Content.ReadAsStringAsync();
                    var upToDateObject = JsonSerializer.Deserialize<UpToDateCheckResponse>(responseString)!;

                    if (upToDateObject.Response is { Success: true, UpToDate: false })
                    {
                        requiredVersion = upToDateObject.Response.RequiredVersion!;
                        ConsoleLog($"New Counter-Strike 2 update released (Build: {requiredVersion})");
                        return true;
                    }
                }
                else
                {
                    ConsoleLog($"HTTP request failed with status code: {response.StatusCode}", ConsoleColor.Red);
                }
            }
            catch (Exception ex)
            {
                ConsoleLog($"An error occurred: {ex.Message}", ConsoleColor.Red);
            }

            return false;
        }

        private void PrepareServerShutdown()
        {
            List<CCSPlayerController> players = Utilities.GetPlayers().Where(player => !player.IsBot).ToList();
            
            foreach (var player in players)
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

            AddTimer((float)Configuration.config.ShutdownDelay, ShutdownServer);
        }

        private string GetSteamInfPatchVersion()
        {
            string steamInfPath = Path.Combine(Server.GameDirectory, "csgo", "steam.inf");

            if (File.Exists(steamInfPath))
            {
                try
                {
                    Match match = PatchVersion().Match(File.ReadAllText(steamInfPath));

                    if (match.Success) return match.Groups[1].Value;
                    else
                    {
                        ConsoleLog("The 'PatchVersion' key could not be located in the steam.inf file.", ConsoleColor.Red);
                    }
                }
                catch (Exception ex)
                {
                    ConsoleLog($"An error occurred while reading the 'steam.inf' file: {ex.Message}", ConsoleColor.Red);
                }
            }
            else
            {
                ConsoleLog($"The 'steam.inf' file was not found in the root directory of Counter-Strike 2. Path: \"{steamInfPath}\"", ConsoleColor.Red);
            }

            return string.Empty;
        }

        private void ConsoleLog(string message, ConsoleColor color = ConsoleColor.Green)
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"[{this.ModuleName}] {message}");
            Console.ResetColor();
        }
        
        private void ShutdownServer()
        {
            ConsoleLog($"Restarting the server due to the new game update. (Build: {requiredVersion})");
            Server.ExecuteCommand("quit");
        }

        [GeneratedRegex("PatchVersion=(.+)")]
        private static partial Regex PatchVersion();
    }
}