# Troubleshooting

Find the message you see, then follow the steps under it. If nothing here fits, see "Asking for help" at the end.

## Installing the server

**"No server data in ... pass --data"**
The data bundle is missing. Download `aion-server-data.tar.gz` from the Releases page, put it in the `data` folder and
pass it to the install (`--data data/aion-server-data.tar.gz`). It must be the bundle of the same release as the code.

**Is the server running?**
- Linux: `systemctl status aion-host` should say "active (running)". The log: `journalctl -u aion-host -e`.
- Windows: the window `start.ps1` opened shows the log. With `-Background` the log is
  `C:\AionServer\state\logs\host.log`.

**"Port 443 is in use by ..." (Windows) or the server stops right after starting**
Another program holds a port the game needs (443, 3700, 8087, 8091, 8780, 13328). Port 443 is usually a web server,
VMware Workstation or an old Skype. Close it or stop its service, then start the server again.

**I can't find a file to start the server**
There is no single exe. On Linux the install starts the server as a service. On Windows run
`deploy\windows\start.ps1` after the install. See the README's Quick start.

## The account page

**"The account service is not configured yet."**
The server was started without its settings, so it has no database. This happens when the host is started by hand
(`dotnet run`) instead of through the install. Start it the normal way: the Linux service, or `start.ps1` on Windows.

**"Sign-in failed. Check your username and password."**
The account does not exist or the password is wrong. A new server has no accounts: choose "Create account" first.

**"Registration is closed. Ask an admin for an account."** or there is no "Create account" choice
Registration is closed by default. The first account of a new server can still register; after that, staff make
accounts on the dashboard. To let everyone register, run the install again with `--open-registration`
(Windows: `-OpenRegistration`).

**The browser says the connection is not private**
With a self-signed certificate that is expected: the server made the certificate itself. Choose "Advanced" and
continue. The launcher accepts it through the `certificatePins` value in `launcher.json`.

**Where is the staff dashboard? How do I give items or change a level?**
The dashboard's address was printed at the end of the install (Linux: `sudo grep StaffPath /etc/aion/aion.env`).
Your account needs a staff rank first. Both are in the README under "After the install", with the in-game `//help`
commands.

## The launcher and the game

**NCGuard closes the game with error 0xE0000013**
1. The launcher was started as administrator. This is the usual cause. Close it and start it normally: no
   right-click "Run as administrator", and no "Run this program as an administrator" in the exe's compatibility
   settings.
2. The game copy was not downloaded by the launcher (copied in from somewhere else, or another build). Let the
   launcher download the game itself.

**"Check your network connection. You will be taken to the home screen."**
The game cannot reach the server.
1. Is the server running? (See above.) After a restart of the server PC it has to be started again on Windows.
2. Is `serverAddress` in `launcher.json` the address players reach the server by?
3. Are the ports open? The game needs TCP 443, 3700, 8087, 8091, 8780 and 13328 on the server: the server's firewall,
   your hosting provider's firewall, and port forwarding in the router for a server at home.

**The launcher says the game needs an update, or rejects the game version**
The server supports exactly one client build. Use the launcher's own download or its "Update the game" button. Do not
copy the normal Steam install over the launcher's copy.

**I cannot download the game: it is not on Steam in my country, or I use the Purple launcher**
A2Core only works with the Steam client, downloaded through a Steam account that owns the game. The Purple client
and the Korean and Taiwanese clients are different builds and are not supported.

**Windows SmartScreen warns about the launcher**
The exe is not code-signed. Choose "More info", then "Run anyway", if you built it yourself or got it from your own
server's account page.

**The Steam QR code does not work**
Scan it with the Steam mobile app of the account that owns the game (Steam Guard, "Scan a QR code"). The launcher
never sees your Steam password.

## In the game

**I created an Asmodian and got an Elyos (or the other way round)**
Update the server: this was fixed. An account's characters must all be of one race: delete the characters of the
other race first.

**A quest does not continue**
Many quest steps work, some do not yet. Report it with the quest's name, the step text shown in the journal and what
you did (see below). Staff can skip the step in the meantime: `//QuestCompleteStep` in the chat.

## Asking for help

Say what you did, what you expected and what happened, and add:

- which install you use (Linux or Windows, one PC or a server) and the release or the date of your checkout;
- the exact message, as text;
- the last lines of the server log around the time it happened.

Before posting a log or a screenshot, remove:

- the value after `-lp:` in a game command line (it is your launch token);
- passwords, and anything from `aion.env` (it holds the database passwords);
- Steam sign-in codes and QR codes.
