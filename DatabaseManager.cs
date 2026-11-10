using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CS2IPSuite;

/// <summary>
/// Owns all DB I/O for CS2-IP-Suite. The DB file lives in the plugin's
/// configs folder (auto-created) and stores:
///   - players: every (steamid, ip) pair ever seen, with last-seen.
///   - bans:    historical and active ban entries keyed by IP.
///
/// An <see cref="ActiveBans"/> dictionary mirrors the active rows so that
/// connection checks are O(1) and never block the game thread on disk.
/// </summary>
public sealed class DatabaseManager : IDisposable
{
    private readonly string  _dbPath;
    private readonly string  _connStr;
    private readonly ILogger _logger;
    private readonly object  _writeLock = new();

    /// <summary>IP -&gt; active ban. Updated on every ban/unban/expiry.</summary>
    public ConcurrentDictionary<string, BanInfo> ActiveBans { get; } = new();

    public DatabaseManager(string dbPath, ILogger logger)
    {
        _dbPath  = dbPath;
        _connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode       = SqliteOpenMode.ReadWriteCreate,
            Cache      = SqliteCacheMode.Shared,
        }.ToString();
        _logger = logger;
    }

    // Init / schema

    public void Initialize()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var c = Open();
        ExecNonQuery(c, @"
            CREATE TABLE IF NOT EXISTS players (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                steam_id    TEXT NOT NULL,
                name        TEXT NOT NULL,
                ip_address  TEXT NOT NULL,
                last_seen   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(steam_id, ip_address)
            );
            CREATE INDEX IF NOT EXISTS idx_players_steam ON players(steam_id);
            CREATE INDEX IF NOT EXISTS idx_players_ip    ON players(ip_address);
            CREATE INDEX IF NOT EXISTS idx_players_name  ON players(name);

            CREATE TABLE IF NOT EXISTS bans (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                ip_address  TEXT NOT NULL,
                banned_at   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                expires_at  DATETIME,
                banned_by   TEXT,
                is_active   INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS idx_bans_ip_active
                ON bans(ip_address, is_active);
        ");

        // Mirror active bans into memory for fast connect-time lookups.
        ReloadActiveBansToMemory();
        _logger.LogInformation(
            "[CS2-IP-Suite] DB ready at {p}. Active bans loaded: {n}",
            _dbPath, ActiveBans.Count);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        // WAL lets readers run alongside writes, which suits this read-heavy workload.
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private static void ExecNonQuery(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // Players

    public Task RecordPlayerAsync(PlayerInfo p) => Task.Run(() => RecordPlayer(p));

    public void RecordPlayer(PlayerInfo p)
    {
        if (p.SteamId == 0 || string.IsNullOrEmpty(p.IpAddress)) return;
        lock (_writeLock)
        {
            try
            {
                using var c = Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO players (steam_id, name, ip_address, last_seen)
                    VALUES ($sid, $name, $ip, CURRENT_TIMESTAMP)
                    ON CONFLICT(steam_id, ip_address) DO UPDATE SET
                        name      = excluded.name,
                        last_seen = CURRENT_TIMESTAMP;";
                cmd.Parameters.AddWithValue("$sid",  p.SteamId.ToString());
                cmd.Parameters.AddWithValue("$name", p.Name);
                cmd.Parameters.AddWithValue("$ip",   p.IpAddress);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CS2-IP-Suite] RecordPlayer failed for {sid}", p.SteamId);
            }
        }
    }

    /// <summary>Return all distinct (steamid, name, last-seen) rows that have
    /// ever connected from <paramref name="ip"/>.</summary>
    public List<StoredPlayer> GetPlayersByIp(string ip)
    {
        var list = new List<StoredPlayer>();
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                SELECT steam_id, name, ip_address, last_seen
                FROM players
                WHERE ip_address = $ip
                ORDER BY last_seen DESC;";
            cmd.Parameters.AddWithValue("$ip", ip);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new StoredPlayer
                {
                    SteamId    = r.GetString(0),
                    Name       = r.GetString(1),
                    IpAddress  = r.GetString(2),
                    LastSeen   = r.GetDateTime(3),
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CS2-IP-Suite] GetPlayersByIp({ip})", ip);
        }
        return list;
    }

    public List<StoredPlayer> GetPlayersBySteamId(string steamId)
    {
        var list = new List<StoredPlayer>();
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                SELECT steam_id, name, ip_address, last_seen
                FROM players
                WHERE steam_id = $sid
                ORDER BY last_seen DESC;";
            cmd.Parameters.AddWithValue("$sid", steamId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new StoredPlayer
                {
                    SteamId   = r.GetString(0),
                    Name      = r.GetString(1),
                    IpAddress = r.GetString(2),
                    LastSeen  = r.GetDateTime(3),
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CS2-IP-Suite] GetPlayersBySteamId({sid})", steamId);
        }
        return list;
    }

    /// <summary>Case-insensitive substring search on the players table. Returns
    /// the most recently seen row per distinct steamid.</summary>
    public List<StoredPlayer> GetPlayersByNameLike(string nameFragment)
    {
        var list = new List<StoredPlayer>();
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                SELECT steam_id, name, ip_address, last_seen FROM players p
                WHERE LOWER(name) LIKE '%' || LOWER($q) || '%'
                  AND last_seen = (SELECT MAX(last_seen) FROM players p2
                                   WHERE p2.steam_id = p.steam_id)
                ORDER BY last_seen DESC;";
            cmd.Parameters.AddWithValue("$q", nameFragment);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new StoredPlayer
                {
                    SteamId   = r.GetString(0),
                    Name      = r.GetString(1),
                    IpAddress = r.GetString(2),
                    LastSeen  = r.GetDateTime(3),
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CS2-IP-Suite] GetPlayersByNameLike({q})", nameFragment);
        }
        return list;
    }

    // Bans

    public void ReloadActiveBansToMemory()
    {
        ActiveBans.Clear();
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                SELECT id, ip_address, banned_at, expires_at, banned_by
                FROM bans
                WHERE is_active = 1;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var ban = new BanInfo
                {
                    Id         = r.GetInt64(0),
                    IpAddress  = r.GetString(1),
                    BannedAt   = r.GetDateTime(2),
                    ExpiresAt  = r.IsDBNull(3) ? null : r.GetDateTime(3),
                    BannedBy   = r.IsDBNull(4) ? "" : r.GetString(4),
                    IsActive   = true,
                };
                ActiveBans[ban.IpAddress] = ban;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CS2-IP-Suite] ReloadActiveBansToMemory failed");
        }
    }

    public bool IsIpBanned(string ip)
    {
        if (!ActiveBans.TryGetValue(ip, out var b)) return false;
        if (b.IsExpired) return false;
        return true;
    }

    /// <summary>Insert a new active ban. If <paramref name="durationMinutes"/>
    /// is &lt;= 0 the ban is treated as permanent (NULL expires_at).</summary>
    public BanInfo AddBan(string ip, int durationMinutes, string adminName)
    {
        DateTime? expires = durationMinutes > 0
            ? DateTime.UtcNow.AddMinutes(durationMinutes)
            : (DateTime?)null;

        lock (_writeLock)
        {
            using var c = Open();

            // Deactivate earlier active rows so each IP has at most one active ban.
            using (var deactivate = c.CreateCommand())
            {
                deactivate.CommandText =
                    "UPDATE bans SET is_active = 0 WHERE ip_address = $ip AND is_active = 1;";
                deactivate.Parameters.AddWithValue("$ip", ip);
                deactivate.ExecuteNonQuery();
            }

            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO bans (ip_address, banned_at, expires_at, banned_by, is_active)
                VALUES ($ip, CURRENT_TIMESTAMP, $exp, $by, 1);
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$ip",  ip);
            cmd.Parameters.AddWithValue("$exp", (object?)expires ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$by",  adminName);
            var id = (long)(cmd.ExecuteScalar() ?? 0L);

            var ban = new BanInfo
            {
                Id         = id,
                IpAddress  = ip,
                BannedAt   = DateTime.UtcNow,
                ExpiresAt  = expires,
                BannedBy   = adminName,
                IsActive   = true,
            };
            ActiveBans[ip] = ban;
            return ban;
        }
    }

    /// <summary>Mark every active ban for <paramref name="ip"/> as inactive.</summary>
    /// <returns>true if at least one row was deactivated.</returns>
    public bool RemoveBan(string ip)
    {
        bool removed;
        lock (_writeLock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                "UPDATE bans SET is_active = 0 WHERE ip_address = $ip AND is_active = 1;";
            cmd.Parameters.AddWithValue("$ip", ip);
            removed = cmd.ExecuteNonQuery() > 0;
        }
        ActiveBans.TryRemove(ip, out _);
        return removed;
    }

    /// <summary>Sweep expired rows. Called by the 30-second heartbeat.</summary>
    /// <returns>List of IPs that just expired.</returns>
    public List<string> ExpireDueBans()
    {
        var expired = new List<string>();
        lock (_writeLock)
        {
            try
            {
                using var c = Open();
                using (var sel = c.CreateCommand())
                {
                    sel.CommandText = @"
                        SELECT ip_address FROM bans
                        WHERE is_active = 1
                          AND expires_at IS NOT NULL
                          AND expires_at <= CURRENT_TIMESTAMP;";
                    using var r = sel.ExecuteReader();
                    while (r.Read()) expired.Add(r.GetString(0));
                }

                if (expired.Count > 0)
                {
                    using var upd = c.CreateCommand();
                    upd.CommandText = @"
                        UPDATE bans SET is_active = 0
                        WHERE is_active = 1
                          AND expires_at IS NOT NULL
                          AND expires_at <= CURRENT_TIMESTAMP;";
                    upd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CS2-IP-Suite] ExpireDueBans failed");
            }
        }

        foreach (var ip in expired) ActiveBans.TryRemove(ip, out _);
        return expired;
    }

    public void Dispose()
    {
        // Clear the connection pool so the file handle is released for hot reload.
        SqliteConnection.ClearAllPools();
    }
}
