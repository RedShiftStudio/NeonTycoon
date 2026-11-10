using System.Data;
using Microsoft.EntityFrameworkCore;
using NeonTycoon.Application;
using NeonTycoon.Domain;
namespace NeonTycoon.Infrastructure;
public sealed class GameService(GameDb db)
{
 public async Task<Player> Load(long id)=>await db.Players.Include(x=>x.Resources).Include(x=>x.Buildings).Include(x=>x.Technologies).Include(x=>x.Missions).Include(x=>x.Achievements).SingleAsync(x=>x.Id==id);
 public decimal Multiplier=>db.GameEvents.Where(x=>x.StartsAt<=DateTime.UtcNow&&x.EndsAt>DateTime.UtcNow).Select(x=>x.Multiplier).FirstOrDefault() is var m && m>0?m:1;
 public PlayerDto Map(Player p)=>new(p.Id,p.Name,p.Level,p.Experience,GameRules.NextLevel(p.Level),p.Prestige,p.Resources.Credits,p.Resources.Energy,EnergyCap(p),GameRules.PerSecond(p,Catalog.Buildings,Multiplier),GameRules.Tap(p),p.LifetimeCredits,p.ReferralCode,DateTime.UtcNow);
 int EnergyCap(Player p)=>100+(p.Technologies.Any(x=>x.ResearchId=="energy-1")?50:0);
 public void Credit(Player p,decimal amount,string kind)
 {
  amount=GameRules.Clamp(amount); if(amount<=0)return;
  p.Resources.Credits=GameRules.Clamp(p.Resources.Credits+amount);p.LifetimeCredits=GameRules.Clamp(p.LifetimeCredits+amount);p.Experience+=Math.Min(amount/10,10000);while(p.Experience>=GameRules.NextLevel(p.Level)&&p.Level<100){p.Experience-=GameRules.NextLevel(p.Level);p.Level++;}db.EconomyTransactions.Add(new(){PlayerId=p.Id,Kind=kind,Amount=amount});Progress(p,"earn",(int)Math.Min(10000,amount));
 }
 public void Spend(Player p,decimal amount,string kind){if(amount<=0||p.Resources.Credits<amount)throw new GameException("Not enough NEON Credits");p.Resources.Credits-=amount;db.EconomyTransactions.Add(new(){PlayerId=p.Id,Kind=kind,Amount=-amount});}
 public void Progress(Player p,string kind,int amount)
 {
  foreach(var mission in Catalog.Missions.Where(x=>x.Kind==kind)){
   var start=mission.Period=="weekly"?DateTime.UtcNow.Date.AddDays(-((int)DateTime.UtcNow.DayOfWeek+6)%7):DateTime.UtcNow.Date;
   var state=p.Missions.FirstOrDefault(x=>x.MissionId==mission.Id);
   if(state is null){state=new(){PlayerId=p.Id,MissionId=mission.Id,PeriodStart=start};p.Missions.Add(state);}if(state.PeriodStart<start){state.PeriodStart=start;state.Progress=0;state.Claimed=false;}state.Progress=Math.Min(mission.Target,state.Progress+amount);
  }
 }
 public void Settle(Player p)
 {
  var now=DateTime.UtcNow;var seconds=Math.Max(0,(now-p.UpdatedAt).TotalSeconds);var cap=p.Technologies.Any(x=>x.ResearchId=="offline-1")?12*3600:8*3600;
  if(seconds>=1){Credit(p,GameRules.Clamp(GameRules.PerSecond(p,Catalog.Buildings,Multiplier)*(decimal)Math.Min(seconds,cap)),"production");p.UpdatedAt=now;}
  var energySeconds=Math.Max(0,(now-p.Resources.EnergyAt).TotalSeconds);if(energySeconds>=10){p.Resources.Energy=Math.Min(EnergyCap(p),p.Resources.Energy+(int)(energySeconds/10));p.Resources.EnergyAt=now;}
 }
 public async Task<T> Execute<T>(long id,Func<Player,Task<T>> action)
 {
  await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
  var p=await Load(id);Settle(p);var result=await action(p);await db.SaveChangesAsync();await tx.CommitAsync();return result;
 }
 public Task<PlayerDto> State(long id)=>Execute(id,p=>Task.FromResult(Map(p)));
 public Task<PlayerDto> Tap(long id)=>Execute(id,p=>{var now=DateTime.UtcNow;if((now-p.LastTap).TotalMilliseconds<180)throw new GameException("Slow down",429);if(p.Resources.Energy<1)throw new GameException("Energy depleted");p.Resources.Energy--;p.LastTap=now;Credit(p,GameRules.Tap(p),"tap");Progress(p,"tap",1);return Task.FromResult(Map(p));});
 public Task<PlayerDto> Build(long id,string key,bool upgrade)=>Execute(id,p=>{var def=Catalog.Buildings.FirstOrDefault(x=>x.Id==key)??throw new GameException("Unknown building",404);if(p.Level<def.RequiredLevel)throw new GameException("Level required");var owned=p.Buildings.FirstOrDefault(x=>x.BuildingId==key);if(upgrade&&(owned is null||owned.Count==0))throw new GameException("Build first");if((upgrade ? owned?.Level??0 : owned?.Count??0)>=250)throw new GameException("Facility limit reached");var cost=GameRules.Cost(def,owned,upgrade);if(p.Technologies.Any(x=>x.ResearchId=="discount-1"))cost=GameRules.Clamp(cost*.9m);Spend(p,cost,"building:"+key);if(owned is null){owned=new(){PlayerId=p.Id,BuildingId=key};p.Buildings.Add(owned);}if(upgrade)owned.Level++;else{owned.Count++;Progress(p,"build",1);}return Task.FromResult(Map(p));});
 public Task<PlayerDto> Research(long id,string key)=>Execute(id,p=>{var def=Catalog.Research.FirstOrDefault(x=>x.Id==key)??throw new GameException("Unknown technology",404);if(p.Level<def.RequiredLevel||def.Prerequisite is not null&&!p.Technologies.Any(x=>x.ResearchId==def.Prerequisite))throw new GameException("Research locked");if(p.Technologies.Any(x=>x.ResearchId==key))throw new GameException("Already unlocked",409);Spend(p,def.Cost,"research:"+key);p.Technologies.Add(new(){PlayerId=p.Id,ResearchId=key});return Task.FromResult(Map(p));});
 public Task<PlayerDto> Mission(long id,string key)=>Execute(id,p=>{var def=Catalog.Missions.FirstOrDefault(x=>x.Id==key)??throw new GameException("Unknown mission",404);Progress(p,def.Kind,0);var state=p.Missions.Single(x=>x.MissionId==key);if(state.Claimed||state.Progress<def.Target)throw new GameException("Mission incomplete or claimed");state.Claimed=true;Credit(p,def.Reward,"mission:"+key);return Task.FromResult(Map(p));});
 public Task<PlayerDto> Achievement(long id,string key)=>Execute(id,p=>{var def=Catalog.Achievements.FirstOrDefault(x=>x.Id==key)??throw new GameException("Unknown achievement",404);var progress=AchievementProgress(p,def);if(progress<def.Target||p.Achievements.Any(x=>x.AchievementId==key))throw new GameException("Achievement unavailable");p.Achievements.Add(new(){PlayerId=p.Id,AchievementId=key,Claimed=true});Credit(p,def.Reward,"achievement:"+key);return Task.FromResult(Map(p));});
 public int AchievementProgress(Player p,Achievement a)=>a.Kind switch{"build"=>p.Buildings.Sum(x=>x.Count),"level"=>p.Level,"earn"=>(int)Math.Min(int.MaxValue,p.LifetimeCredits),"prestige"=>p.Prestige,_=>0};
 public Task<PlayerDto> Daily(long id)=>Execute(id,p=>{var today=DateTime.UtcNow.Date;if(p.LastDaily?.Date==today)throw new GameException("Already claimed");p.DailyStreak=p.LastDaily?.Date==today.AddDays(-1)?Math.Min(7,p.DailyStreak+1):1;p.LastDaily=today;var amount=100m*p.DailyStreak*(1+p.Prestige*.15m);db.DailyRewards.Add(new(){PlayerId=p.Id,Day=today,Amount=amount});Credit(p,amount,"daily");return Task.FromResult(Map(p));});
 public Task<PlayerDto> Prestige(long id)=>Execute(id,p=>{if(p.Level<20||p.LifetimeCredits<1000000)throw new GameException("Requires level 20 and 1M lifetime credits");db.EconomyTransactions.Add(new(){PlayerId=p.Id,Kind="prestige-reset",Amount=-p.Resources.Credits});p.Prestige++;p.Level=1;p.Experience=0;p.Resources.Credits=100;p.Buildings.Clear();p.Technologies.Clear();p.Resources.Energy=EnergyCap(p);p.UpdatedAt=DateTime.UtcNow;return Task.FromResult(Map(p));});
}
