using System;

namespace CS2IPSuite;

/// <summary>Currently connected player snapshot held in memory.</summary>
public sealed class PlayerInfo
{
    public ulong SteamId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Slot { get; set; }
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Active ban entry held both in memory and DB.</summary>
public sealed class BanInfo
{
    public long Id { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public DateTime BannedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }      // null = permanent
    public string BannedBy { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    public bool IsExpired =>
        ExpiresAt.HasValue && ExpiresAt.Value <= DateTime.UtcNow;
}

/// <summary>A historical (DB) record of a player connection event.</summary>
public sealed class StoredPlayer
{
    public string SteamId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public DateTime LastSeen { get; set; }
}

/// <summary>WHOIS-style geo/ISP info from the public lookup service.</summary>
public sealed class IPInfo
{
    public string Ip { get; set; } = string.Empty;
    public string Isp { get; set; } = "Unknown";
    public string City { get; set; } = "Unknown";
    public string Region { get; set; } = "Unknown";
    public string Country { get; set; } = "Unknown";
}

public enum PendingActionKind
{
    Ban,
    Unban
}

/// <summary>An admin's queued ban/unban awaiting yes/no confirmation.</summary>
public sealed class PendingAction
{
    public PendingActionKind Kind { get; set; }

    /// <summary>The IP(s) the action will operate on once confirmed.</summary>
    public List<string> IpTargets { get; set; } = new();

    /// <summary>Human-readable description shown in the confirmation prompt.</summary>
    public string DisplayInfo { get; set; } = string.Empty;

    /// <summary>For bans only. Minutes to ban for. &gt; 0.</summary>
    public int DurationMinutes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Pending actions auto-expire after 60 seconds.</summary>
    public bool IsExpired => (DateTime.UtcNow - CreatedAt).TotalSeconds > 60;
}
