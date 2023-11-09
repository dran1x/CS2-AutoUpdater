using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2AutoUpdater
{
    [MinimumApiVersion(14)]
    public partial class CS2AutoUpdater : BasePlugin
    {
        public override string ModuleName => "CS2AutoUpdater";
        public override string ModuleAuthor => "DRANIX";
        public override string ModuleDescription => "Auto Updater for Counter-Strike 2.";
        public override string ModuleVersion => "1.0.2";
        private const string steamApiEndpoint = "http://api.steampowered.com/ISteamApps/UpToDateCheck/v0001/?appid=730&version={steamInfPatchVersion}";
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
            
            ConVar sv_hibernate_when_empty = ConVar.Find("sv_hibernate_when_empty")!;
            
            this.RegisterListener<Listeners.OnGameServerSteamAPIActivated>(OnGameServerSteamAPIActivated);
            this.RegisterListener<Listeners.OnMapStart>(OnMapStart);
            this.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
            this.RegisterListener<Listeners.OnClientConnected>(playerSlot => { playersNotified[playerSlot + 1] = false; });
            this.RegisterListener<Listeners.OnClientDisconnectPost>(playerSlot => { playersNotified[playerSlot + 1] = false; });
            
            updateCheck = AddTimer(Configuration.config.UpdateCheckInterval, CheckServerForUpdate, TimerFlags.REPEAT);
            
            if (sv_hibernate_when_empty.GetPrimitiveValue<bool>()) ConsoleLog("'sv_hibernate_when_empty' is enabled. This plugin might not work as expected.", ConsoleColor.Yellow);
        }
        
        [GameEventHandler]
        private static HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
        {
            if (!updateAvailable) return HookResult.Continue;
            
            CCSPlayerController player = @event.Userid;
            
            if (!player.IsValid || player.IsBot || player.TeamNum < (byte)CsTeam.Spectator) return HookResult.Continue;
            if (playersNotified[player.EntityIndex!.Value.Value]) return HookResult.Continue;
            
            NotifyPlayer(player);
            
            return HookResult.Continue;
        }

        private async void CheckServerForUpdate()
        {
            try
            { 
                if (!await IsUpdateAvailable()) return;
            } 
            catch (Exception ex)
            {
                ConsoleLog($"Failed to request Steam API for updates: {ex.Message}");
                return;
            }
            
            updateCheck?.Kill();
            
            updateAvailable = true;
            
            updateFoundTime = Server.CurrentTime;
            
            List<CCSPlayerController> players = GetCurrentPlayers();
            
            if (players.Count <= Configuration.config.MinimumPlayersBeforeInstantRestart)
            {
                PrepareServerShutdown();
                return;
            }

            if (!isMapLoading) foreach (var player in players) NotifyPlayer(player);

            AddTimer(Configuration.config.RestartDelay, PrepareServerShutdown);
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
                HttpResponseMessage response = await httpClient.GetAsync(steamApiEndpoint.Replace("{steamInfPatchVersion}", steamInfPatchVersion));
                
                if (response.IsSuccessStatusCode)
                {
                    var upToDateObject = JsonSerializer.Deserialize<UpToDateCheckResponse>(await response.Content.ReadAsStringAsync())!;

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
            List<CCSPlayerController> players = GetCurrentPlayers();
            
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

        private static List<CCSPlayerController> GetCurrentPlayers()
        {
            return Utilities.GetPlayers().Where(player => player is { IsValid: true, IsBot: false, IsHLTV: false }).ToList();
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