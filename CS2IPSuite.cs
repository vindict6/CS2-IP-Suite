using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

using Microsoft.Extensions.Logging;

using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace CS2IPSuite;

/// <summary>
/// CS2-IP-Suite — comprehensive IP-based ban/unban + WHOIS lookup tool.
/// All chat input AND output for admin commands is hidden from public chat.
/// </summary>
public sealed class CS2IPSuitePlugin : BasePlugin
{
    // ──────────────────────────────────────────────────────────────────
    //  Metadata
    // ──────────────────────────────────────────────────────────────────
    public override string ModuleName        => "CS2-IP-Suite";
    public override string ModuleVersion     => "1.0.0";
    public override string ModuleAuthor      => "BONE";
    public override string ModuleDescription => "IP ban/unban manager with WHOIS lookup and alt-account detection.";

    /// <summary>Permission flag required to use any command in this plugin.</summary>
    public const string AdminFlag = "@css/ban";

    // ──────────────────────────────────────────────────────────────────
    //  State
    // ──────────────────────────────────────────────────────────────────
    private DatabaseManager  _db       = null!;
    private IPLookupService  _lookup   = null!;

    /// <summary>SteamID64 -&gt; live PlayerInfo for everyone currently in the server.</summary>
    private readonly ConcurrentDictionary<ulong, PlayerInfo> _current = new();

    /// <summary>Admin SteamID64 -&gt; queued action awaiting yes/no.</summary>
    private readonly ConcurrentDictionary<ulong, PendingAction> _pending = new();

    private Timer? _expiryTimer;

    // ──────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ──────────────────────────────────────────────────────────────────
    public override void Load(bool hotReload)
    {
        // configs/plugins/CS2-IP-Suite/ipsuite.db
        var configDir = Path.Combine(
            ModuleDirectory, "..", "..", "configs", "plugins", "CS2-IP-Suite");
        Directory.CreateDirectory(configDir);

        var dbPath = Path.GetFullPath(Path.Combine(configDir, "ipsuite.db"));

        _db = new DatabaseManager(dbPath, Logger);
        _db.Initialize();

        _lookup = new IPLookupService(Logger);

        // Chat interception so we can hide commands and confirmations.
        AddCommandListener("say",      OnPlayerSay, HookMode.Pre);
        AddCommandListener("say_team", OnPlayerSay, HookMode.Pre);

        // Connect / disconnect tracking + IP-ban enforcement.
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);

        // Sweep expired bans every 30 s.
        _expiryTimer = AddTimer(30.0f, SweepExpiredBans, TimerFlags.REPEAT);

        // On hot reload: re-populate _current from whoever is on the server.
        if (hotReload)
        {
            foreach (var p in Utilities.GetPlayers())
            {
                if (p == null || !p.IsValid || p.IsBot || p.IsHLTV) continue;
                if (p.AuthorizedSteamID == null) continue;

                var ip = ExtractIp(p.IpAddress);
                if (string.IsNullOrEmpty(ip)) continue;

                var info = new PlayerInfo
                {
                    SteamId     = p.AuthorizedSteamID.SteamId64,
                    Name        = p.PlayerName ?? "Unknown",
                    IpAddress   = ip,
                    Slot        = p.Slot,
                    ConnectedAt = DateTime.UtcNow,
                };
                _current[info.SteamId] = info;
                _ = _db.RecordPlayerAsync(info);
            }
        }

        Logger.LogInformation(
            "[CS2-IP-Suite] v{ver} by {auth} loaded. DB: {db}. Hot reload: {hr}",
            ModuleVersion, ModuleAuthor, dbPath, hotReload);
    }

    public override void Unload(bool hotReload)
    {
        try { _expiryTimer?.Kill(); } catch { /* ignore */ }
        _expiryTimer = null;

        _current.Clear();
        _pending.Clear();

        try { _lookup?.Dispose(); } catch { /* ignore */ }
        try { _db?.Dispose();     } catch { /* ignore */ }

        Logger.LogInformation("[CS2-IP-Suite] Unloaded. (hot reload: {hr})", hotReload);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Player tracking + IP-ban enforcement
    // ──────────────────────────────────────────────────────────────────
    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var p = @event.Userid;
        if (p == null || !p.IsValid || p.IsBot || p.IsHLTV) return HookResult.Continue;

        var ip = ExtractIp(p.IpAddress);
        if (string.IsNullOrEmpty(ip)) return HookResult.Continue;

        var sid = p.AuthorizedSteamID?.SteamId64 ?? 0UL;

        // Always log the attempt (even if banned) so the audit trail is complete.
        if (sid != 0)
        {
            var snapshot = new PlayerInfo
            {
                SteamId     = sid,
                Name        = p.PlayerName ?? "Unknown",
                IpAddress   = ip,
                Slot        = p.Slot,
                ConnectedAt = DateTime.UtcNow,
            };
            _ = _db.RecordPlayerAsync(snapshot);

            // Enforce IP ban — kick, do not track them as "currently connected".
            if (_db.IsIpBanned(ip))
            {
                var userId = p.UserId ?? -1;
                Logger.LogInformation(
                    "[CS2-IP-Suite] Kicking banned IP on connect: {name} {sid} {ip}",
                    snapshot.Name, sid, ip);
                Server.NextFrame(() =>
                {
                    if (userId >= 0)
                        Server.ExecuteCommand(
                            $"kickid {userId} \"You are IP banned from this server.\"");
                });
                return HookResult.Continue;
            }

            _current[sid] = snapshot;
        }

        return HookResult.Continue;
    }

    private void OnClientDisconnect(int slot)
    {
        // Remove from in-memory list.
        var entry = _current.FirstOrDefault(kv => kv.Value.Slot == slot);
        if (entry.Key != 0) _current.TryRemove(entry.Key, out _);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Chat interception (hides every command and confirmation)
    // ──────────────────────────────────────────────────────────────────
    private HookResult OnPlayerSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        var raw = info.GetArg(1);
        if (string.IsNullOrWhiteSpace(raw)) return HookResult.Continue;

        var trimmed = raw.Trim();
        var noBang  = trimmed.StartsWith("!", StringComparison.Ordinal)
                          ? trimmed[1..].TrimStart()
                          : trimmed;
        var firstWord = noBang.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)
                              .FirstOrDefault()?.ToLowerInvariant() ?? "";

        var sid = player.AuthorizedSteamID?.SteamId64 ?? 0UL;

        // 1) Confirmation reply only counts when this admin has a pending action.
        if ((firstWord == "yes" || firstWord == "no") && sid != 0
            && _pending.ContainsKey(sid)
            && (noBang.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                noBang.Equals("no",  StringComparison.OrdinalIgnoreCase)))
        {
            // Echo back so admin sees what they typed (it's hidden from everyone else).
            player.PrintToChat(ChatHelper.Echo(trimmed));
            HandleConfirmation(player, noBang.ToLowerInvariant());
            return HookResult.Stop;
        }

        // 2) Plugin commands.
        if (firstWord == "ipban" || firstWord == "ipunban" || firstWord == "ipsearch")
        {
            // Always hide it; even non-admin probes should not leak to public chat.
            player.PrintToChat(ChatHelper.Echo(trimmed));

            if (!AdminManager.PlayerHasPermissions(player, AdminFlag))
            {
                player.PrintToChat(ChatHelper.Error("You do not have permission to use this command."));
                return HookResult.Stop;
            }

            DispatchPluginCommand(player, noBang);
            return HookResult.Stop;
        }

        return HookResult.Continue;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Console-side commands (server console, rcon, host console)
    //  Players in-game cannot reach these because chat is intercepted
    //  above, so these are mainly for the dedicated-server console.
    // ──────────────────────────────────────────────────────────────────
    [ConsoleCommand("ipban", "Ban a player by name, IP, or SteamID64 for N minutes.")]
    [CommandHelper(minArgs: 0, usage: "<name|ip|id> <value...> <minutes>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions(AdminFlag)]
    public void OnIpBanConsole(CCSPlayerController? player, CommandInfo info)
    {
        // If this came from chat (which we route through OnPlayerSay), ignore;
        // OnPlayerSay already handled it. Console-only path proceeds.
        DispatchPluginCommand(player, "ipban " + info.ArgString);
    }

    [ConsoleCommand("ipunban", "Lift an IP ban by name, IP, or SteamID64.")]
    [CommandHelper(minArgs: 0, usage: "<name|ip|id> <value...>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions(AdminFlag)]
    public void OnIpUnbanConsole(CCSPlayerController? player, CommandInfo info)
    {
        DispatchPluginCommand(player, "ipunban " + info.ArgString);
    }

    [ConsoleCommand("ipsearch", "Look up WHOIS info and alt accounts for an IP or name.")]
    [CommandHelper(minArgs: 0, usage: "<ip|name>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions(AdminFlag)]
    public void OnIpSearchConsole(CCSPlayerController? player, CommandInfo info)
    {
        DispatchPluginCommand(player, "ipsearch " + info.ArgString);
    }

    // yes / no confirmations also accepted as plain console commands.
    [ConsoleCommand("yes", "Confirm a queued IP-Suite action.")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions(AdminFlag)]
    public void OnYesConsole(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null) return;     // server console can't have a pending action
        var sid = player.AuthorizedSteamID?.SteamId64 ?? 0UL;
        if (sid == 0 || !_pending.ContainsKey(sid)) return;
        HandleConfirmation(player, "yes");
    }

    [ConsoleCommand("no", "Cancel a queued IP-Suite action.")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions(AdminFlag)]
    public void OnNoConsole(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null) return;
        var sid = player.AuthorizedSteamID?.SteamId64 ?? 0UL;
        if (sid == 0 || !_pending.ContainsKey(sid)) return;
        HandleConfirmation(player, "no");
    }

    // ──────────────────────────────────────────────────────────────────
    //  Command dispatch
    // ──────────────────────────────────────────────────────────────────
    private void DispatchPluginCommand(CCSPlayerController? admin, string fullText)
    {
        var args = SplitArgs(fullText);
        if (args.Length == 0) return;

        var verb = args[0].ToLowerInvariant();
        switch (verb)
        {
            case "ipban":     ProcessIpBan(admin, args);     break;
            case "ipunban":   ProcessIpUnban(admin, args);   break;
            case "ipsearch":  ProcessIpSearch(admin, args);  break;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  ipban
    // ──────────────────────────────────────────────────────────────────
    private void ProcessIpBan(CCSPlayerController? admin, string[] args)
    {
        // args[0]=ipban, args[1]=name|ip|id, args[2..n-2]=value, args[n-1]=minutes
        if (args.Length < 4)
        {
            SendUsage(admin, isBan: true);
            return;
        }

        var sub = args[1].ToLowerInvariant();
        if (sub != "name" && sub != "ip" && sub != "id")
        {
            SendUsage(admin, isBan: true);
            return;
        }

        // Last arg must be the duration.
        if (!int.TryParse(args[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            || minutes <= 0)
        {
            Send(admin, ChatHelper.Error("Duration must be a positive integer (minutes)."));
            SendUsage(admin, isBan: true);
            return;
        }

        var valueParts = args.Skip(2).Take(args.Length - 3).ToArray();
        if (valueParts.Length == 0)
        {
            SendUsage(admin, isBan: true);
            return;
        }
        var value = string.Join(' ', valueParts);

        switch (sub)
        {
            case "name": ProcessIpBanByName(admin, value, minutes); break;
            case "ip":   ProcessIpBanByIp(admin, value, minutes);   break;
            case "id":   ProcessIpBanBySteamId(admin, value, minutes); break;
        }
    }

    private void ProcessIpBanByName(CCSPlayerController? admin, string nameQuery, int minutes)
    {
        // Match against currently connected players first (case-insensitive substring).
        var matches = _current.Values
            .Where(p => p.Name.Contains(nameQuery, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            Send(admin, ChatHelper.Error($"No connected player matches '{nameQuery}'."));
            Send(admin, ChatHelper.Info("Tip: 'ipban id <SteamID64> <min>' works for offline players."));
            return;
        }
        if (matches.Count > 1)
        {
            Send(admin, ChatHelper.Warn($"Multiple players match '{nameQuery}'. Be more specific:"));
            foreach (var m in matches.Take(8))
                Send(admin, $"  {ChatColors.Default}- {ChatColors.LightBlue}{m.Name}" +
                            $"{ChatColors.Default} ({ChatColors.Yellow}{m.SteamId}{ChatColors.Default})" +
                            $" [{ChatColors.LightRed}{m.IpAddress}{ChatColors.Default}]");
            if (matches.Count > 8)
                Send(admin, ChatHelper.Info($"...and {matches.Count - 8} more."));
            Send(admin, ChatHelper.Info("Use the exact name, or 'ipban id <SteamID64> <min>'."));
            return;
        }

        var target = matches[0];
        QueueBanConfirmation(admin, new[] { target.IpAddress }, minutes,
            display: $"{ChatColors.LightBlue}{target.Name}{ChatColors.Default}" +
                     $" ({ChatColors.Yellow}{target.SteamId}{ChatColors.Default})" +
                     $" — IP {ChatColors.LightRed}{target.IpAddress}{ChatColors.Default}");
    }

    private void ProcessIpBanByIp(CCSPlayerController? admin, string ip, int minutes)
    {
        if (!IsValidIPv4(ip))
        {
            Send(admin, ChatHelper.Error($"'{ip}' is not a valid IPv4 address."));
            return;
        }
        QueueBanConfirmation(admin, new[] { ip }, minutes,
            display: $"IP {ChatColors.LightRed}{ip}{ChatColors.Default}");
    }

    private void ProcessIpBanBySteamId(CCSPlayerController? admin, string sidStr, int minutes)
    {
        if (!ulong.TryParse(sidStr, out var sid) || sid == 0)
        {
            Send(admin, ChatHelper.Error($"'{sidStr}' is not a valid SteamID64."));
            return;
        }

        // Prefer the live IP if they're online; else fall back to most-recent DB row.
        string? ip = null;
        string  who = sid.ToString();

        if (_current.TryGetValue(sid, out var live))
        {
            ip  = live.IpAddress;
            who = $"{live.Name} ({sid})";
        }
        else
        {
            var rows = _db.GetPlayersBySteamId(sid.ToString());
            if (rows.Count > 0)
            {
                ip  = rows[0].IpAddress;
                who = $"{rows[0].Name} ({sid})";
            }
        }

        if (ip == null)
        {
            Send(admin, ChatHelper.Error($"No record for SteamID {sid}. They have never connected."));
            return;
        }

        // Per spec: ipban id does not require yes/no confirmation. Apply directly.
        var ban = _db.AddBan(ip, minutes, AdminName(admin));
        KickIfOnline(ip);

        Send(admin, ChatHelper.Success($"Banned {who} via IP {ip}."));
        Send(admin, ChatHelper.Field("Duration", FormatDuration(minutes)));
        if (ban.ExpiresAt is { } exp)
            Send(admin, ChatHelper.Field("Expires (UTC)", exp.ToString("yyyy-MM-dd HH:mm:ss")));

        Logger.LogInformation(
            "[CS2-IP-Suite] {by} banned {who} ip={ip} for {min}m via id-path",
            AdminName(admin), who, ip, minutes);
    }

    // ──────────────────────────────────────────────────────────────────
    //  ipunban
    // ──────────────────────────────────────────────────────────────────
    private void ProcessIpUnban(CCSPlayerController? admin, string[] args)
    {
        if (args.Length < 3)
        {
            SendUsage(admin, isBan: false);
            return;
        }

        var sub = args[1].ToLowerInvariant();
        if (sub != "name" && sub != "ip" && sub != "id")
        {
            SendUsage(admin, isBan: false);
            return;
        }

        var value = string.Join(' ', args.Skip(2));

        switch (sub)
        {
            case "ip":   ProcessIpUnbanByIp(admin, value);    break;
            case "id":   ProcessIpUnbanBySteamId(admin, value); break;
            case "name": ProcessIpUnbanByName(admin, value);   break;
        }
    }

    private void ProcessIpUnbanByIp(CCSPlayerController? admin, string ip)
    {
        if (!IsValidIPv4(ip))
        {
            Send(admin, ChatHelper.Error($"'{ip}' is not a valid IPv4 address."));
            return;
        }
        if (!_db.IsIpBanned(ip))
        {
            Send(admin, ChatHelper.Warn($"IP {ip} is not currently banned."));
            return;
        }

        QueueUnbanConfirmation(admin, new[] { ip },
            display: $"IP {ChatColors.LightRed}{ip}{ChatColors.Default}");
    }

    private void ProcessIpUnbanBySteamId(CCSPlayerController? admin, string sidStr)
    {
        if (!ulong.TryParse(sidStr, out var sid) || sid == 0)
        {
            Send(admin, ChatHelper.Error($"'{sidStr}' is not a valid SteamID64."));
            return;
        }

        var rows = _db.GetPlayersBySteamId(sid.ToString());
        if (rows.Count == 0)
        {
            Send(admin, ChatHelper.Error($"No record for SteamID {sid}."));
            return;
        }

        var bannedIps = rows.Select(r => r.IpAddress).Distinct()
                            .Where(_db.IsIpBanned).ToArray();
        if (bannedIps.Length == 0)
        {
            Send(admin, ChatHelper.Warn(
                $"None of the {rows.Count} known IPs for SteamID {sid} are currently banned."));
            return;
        }

        var name = rows[0].Name;
        var listText = string.Join(", ",
            bannedIps.Select(ip => $"{ChatColors.LightRed}{ip}{ChatColors.Default}"));
        QueueUnbanConfirmation(admin, bannedIps,
            display: $"{ChatColors.LightBlue}{name}{ChatColors.Default}" +
                     $" ({ChatColors.Yellow}{sid}{ChatColors.Default}) — {listText}");
    }

    private void ProcessIpUnbanByName(CCSPlayerController? admin, string nameQuery)
    {
        var rows = _db.GetPlayersByNameLike(nameQuery);
        if (rows.Count == 0)
        {
            Send(admin, ChatHelper.Error($"No DB record matches '{nameQuery}'."));
            return;
        }

        // Group by SteamID — we want one entry per distinct user.
        var byUser = rows.GroupBy(r => r.SteamId).ToList();

        if (byUser.Count > 1)
        {
            Send(admin, ChatHelper.Warn($"Multiple users match '{nameQuery}'. Be more specific:"));
            foreach (var grp in byUser.Take(8))
            {
                var top = grp.OrderByDescending(g => g.LastSeen).First();
                Send(admin,
                    $"  {ChatColors.Default}- {ChatColors.LightBlue}{top.Name}" +
                    $"{ChatColors.Default} ({ChatColors.Yellow}{top.SteamId}{ChatColors.Default})");
            }
            if (byUser.Count > 8)
                Send(admin, ChatHelper.Info($"...and {byUser.Count - 8} more."));
            Send(admin, ChatHelper.Info("Use 'ipunban id <SteamID64>' to disambiguate."));
            return;
        }

        // Single user matched — collect all banned IPs across all their records.
        var user = byUser[0];
        var bannedIps = user.Select(r => r.IpAddress).Distinct()
                            .Where(_db.IsIpBanned).ToArray();

        if (bannedIps.Length == 0)
        {
            Send(admin, ChatHelper.Warn(
                $"No active bans on any IPs associated with '{user.First().Name}'."));
            return;
        }

        var name = user.OrderByDescending(r => r.LastSeen).First().Name;
        var listText = string.Join(", ",
            bannedIps.Select(ip => $"{ChatColors.LightRed}{ip}{ChatColors.Default}"));
        QueueUnbanConfirmation(admin, bannedIps,
            display: $"{ChatColors.LightBlue}{name}{ChatColors.Default}" +
                     $" ({ChatColors.Yellow}{user.Key}{ChatColors.Default}) — {listText}");
    }

    // ──────────────────────────────────────────────────────────────────
    //  Confirmation queue
    // ──────────────────────────────────────────────────────────────────
    private void QueueBanConfirmation(
        CCSPlayerController? admin, IEnumerable<string> ips, int minutes, string display)
    {
        if (admin == null) return;       // server console: should never reach here
        var sid = admin.AuthorizedSteamID?.SteamId64 ?? 0UL;
        if (sid == 0)
        {
            Send(admin, ChatHelper.Error("Could not resolve your SteamID; cannot queue confirmation."));
            return;
        }

        _pending[sid] = new PendingAction
        {
            Kind            = PendingActionKind.Ban,
            IpTargets       = ips.Distinct().ToList(),
            DurationMinutes = minutes,
            DisplayInfo     = display,
        };

        Send(admin, ChatHelper.Header("Confirm BAN"));
        Send(admin, ChatHelper.Field("Target",   display));
        Send(admin, ChatHelper.Field("Duration", FormatDuration(minutes)));
        Send(admin, ChatHelper.Info(
            $"Type {ChatColors.LightGreen}yes{ChatColors.Default} (or " +
            $"{ChatColors.LightRed}no{ChatColors.Default}) within 60s. " +
            "(!yes / !no also accepted.)"));
    }

    private void QueueUnbanConfirmation(
        CCSPlayerController? admin, IEnumerable<string> ips, string display)
    {
        if (admin == null) return;
        var sid = admin.AuthorizedSteamID?.SteamId64 ?? 0UL;
        if (sid == 0)
        {
            Send(admin, ChatHelper.Error("Could not resolve your SteamID; cannot queue confirmation."));
            return;
        }

        _pending[sid] = new PendingAction
        {
            Kind        = PendingActionKind.Unban,
            IpTargets   = ips.Distinct().ToList(),
            DisplayInfo = display,
        };

        Send(admin, ChatHelper.Header("Confirm UNBAN"));
        Send(admin, ChatHelper.Field("Target", display));
        Send(admin, ChatHelper.Info(
            $"Type {ChatColors.LightGreen}yes{ChatColors.Default} (or " +
            $"{ChatColors.LightRed}no{ChatColors.Default}) within 60s. " +
            "(!yes / !no also accepted.)"));
    }

    private void HandleConfirmation(CCSPlayerController admin, string answer)
    {
        var sid = admin.AuthorizedSteamID?.SteamId64 ?? 0UL;
        if (sid == 0 || !_pending.TryRemove(sid, out var pending)) return;

        if (pending.IsExpired)
        {
            Send(admin, ChatHelper.Warn("That confirmation expired. Re-issue the command."));
            return;
        }

        if (answer == "no")
        {
            Send(admin, ChatHelper.Info("Action cancelled."));
            return;
        }

        // answer == "yes"
        var who = AdminName(admin);

        if (pending.Kind == PendingActionKind.Ban)
        {
            foreach (var ip in pending.IpTargets)
            {
                var ban = _db.AddBan(ip, pending.DurationMinutes, who);
                KickIfOnline(ip);
                Logger.LogInformation(
                    "[CS2-IP-Suite] {by} banned ip={ip} for {min}m (expires {exp})",
                    who, ip, pending.DurationMinutes,
                    ban.ExpiresAt?.ToString("u") ?? "never");
            }
            Send(admin, ChatHelper.Success($"Ban applied → {pending.DisplayInfo}"));
            Send(admin, ChatHelper.Field("Duration", FormatDuration(pending.DurationMinutes)));
        }
        else
        {
            int n = 0;
            foreach (var ip in pending.IpTargets)
                if (_db.RemoveBan(ip)) n++;
            Logger.LogInformation(
                "[CS2-IP-Suite] {by} unbanned {n} IP(s): {ips}",
                who, n, string.Join(",", pending.IpTargets));
            Send(admin, ChatHelper.Success($"Unbanned {n} IP(s) → {pending.DisplayInfo}"));
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  ipsearch  (WHOIS + alt accounts)
    // ──────────────────────────────────────────────────────────────────
    private void ProcessIpSearch(CCSPlayerController? admin, string[] args)
    {
        if (args.Length < 2)
        {
            Send(admin, ChatHelper.Error("Usage: ipsearch <ip>  |  ipsearch <name>"));
            return;
        }

        var query = string.Join(' ', args.Skip(1));
        if (IsValidIPv4(query))                 RunIpSearchByIp(admin, query);
        else if (ulong.TryParse(query, out _))  RunIpSearchBySteamId(admin, query);
        else                                    RunIpSearchByName(admin, query);
    }

    private void RunIpSearchByIp(CCSPlayerController? admin, string ip)
    {
        Send(admin, ChatHelper.Header($"IP Search: {ip}"));

        var dbRows = _db.GetPlayersByIp(ip);
        var capturedAdmin = admin;       // reference is safe to capture; we re-validate later.

        // Async lookup; marshal back to the game thread for chat output.
        Task.Run(async () =>
        {
            var info = await _lookup.LookupAsync(ip).ConfigureAwait(false);
            Server.NextFrame(() => RenderIpSearch(capturedAdmin, ip, info, dbRows));
        });
    }

    private void RunIpSearchBySteamId(CCSPlayerController? admin, string sidStr)
    {
        var rows = _db.GetPlayersBySteamId(sidStr);
        if (rows.Count == 0)
        {
            Send(admin, ChatHelper.Error($"No record for SteamID {sidStr}."));
            return;
        }
        // Use the most-recent IP for the WHOIS lookup.
        var ip = rows[0].IpAddress;
        var name = rows[0].Name;

        Send(admin, ChatHelper.Header($"IP Search: {name} ({sidStr})"));

        var dbRows = _db.GetPlayersByIp(ip);
        Task.Run(async () =>
        {
            var info = await _lookup.LookupAsync(ip).ConfigureAwait(false);
            Server.NextFrame(() => RenderIpSearch(admin, ip, info, dbRows));
        });
    }

    private void RunIpSearchByName(CCSPlayerController? admin, string nameQuery)
    {
        var rows = _db.GetPlayersByNameLike(nameQuery);
        if (rows.Count == 0)
        {
            Send(admin, ChatHelper.Error($"No DB record matches '{nameQuery}'."));
            return;
        }

        var byUser = rows.GroupBy(r => r.SteamId).ToList();
        if (byUser.Count > 1)
        {
            Send(admin, ChatHelper.Warn($"Multiple users match '{nameQuery}':"));
            foreach (var g in byUser.Take(10))
            {
                var top = g.OrderByDescending(x => x.LastSeen).First();
                Send(admin,
                    $"  {ChatColors.Default}- {ChatColors.LightBlue}{top.Name}" +
                    $"{ChatColors.Default} ({ChatColors.Yellow}{top.SteamId}{ChatColors.Default})");
            }
            Send(admin, ChatHelper.Info("Re-run with 'ipsearch <SteamID64>' or the exact name."));
            return;
        }

        var user  = byUser[0].OrderByDescending(r => r.LastSeen).First();
        var ip    = user.IpAddress;
        var name  = user.Name;

        Send(admin, ChatHelper.Header($"IP Search: {name} ({user.SteamId})"));
        var dbRows = _db.GetPlayersByIp(ip);
        Task.Run(async () =>
        {
            var info = await _lookup.LookupAsync(ip).ConfigureAwait(false);
            Server.NextFrame(() => RenderIpSearch(admin, ip, info, dbRows));
        });
    }

    private void RenderIpSearch(
        CCSPlayerController? admin,
        string ip,
        IPInfo? info,
        List<StoredPlayer> dbRows)
    {
        if (admin != null && !admin.IsValid) return;

        Send(admin, ChatHelper.Field("IP Address", $"{ChatColors.LightRed}{ip}{ChatColors.Default}"));
        if (info != null)
        {
            Send(admin, ChatHelper.Field("ISP", $"{ChatColors.Yellow}{info.Isp}{ChatColors.Default}"));
            var loc = $"{info.City}, {info.Region}";
            if (!string.IsNullOrWhiteSpace(info.Country))
                loc += $", {info.Country}";
            Send(admin, ChatHelper.Field("Location", $"{ChatColors.LightGreen}{loc}{ChatColors.Default}"));
        }
        else
        {
            Send(admin, ChatHelper.Warn("WHOIS lookup unavailable (private IP or service down)."));
        }

        if (dbRows.Count == 0)
        {
            Send(admin, ChatHelper.Info("No accounts associated in DB."));
            return;
        }

        var unique = dbRows.GroupBy(r => r.SteamId)
                           .Select(g => g.OrderByDescending(x => x.LastSeen).First())
                           .OrderByDescending(r => r.LastSeen)
                           .ToList();

        Send(admin, ChatHelper.Field(
            "Associated Accounts",
            unique.Count > 1
                ? $"{ChatColors.Gold}{unique.Count} (possible alts!){ChatColors.Default}"
                : $"{ChatColors.Default}{unique.Count}"));
        foreach (var u in unique.Take(10))
            Send(admin,
                $"  {ChatColors.Default}- {ChatColors.LightBlue}{u.Name}" +
                $"{ChatColors.Default} ({ChatColors.Yellow}{u.SteamId}{ChatColors.Default})" +
                $" {ChatColors.Grey}— last seen {u.LastSeen:yyyy-MM-dd HH:mm} UTC");
        if (unique.Count > 10)
            Send(admin, ChatHelper.Info($"...and {unique.Count - 10} more."));
    }

    // ──────────────────────────────────────────────────────────────────
    //  30-second sweep
    // ──────────────────────────────────────────────────────────────────
    private void SweepExpiredBans()
    {
        try
        {
            var expired = _db.ExpireDueBans();
            // Also drop any pending confirmations that are stale.
            foreach (var kv in _pending)
                if (kv.Value.IsExpired)
                    _pending.TryRemove(kv.Key, out _);

            if (expired.Count > 0)
                Logger.LogInformation(
                    "[CS2-IP-Suite] Auto-expired {n} ban(s): {ips}",
                    expired.Count, string.Join(",", expired));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[CS2-IP-Suite] SweepExpiredBans error");
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────
    private void KickIfOnline(string ip)
    {
        var matches = _current.Values.Where(p => p.IpAddress == ip).ToList();
        foreach (var m in matches)
        {
            Server.NextFrame(() =>
            {
                var p = Utilities.GetPlayerFromSlot(m.Slot);
                if (p != null && p.IsValid && p.UserId.HasValue)
                    Server.ExecuteCommand(
                        $"kickid {p.UserId.Value} \"You are IP banned from this server.\"");
            });
            _current.TryRemove(m.SteamId, out _);
        }
    }

    /// <summary>Send a chat line to a player, or to the server console if
    /// <paramref name="admin"/> is null (which only happens for server-side
    /// console invocations).</summary>
    private void Send(CCSPlayerController? admin, string line)
    {
        if (admin != null && admin.IsValid)
        {
            admin.PrintToChat(line);
        }
        else
        {
            // Server console — strip color bytes for readability.
            Server.PrintToConsole(ChatHelper.StripColors(line));
        }
    }

    private static string AdminName(CCSPlayerController? admin)
        => admin == null ? "CONSOLE" : (admin.PlayerName ?? "Admin");

    private void SendUsage(CCSPlayerController? admin, bool isBan)
    {
        if (isBan)
        {
            Send(admin, ChatHelper.Header("Usage: ipban"));
            Send(admin, ChatHelper.Info("  ipban name <username> <minutes>"));
            Send(admin, ChatHelper.Info("  ipban ip   <xxx.xxx.xxx.xxx> <minutes>"));
            Send(admin, ChatHelper.Info("  ipban id   <SteamID64> <minutes>"));
            Send(admin, ChatHelper.Info(
                $"{ChatColors.Grey}(prefix with '!' in chat — both work)"));
        }
        else
        {
            Send(admin, ChatHelper.Header("Usage: ipunban"));
            Send(admin, ChatHelper.Info("  ipunban name <username>"));
            Send(admin, ChatHelper.Info("  ipunban ip   <xxx.xxx.xxx.xxx>"));
            Send(admin, ChatHelper.Info("  ipunban id   <SteamID64>"));
            Send(admin, ChatHelper.Info(
                $"{ChatColors.Grey}(prefix with '!' in chat — both work)"));
        }
    }

    /// <summary>Strip the trailing :port (if any) from CSS's IpAddress string.</summary>
    private static string ExtractIp(string? ipWithPort)
    {
        if (string.IsNullOrWhiteSpace(ipWithPort)) return string.Empty;
        var idx = ipWithPort.IndexOf(':');
        return idx > 0 ? ipWithPort[..idx] : ipWithPort;
    }

    private static bool IsValidIPv4(string s)
    {
        if (!IPAddress.TryParse(s, out var ip)) return false;
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    private static string[] SplitArgs(string s)
        => (s ?? string.Empty).Trim()
                              .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string FormatDuration(int minutes)
    {
        if (minutes <= 0) return "permanent";
        if (minutes < 60) return $"{minutes} minute(s)";
        var hours = minutes / 60.0;
        if (hours < 24) return $"{hours:0.##} hour(s) ({minutes} min)";
        var days = hours / 24.0;
        return $"{days:0.##} day(s) ({minutes} min)";
    }
}
