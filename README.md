# CS2-IP-Suite

A comprehensive IP-based ban / unban / WHOIS lookup tool for **CounterStrike 2**
servers running [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp).

* **Author:** BONE
* **Version:** 1.0.0
* **License:** Use freely.

---

## Features

* In-memory tracking of every connected player (SteamID, name, IP).
* `ipban` and `ipunban` commands — by **name** (partial match), **IP**, or **SteamID64**.
* `ipsearch` command — WHOIS-style lookup (ISP / city / region / country) plus
  alt-account detection across the local DB.
* **All admin command input *and* output is hidden from public chat.** Players
  on the server never see the command, the response, or the yes/no confirmation.
* Yes / no (or `!yes` / `!no`) confirmation step for every name- and IP-based
  ban and for every unban.
* 30-second background sweep that lifts bans whose duration is up.
* SQLite database, auto-created in
  `addons/counterstrikesharp/configs/plugins/CS2-IP-Suite/ipsuite.db`.
* Connection-time enforcement: banned IPs are kicked the moment they join.
* Full hot-reload safety — the plugin restores in-memory state from the
  current player list and the DB.
* Colored, easy-to-read chat output.

---

## Building

Requires the **.NET 8 SDK**.

```bash
cd CS2-IP-Suite
dotnet restore
dotnet publish -c Release -o ./publish
```

The build will produce `CS2-IP-Suite.dll` along with `Microsoft.Data.Sqlite.dll`
and the native `e_sqlite3` runtime in `./publish`.

---

## Installation

1. Make sure CounterStrikeSharp is installed on your server.
2. Create the plugin folder:

   ```
   game/csgo/addons/counterstrikesharp/plugins/CS2-IP-Suite/
   ```

3. Copy **all** files from `./publish` into that folder. In particular you
   need:

   * `CS2-IP-Suite.dll`
   * `Microsoft.Data.Sqlite.dll`
   * `SQLitePCLRaw.*.dll` files
   * `runtimes/` folder (contains the native SQLite binaries)

4. Grant the `@css/ban` permission flag to whichever admin groups should be
   able to use the commands. This is configured in
   `addons/counterstrikesharp/configs/admins.json`.

5. Start (or `css_plugins reload CS2-IP-Suite`) the server.

The DB file appears automatically at:

```
addons/counterstrikesharp/configs/plugins/CS2-IP-Suite/ipsuite.db
```

---

## Commands

Every command works in two ways:

* **Chat:** type with or without a leading `!` — both work
  (`ipban …` or `!ipban …`). The chat line and every plugin response are
  invisible to the rest of the server.
* **Server console / rcon:** type without the `!`.

Anyone without the `@css/ban` flag is silently ignored.

### Banning

```
ipban name <username> <minutes>
ipban ip   <xxx.xxx.xxx.xxx> <minutes>
ipban id   <SteamID64> <minutes>
```

* **`name`** matches case-insensitively against the names of currently
  connected players. Partial substrings work. If more than one player matches,
  you'll get a list and be asked to be more specific.
* **`ip`** takes a literal IPv4 address.
* **`id`** takes a SteamID64. Works for **offline** players — the plugin
  looks up their last-known IP from the local DB.
* **Duration is in minutes** and must be a positive integer.

`ipban name` and `ipban ip` ask for **yes / no** confirmation before applying.
`ipban id` applies immediately (the spec calls for confirmation only on the
name and IP forms).

### Unbanning

```
ipunban name <username>
ipunban ip   <xxx.xxx.xxx.xxx>
ipunban id   <SteamID64>
```

All three forms ask for yes / no confirmation. `name` and `id` look up every
IP that user has ever connected from and lift any active ban on those IPs at
once — handy for clearing alt-account bans in one go.

### Confirmation

After being prompted, reply with one of:

```
yes    !yes    no    !no
```

Confirmations time out after 60 seconds.

### Searching / WHOIS

```
ipsearch <xxx.xxx.xxx.xxx>
ipsearch <username>
ipsearch <SteamID64>
```

Returns:

* IP address (resolved from the name / SteamID if needed)
* ISP name
* City, region, country (via the free `ip-api.com` service)
* Every account in the local DB that has ever connected from that IP — flagged
  as **possible alts** when there is more than one.

---

## Notes

* The 30-second timer scans `bans.expires_at` and deactivates any ban whose
  time is up; affected players will simply be allowed back in on their next
  connection.
* The plugin always **echoes the hidden input** back to the admin who typed it
  (in grey, prefixed with `[hidden]`) so they can see what they sent — even
  though the public chat shows nothing.
* On hot reload, the plugin re-reads the active-bans table into memory and
  re-populates the connected-player snapshot from
  `Utilities.GetPlayers()`.
* The database file persists between reloads and restarts; deleting it will
  wipe both the connection history and every ban.

---

## File map

```
CS2-IP-Suite/
├── CS2-IP-Suite.csproj    .NET 8 project file
├── CS2IPSuite.cs          Main plugin: lifecycle, listeners, command logic
├── Models.cs              Data classes
├── DatabaseManager.cs     SQLite layer + active-bans cache
├── IPLookupService.cs     ip-api.com WHOIS client
├── ChatHelper.cs          Colored chat formatting
└── README.md              You are here
```
