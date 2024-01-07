using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2AutoUpdater;

[MinimumApiVersion(14)]
public partial class CS2AutoUpdater : BasePlugin
{
    public override string ModuleName => "AutoUpdater";
    public override string ModuleAuthor => "DRANIX";
    public override string ModuleDescription => "Auto Updater for Counter-Strike 2.";
    public override string ModuleVersion => "1.0.4";
    private const string SteamApiEndpoint = "http://api.steampowered.com/ISteamApps/UpToDateCheck/v0001/?appid=730&version={steamInfPatchVersion}";
    private static CounterStrikeSharp.API.Modules.Timers.Timer? _updateCheck;
    private static readonly bool[] PlayersNotified = new bool[65];
    private static float _updateFoundTime;
    private static bool _updateAvailable;
    private static int _requiredVersion;
    private static bool _isMapLoading;
    
    public override void Load(bool hotReload)
    {
        Configuration.LoadConfig(ModuleDirectory);
        
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        
        RegisterListener<Listeners.OnGameServerSteamAPIActivated>(OnGameServerSteamAPIActivated);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnClientConnected>(playerSlot => { PlayersNotified[playerSlot + 1] = false; });
        RegisterListener<Listeners.OnClientDisconnectPost>(playerSlot => { PlayersNotified[playerSlot + 1] = false; });
        
        _updateCheck = AddTimer(Configuration.Config.UpdateCheckInterval, CheckServerForUpdate, TimerFlags.REPEAT);
        
        var svHibernateWhenEmpty = ConVar.Find("sv_hibernate_when_empty");
        
        if (svHibernateWhenEmpty != null && svHibernateWhenEmpty.GetPrimitiveValue<bool>())
        {
            ConsoleLog("'sv_hibernate_when_empty' is enabled. This plugin might not work as expected.", ConsoleColor.Yellow);
        }
    }
        
    [GameEventHandler]
    private static HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (!_updateAvailable)
        {
            return HookResult.Continue;
        }
        
        var player = @event.Userid;

        if (!player.IsValid || player.IsBot || (CsTeam)player.TeamNum == CsTeam.None)
        {
            return HookResult.Continue;
        }

        if (PlayersNotified[player.Index])
        {
            return HookResult.Continue;
        }
        
        NotifyPlayer(player);
        
        return HookResult.Continue;
    }

    private async void CheckServerForUpdate()
    {
        try
        {
            if (!await IsUpdateAvailable())
            {
                return;
            }
        } 
        catch (Exception ex)
        {
            ConsoleLog($"Failed to request Steam API for updates: {ex.Message}");
            return;
        }
        
        _updateCheck?.Kill();
        
        _updateAvailable = true;
        
        _updateFoundTime = Server.CurrentTime;
        
        var players = GetCurrentPlayers();
        
        if (players.Count <= Configuration.Config.MinimumPlayersBeforeInstantRestart)
        {
            PrepareServerShutdown();
            return;
        }

        if (!_isMapLoading)
        {
            foreach (var player in players)
            {
                NotifyPlayer(player);
            }
        }

        AddTimer(Configuration.Config.RestartDelay, PrepareServerShutdown);
    }

    private static void NotifyPlayer(CCSPlayerController player)
    {
        var remainingTime = Configuration.Config.RestartDelay - (int)(Server.CurrentTime - _updateFoundTime);

        if (remainingTime < 0)
        {
            remainingTime = 1;
        }
        
        var timeToRestart = remainingTime >= 60 ? $"{remainingTime / 60} minute{(remainingTime >= 120 ? "s" : "")}" : $"{remainingTime} second{(remainingTime > 1 ? "s" : "")}";
        player.PrintToChat($" {Configuration.Config.ChatTag} New Counter-Strike 2 update released (Version: {_requiredVersion}) the server will restart in {timeToRestart}");
        
        PlayersNotified[player.Index] = true;
    }

    private void OnGameServerSteamAPIActivated()
    {
        ConsoleLog("Steam API activated. Server will be checked for updates.");
    }
        
    private static void OnMapStart(string mapName)
    {
        _isMapLoading = false;
    }
        
    private static void OnMapEnd()
    {
        _isMapLoading = true;
    }

    private async Task<bool> IsUpdateAvailable()
    {
        var steamInfPatchVersion = GetSteamInfPatchVersion();
        
        if (string.IsNullOrEmpty(steamInfPatchVersion))
        {
            ConsoleLog("Unable to get the current patch version of Counter-Strike 2. The server will not be checked for updates.", ConsoleColor.Red);
            Server.ExecuteCommand($"css_plugins stop {ModuleName}");
            return false;
        }
        
        try
        {
            using var httpClient = new HttpClient();
            var response = await httpClient.GetAsync(SteamApiEndpoint.Replace("{steamInfPatchVersion}", steamInfPatchVersion));
            
            if (response.IsSuccessStatusCode)
            {
                var upToDateObject = JsonSerializer.Deserialize<UpToDateCheckResponse>(await response.Content.ReadAsStringAsync())!;
                
                if (upToDateObject.Response is { Success: true, UpToDate: false })
                {
                    _requiredVersion = upToDateObject.Response.RequiredVersion!;
                    ConsoleLog($"New Counter-Strike 2 update released (Version: {_requiredVersion}, Current: {steamInfPatchVersion})");
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
        var players = GetCurrentPlayers();
        
        foreach (var player in players)
        {
            switch (player.Connected)
            {
                case PlayerConnectedState.PlayerConnected:
                case PlayerConnectedState.PlayerConnecting:
                case PlayerConnectedState.PlayerReconnecting:
                    Server.ExecuteCommand($"kickid {player.UserId} Due to the game update (Version: {_requiredVersion}), the server is now restarting.");
                    break;
            }
        }
        
        AddTimer(Configuration.Config.ShutdownDelay, ShutdownServer);
    }

    private string GetSteamInfPatchVersion()
    {
        var steamInfPath = Path.Combine(Server.GameDirectory, "csgo", "steam.inf");
        
        if (File.Exists(steamInfPath))
        {
            try
            {
                var match = PatchVersion().Match(File.ReadAllText(steamInfPath));
                
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
        var path = Path.Combine(ModuleDirectory, "logs.txt");
        var log = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{ModuleName}] {message}";
        
        Console.ForegroundColor = color;
        Console.WriteLine(log);
        Console.ResetColor();
        
        try
        {
            using var file = new StreamWriter(path, true);
            file.WriteLine(log);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[{ModuleName}]Error writing to log file: {ex.Message}");
            Console.ResetColor();
        }
    }
        
    private void ShutdownServer()
    {
        ConsoleLog($"Restarting the server due to the new game update. (Version: {_requiredVersion})");
        Server.ExecuteCommand("quit");
    }

    [GeneratedRegex("PatchVersion=(.+)")]
    private static partial Regex PatchVersion();
}
