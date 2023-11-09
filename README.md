# CS2-AutoUpdater
 The Auto Updater plugin automates the update process for your Counter-Strike 2 (CS2) server.
 > [!IMPORTANT]  
 > It is required for the server to have hibernation disabled: `sv_hibernate_when_empty` set to `false`.

# Features
 - [x] Automatically checks the current game version of Counter-Strike 2 by querying Steam's API.
 - [x] Notifies players about the upcoming server restart.
 - [ ] Translations. (Waiting for CounterStrikeSharp support)

# Installation

 ### Requirements

  - [Metamod:Source](https://www.sourcemm.net/downloads.php/?branch=master) (Dev Build)
  - [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) (Commit [12a654f](https://github.com/roflmuffin/CounterStrikeSharp/actions/runs/6782595525) or higher)

  Download the latest release of CS2-AutoUpdater from the [GitHub Release Page](https://github.com/dran1x/CS2-AutoUpdater/releases).

  Extract the contents of the archive into your `counterstrikesharp/plugins` folder.

 ### Build Instructions

  If you want to build CS2-AutoUpdater from the source, follow these instructions:

  ```bash
  git clone https://github.com/dran1x/CS2-AutoUpdater && cd CS2-AutoUpdater

  # Make sure the CounterStrikeSharp dependacy has a valid path.
  dotnet publish -f net7.0 -c Release 
  ```

# Confiuration
 ```json
 {
   "UpdateCheckInterval": 300,
   "RestartDelay": 120,
   "ShutdownDelay": 2,
   "MinimumPlayersBeforeInstantRestart": 1,
   "ChatTag": "\u0004[AutoUpdater]\u0001"
 }
 ```
