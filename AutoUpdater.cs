namespace AutoUpdater
{
    using CounterStrikeSharp.API;
    using CounterStrikeSharp.API.Core;
    using CounterStrikeSharp.API.Core.Attributes;
    using CounterStrikeSharp.API.Modules.Timers;
    using CounterStrikeSharp.API.Modules.Cvars;
    using CounterStrikeSharp.API.Modules.Utils;
    using System.Text.RegularExpressions;
    using Microsoft.Extensions.Logging;
    using System.Net.Http.Json;
    
    [MinimumApiVersion(178)]
    public partial class AutoUpdater : BasePlugin, IPluginConfig<PluginConfig>
    {
        public override string ModuleName => "AutoUpdater";
        public override string ModuleAuthor => "dranix";
        public override string ModuleDescription => "Auto Updater for Counter-Strike 2.";
        public override string ModuleVersion => "1.0.4";

        private const string SteamApiEndpoint =
            "https://api.steampowered.com/ISteamApps/UpToDateCheck/v0001/?appid=730&version={0}";

        public required PluginConfig Config { get; set; } = new();
        private static Dictionary<int, bool> PlayersNotified = new();
        private static ConVar? sv_visiblemaxplayers;
        private static double UpdateFoundTime;
        private static bool IsServerLoading;
        private static bool RestartRequired;
        private static bool UpdateAvailable;
        private static int RequiredVersion;

        public override void Load(bool hotReload)
        {
            sv_visiblemaxplayers = ConVar.Find("sv_visiblemaxplayers");

            RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);

            RegisterListener<Listeners.OnGameServerSteamAPIActivated>(OnGameServerSteamAPIActivated);
            RegisterListener<Listeners.OnServerHibernationUpdate>(OnServerHibernationUpdate);
            RegisterListener<Listeners.OnClientConnected>(OnClientConnected);
            RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
            RegisterListener<Listeners.OnMapStart>(OnMapStart);
            RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

            AddTimer(Config.UpdateCheckInterval, CheckServerVersion, TimerFlags.REPEAT);
        }

        public override void Unload(bool hotReload) => Dispose();

        public void OnConfigParsed(PluginConfig config)
        {
            if (config.Version < Config.Version) Logger.LogWarning(Localizer["AutoUpdater.Console.ConfigVersionMismatch", Config.Version, config.Version]);

            Config = config;
        }

        private void OnGameServerSteamAPIActivated() => Logger.LogInformation(Localizer["AutoUpdater.Console.UpdateCheckInitiated"]);

        private void OnServerHibernationUpdate(bool isHibernating)
        {
            if (isHibernating) Logger.LogInformation(Localizer["AutoUpdater.Console.HibernateWarning"]);
        }

        private static void OnMapStart(string mapName)
        {
            PlayersNotified.Clear();
            IsServerLoading = false;
        }

        private void OnMapEnd()
        {
            if (RestartRequired && Config.ShutdownOnMapChangeIfPendingUpdate) ShutdownServer();
            IsServerLoading = true;
        }

        private static void OnClientConnected(int playerSlot)
        {
            CCSPlayerController? player = Utilities.GetPlayerFromSlot(playerSlot);
            if (player == null || (player?.IsBot ?? false) || (player?.IsHLTV ?? false)) return;

            PlayersNotified.Add(playerSlot, false);
        }

        private static void OnClientDisconnect(int playerSlot)
        {
            PlayersNotified.Remove(playerSlot);
        }

        private async void CheckServerVersion()
        {
            try
            {
                if (RestartRequired || !await IsUpdateAvailable()) return;
                
                Server.NextFrame(ManageServerUpdate);
            }
            catch (Exception ex)
            {
                Logger.LogError(Localizer["AutoUpdater.Console.ErrorUpdateCheck", ex.Message]);
            }
        }

        private void ManageServerUpdate()
        {
            if (!UpdateAvailable)
            {
                UpdateFoundTime = Server.CurrentTime;
                UpdateAvailable = true;
                
                Logger.LogInformation(Localizer["AutoUpdater.Console.NewUpdateReleased", RequiredVersion]);
            }

            List<CCSPlayerController> players = GetCurrentPlayers();

            if (IsServerLoading || !CheckPlayers(players.Count)) return;

            players.ForEach(NotifyPlayerAboutUpdate);
            players.ForEach(controller => PlayersNotified[controller.Slot] = true);

            AddTimer(players.Count <= Config.MinPlayersInstantShutdown ? 1 : Config.ShutdownDelay,
                PrepareServerShutdown,
                Config.ShutdownOnMapChangeIfPendingUpdate ? TimerFlags.STOP_ON_MAPCHANGE : 0);

            RestartRequired = true;
        }

        private bool CheckPlayers(int players)
        {
            var slots = sv_visiblemaxplayers?.GetPrimitiveValue<int>() ?? -1;

            if (slots == -1) slots = Server.MaxPlayers;

            return (float)players / slots < Config.MinPlayerPercentageShutdownAllowed ||
                   Config.MinPlayersInstantShutdown >= players;
        }

        private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
        {
            if (!UpdateAvailable) return HookResult.Continue;

            CCSPlayerController? player = @event.Userid;

            if (player == null || player!.IsBot || player.TeamNum <= (byte)CsTeam.Spectator) return HookResult.Continue;
            if (PlayersNotified.TryGetValue(player.Slot, out bool notified) && notified) return HookResult.Continue;

            PlayersNotified[player.Slot] = true;

            Server.NextFrame(() => NotifyPlayerAboutUpdate(player));

            return HookResult.Continue;
        }

        private void NotifyPlayerAboutUpdate(CCSPlayerController player)
        {
            int remainingTime = Math.Max(1, Config.ShutdownDelay - (int)(Server.CurrentTime - UpdateFoundTime));

            string timeUnitLabel =
                remainingTime >= 60 ? "AutoUpdater.Chat.MinuteLabel" : "AutoUpdater.Chat.SecondLabel";
            
            string pluralSuffix = remainingTime > 120 || (remainingTime < 60 && remainingTime != 1)
                ? $"{Localizer["AutoUpdater.Chat.PluralSuffix"]}"
                : string.Empty;

            string timeToRestart =
                $"{(remainingTime >= 60 ? remainingTime / 60 : remainingTime)} {Localizer[timeUnitLabel]}{pluralSuffix}";

            player.PrintToChat(
                $" {Localizer["AutoUpdater.Chat.Prefix"]} {Localizer["AutoUpdater.Chat.NewUpdateReleased", RequiredVersion, timeToRestart]}");
        }

        private async Task<bool> IsUpdateAvailable()
        {
            string steamInfPatchVersion = await GetSteamInfPatchVersion();

            if (string.IsNullOrWhiteSpace(steamInfPatchVersion))
            {
                Logger.LogError(Localizer["AutoUpdater.Console.ErrorPatchVersionNull"]);
                return false;
            }

            using HttpClient httpClient = new HttpClient();
            var response = await httpClient.GetAsync(string.Format(SteamApiEndpoint, steamInfPatchVersion));

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning(Localizer["AutoUpdater.Console.WarningSteamRequestFailed", response.StatusCode]);
                return false;
            }

            var upToDateCheckResponse = await response.Content.ReadFromJsonAsync<UpToDateCheckResponse>();
            RequiredVersion = (int)upToDateCheckResponse?.Response?.RequiredVersion!;

            return upToDateCheckResponse.Response is { Success: true, UpToDate: false };
        }

        private async Task<string> GetSteamInfPatchVersion()
        {
            string steamInfPath = Path.Combine(Server.GameDirectory, "csgo", "steam.inf");

            if (!File.Exists(steamInfPath))
            {
                Logger.LogError(Localizer["AutoUpdater.Console.ErrorSteamInfNotFound", steamInfPath]);
                return string.Empty;
            }

            try
            {
                string steamInfContents = await File.ReadAllTextAsync(steamInfPath);
                Match match = PatchVersionRegex().Match(steamInfContents);

                if (match.Success) return match.Groups[1].Value;

                Logger.LogError(Localizer["AutoUpdater.Console.ErrorPatchVersionKeyNotFound", steamInfPath]);

                return string.Empty;
            }
            catch (Exception ex)
            {
                Logger.LogError(Localizer["AutoUpdater.Console.ErrorReadingSteamInf", ex.Message]);
            }

            return string.Empty;
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
                        Server.ExecuteCommand(
                            $"kickid {player.UserId} Due to the game update (Version: {RequiredVersion}), the server is now restarting.");
                        break;
                }
            }

            AddTimer(1, ShutdownServer);
        }
        
        private void ShutdownServer()
        {
            Logger.LogInformation(Localizer["AutoUpdater.Console.ServerShutdownInitiated", RequiredVersion]);
            Server.ExecuteCommand("quit");
        }

        private static List<CCSPlayerController> GetCurrentPlayers()
        {
            return Utilities.GetPlayers().Where(controller => controller is { IsValid: true, IsBot: false, IsHLTV: false }).ToList();
        }

        [GeneratedRegex(@"PatchVersion=(?<version>[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)", RegexOptions.ExplicitCapture, 1000)]
        private static partial Regex PatchVersionRegex();
    }
}    
