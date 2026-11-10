namespace NeonTycoon.Application;
public sealed record TelegramLogin(string InitData);
public sealed record PlayerDto(long Id,string Name,int Level,decimal Experience,decimal NextLevel,int Prestige,decimal Credits,decimal Energy,decimal EnergyCap,decimal PerSecond,decimal TapReward,decimal LifetimeCredits,string ReferralCode,DateTime ServerTime);
public sealed record AuthDto(string Token,PlayerDto Player);
