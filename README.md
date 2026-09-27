# EchoRelay: Echo VR Classic Lobbies

Play the 2019 **Summer** and 2018 **Halloween** lobby builds of Echo VR on community servers.

## Play

1. Download `EchoClassicLobbies.exe` from [Releases](https://github.com/heisthecat31/EchoRelay/releases).
2. Run it, pick **Summer 2019** or **Halloween 2018**, enter a name and password, and press **Install**.
3. Start the Oculus app, connect your headset and press **Play**.

## Run your own server

1. Download the EchoRelay release zip and run `EchoRelay.App.exe` **as administrator**. It serves both builds at once.
2. In your game folder, point every `*_host` in `_local\config.json` at your PC (`ws://YOUR-IP:777/...`, plus `"serverdb_host": "ws://YOUR-IP:777/serverdb"`).
3. Run `Start_EchoRelay_Server.bat` in the game folder once per game server (headless, no headset needed). To host both builds, do this in each build's folder.
4. For players over the internet, forward TCP `777` and UDP `6792` and up.

Full documentation: [DOCUMENTATION.md](DOCUMENTATION.md)
