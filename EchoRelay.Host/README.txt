EchoRelay game server host
==========================

Runs the game servers players request from the EchoClassicLobbies installer, for an EchoRelay server on another PC,
in a region you name (e.g. US-West). Windows 10/11, nothing else to install.

1. Install the Echo VR versions you want to host (Summer 2019, Halloween 2018, Christmas 2018, Christmas 2017,
   Halloween 2017), e.g.
   with EchoClassicLobbies, pointed at the EchoRelay server (its config.json).
2. Extract this zip into a folder and run EchoRelay-Host.bat.
3. Answer the questions:
   - where each version is installed (type skip for ones you don't host)
   - game server options: tick rate, how many servers per player and in total, Christmas / Halloween 2017 software rendering,
     update checks, this PC's name
   - this PC's region name
   It takes the EchoRelay server's address (and API key) from the installs' _local\config.json, and installs the latest
   EchoRelay DLLs from GitHub into every version.
4. Leave the window open. When a player requests a game server in your region, it starts one here. Game servers that
   stay empty for 5 minutes are closed.

Next time, run the .bat again: it shows the saved setup and asks whether to use it (answer n to change anything).

Every option is saved in EchoRelayHost.config.json next to EchoRelay.Host.exe, and can be edited there too:

  relay                          The EchoRelay server, ws://ADDRESS:PORT
  api_key                        Its ServerDB API key, if it uses one
  region                         The region players pick in the installer (up to 24 characters)
  installs                       Each version's install folder: summer, halloween, winter (Christmas 2018), christmas (2017),
                                 halloween2017
  name                           This PC's name in the EchoRelay server's log
  tick_rate                      Frames a second each game server runs at (fixed timestep). Default 120; lower uses
                                 less CPU (and GPU); 0 = uncapped, a whole CPU core per server
  per_player                     Game servers one player can have running at once, per version (default 2)
  max                            Game servers this PC runs at once, in total (default 6)
  christmas_software_rendering   true: Christmas and Halloween 2017 servers render in software, no GPU use (default false: the GPU,
                                 or software if there is none)
  update_on_start                Check for and install the latest EchoRelay DLLs at every start (default true)
  game_files_repository          Where the DLL updates come from (default heisthecat31/EchoRelay)
  extra_args                     Extra game server command line arguments per version, e.g. { "summer": "-noconsole" }

Advanced: EchoRelay.Host.exe also runs without the setup, from command line options (EchoRelay.Host.exe --help).
