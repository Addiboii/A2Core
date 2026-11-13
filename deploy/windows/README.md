# Running the server on Windows

Windows 10/11 or Windows Server, 64-bit. The same server as on Linux (`deploy/linux`), with PowerShell scripts instead
of systemd. Two ways to use it:

- **One PC** (`-Local`): the server and the game on the same computer, for trying things out. Nothing is reachable from
  other computers.
- **A server for other players** (`-PublicAddress`): players connect to this PC's address.

## 1. Install what the server needs

In a PowerShell window:

```powershell
winget install Microsoft.DotNet.SDK.10
winget install Python.Python.3.13
winget install OpenJS.NodeJS.LTS
winget install Git.Git
```

Close the window and open a new one afterwards, so the new programs are found. About 3 GB of free disk space is
needed for the server (the game client is separate: about 90 GB on the PC that plays).

## 2. Get the server and its data

```powershell
git clone <this repository's address> server
cd server
```

Download the server data bundle `aion-server-data.tar.gz` from the Releases page and put it in the `data` folder. It
must be the bundle of the same release as the code.

## 3. Install

One PC:

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\install.ps1 -Local -Data data\aion-server-data.tar.gz
```

A server for other players (use the IPv4 address they reach this PC by; on a home network that is the PC's LAN
address, from the internet your public address with the ports forwarded in your router):

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\install.ps1 -PublicAddress 203.0.113.5 -Data data\aion-server-data.tar.gz
```

The first run takes a few minutes: it downloads PostgreSQL (about 340 MB, checked against its SHA-256; or pass
`-PostgresZip <file>` if you already have `postgresql-18.6-1-windows-x64-binaries.zip`), builds the server, creates
the database and makes a certificate for the account site. Everything goes into `C:\AionServer` (`-Root <folder>` for
another place). At the end it prints the account page address, the staff dashboard address and the three lines the
launcher needs.

For other players to connect, Windows Firewall must let them in. Run the install from an administrator PowerShell
window to have the rule added, or run the one command it prints. `-Local` needs no firewall rule.

Add `-OpenRegistration` if anyone should be able to make an account on the account page. Without it, only the first
account can register there; you make later ones on the staff dashboard.

## 4. Start and stop

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\start.ps1
```

Leave that window open: it shows the server's log. Ctrl+C stops the server. `start.ps1 -Background` starts it hidden
instead (log in `C:\AionServer\state\logs\host.log`), and `stop.ps1` stops the server and the database.

To start the server when Windows starts, add a scheduled task (administrator window, once):

```powershell
schtasks /Create /TN "AION2 server" /SC ONSTART /RU "$env:USERNAME" /RP /TR "powershell -ExecutionPolicy Bypass -File C:\path\to\server\deploy\windows\start.ps1 -Background"
```

## 5. Your account and the launcher

1. Open the account page the install printed (`https://<address>:8780`). The browser warns about the certificate
   because the server made it itself: choose to continue. Register your account.
2. Make it staff (the install prints this command with your paths):

   ```powershell
   $env:AION_PG_HOME='C:\AionServer\postgres'; python C:\AionServer\src\tools\postgres_dev.py role <username> admin
   ```

   Then sign in on the staff dashboard address the install printed.
3. Build the launcher (see the main README): put the three values the install printed (`accountUrl`, `serverAddress`,
   `certificatePins`) in `launcher\launcher.json` first. Copy the built launcher and its `.version` file to
   `C:\AionServer\state\downloads` so players can download it from the account page.

Do not run the launcher as administrator: the game must not run elevated.

## Updating

```powershell
git pull
powershell -ExecutionPolicy Bypass -File deploy\windows\install.ps1
```

With no options it repeats your first install's choices: it stops the server, backs up the database to
`C:\AionServer\state\backups`, rebuilds, applies new migrations and keeps your settings, the staff dashboard address
and the certificate. Add `-Data <file>` when a release comes with a new data bundle. Start the server again afterwards.

## Where things are

| Folder | Contents |
|---|---|
| `C:\AionServer\host` | The built server. |
| `C:\AionServer\data` | The unpacked data bundle. |
| `C:\AionServer\postgres` | PostgreSQL and the database (listens on 127.0.0.1:15432 only; an older install on 55432 moves at its next start). Back this up. |
| `C:\AionServer\config` | `aion.env` (written by every install), `local.env` (your own settings, such as rates; never overwritten), the certificate in `tls`. |
| `C:\AionServer\state` | Backups, logs, the launcher downloads. |

`config` and `postgres` hold passwords: only your Windows account, Administrators and SYSTEM can read them.

## If something goes wrong

- **"Port 443 is in use by ..."** when starting: another program holds a port the game needs. Port 443 is usually a web
  server (IIS, Apache), VMware Workstation or an old Skype. Close it or stop its service.
- **"Install these first ..."** although you installed them: open a new PowerShell window.
- **"running scripts is disabled on this system"**: start the scripts with `powershell -ExecutionPolicy Bypass -File ...`
  as shown above.
- **The database did not start**: see `C:\AionServer\postgres\server.log`. If the log speaks of a missing
  `VCRUNTIME140.dll`, install the Microsoft Visual C++ Redistributable (x64).
- **`start.ps1 -Background` seems to hang** when its output is piped into another command: run it without a pipe.
