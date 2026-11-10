namespace NeonTycoon.Domain;

public sealed class Player
{
    public long Id { get; set; }
    public long TelegramId { get; set; }
    public string Name { get; set; } = "Runner";
    public string ReferralCode { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public int Level { get; set; } = 1;
    public decimal Experience { get; set; }
    public int Prestige { get; set; }
    public decimal LifetimeCredits { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastTap { get; set; }
    public DateTime? LastDaily { get; set; }
    public int DailyStreak { get; set; }
    public PlayerResources Resources { get; set; } = new();
    public List<PlayerBuilding> Buildings { get; set; } = [];
    public List<PlayerResearch> Technologies { get; set; } = [];
    public List<PlayerMission> Missions { get; set; } = [];
    public List<PlayerAchievement> Achievements { get; set; } = [];
}
public sealed class PlayerResources { public long PlayerId { get; set; } public decimal Credits { get; set; } = 100; public decimal Energy { get; set; } = 100; public DateTime EnergyAt { get; set; } = DateTime.UtcNow; }
public sealed class BuildingDefinition { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public int District { get; set; } public int RequiredLevel { get; set; } public decimal BaseCost { get; set; } public decimal Production { get; set; } public string Icon { get; set; } = ""; }
public sealed class PlayerBuilding { public long PlayerId { get; set; } public string BuildingId { get; set; } = ""; public int Level { get; set; } public int Count { get; set; } }
public sealed class ResearchDefinition { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Description { get; set; } = ""; public string? Prerequisite { get; set; } public string Effect { get; set; } = ""; public decimal Cost { get; set; } public int RequiredLevel { get; set; } }
public sealed class PlayerResearch { public long PlayerId { get; set; } public string ResearchId { get; set; } = ""; public DateTime UnlockedAt { get; set; } = DateTime.UtcNow; }
public sealed class Mission { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Kind { get; set; } = ""; public string Period { get; set; } = "daily"; public int Target { get; set; } public decimal Reward { get; set; } }
public sealed class PlayerMission { public long PlayerId { get; set; } public string MissionId { get; set; } = ""; public DateTime PeriodStart { get; set; } public int Progress { get; set; } public bool Claimed { get; set; } }
public sealed class Achievement { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Kind { get; set; } = ""; public int Target { get; set; } public decimal Reward { get; set; } }
public sealed class PlayerAchievement { public long PlayerId { get; set; } public string AchievementId { get; set; } = ""; public bool Claimed { get; set; } }
public sealed class DailyReward { public long Id { get; set; } public long PlayerId { get; set; } public DateTime Day { get; set; } public decimal Amount { get; set; } }
public sealed class Referral { public long Id { get; set; } public long ReferrerId { get; set; } public long ReferredId { get; set; } public DateTime CreatedAt { get; set; } = DateTime.UtcNow; }
public sealed class EconomyTransaction { public long Id { get; set; } public long PlayerId { get; set; } public string Kind { get; set; } = ""; public decimal Amount { get; set; } public DateTime CreatedAt { get; set; } = DateTime.UtcNow; }
public sealed class GameEvent { public long Id { get; set; } public string Name { get; set; } = ""; public DateTime StartsAt { get; set; } public DateTime EndsAt { get; set; } public decimal Multiplier { get; set; } = 1; }
public sealed class GameException(string message, int status = 400) : Exception(message) { public int Status { get; } = status; }
public static class GameRules
{
    public const decimal Maximum = 99999999999999999999999999m;
    public static decimal Clamp(decimal amount) => Math.Min(Maximum, Math.Max(0, decimal.Floor(amount * 100) / 100));
    public static decimal Cost(BuildingDefinition b, PlayerBuilding? owned, bool upgrade)
    { var power=upgrade ? owned?.Level ?? 0 : owned?.Count ?? 0; decimal cost=b.BaseCost; for(var i=0;i<power;i++){var factor=upgrade?2.8m:1.18m;if(cost>Maximum/factor)return Maximum;cost*=factor;}return Clamp(cost); }
    public static decimal PerSecond(Player p, IEnumerable<BuildingDefinition> definitions, decimal eventMultiplier = 1)
    {
        decimal total = 0;
        foreach (var b in definitions)
        {
            var owned = p.Buildings.FirstOrDefault(x => x.BuildingId == b.Id);
            if (owned is not null) total += b.Production * owned.Count * (1 + owned.Level * .5m);
        }
        return Clamp(total * (1 + p.Prestige * .15m) * (1 + p.Technologies.Count(x => x.ResearchId.StartsWith("production")) * .2m) * eventMultiplier);
    }
    public static decimal Tap(Player p) => Clamp((1 + p.Level / 5m) * (1 + p.Prestige * .15m) * (1 + p.Technologies.Count(x => x.ResearchId.StartsWith("tap")) * .3m));
    public static decimal NextLevel(int level) => 50m * level * level;
}
