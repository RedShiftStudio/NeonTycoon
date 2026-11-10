using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NeonTycoon.Application;
using NeonTycoon.Domain;
using NeonTycoon.Infrastructure;
using Serilog;
using StackExchange.Redis;

var builder=WebApplication.CreateBuilder(args);
var jwt=builder.Configuration["JWT_SECRET"]??throw new InvalidOperationException("JWT_SECRET required");
var bot=builder.Configuration["BOT_TOKEN"]??throw new InvalidOperationException("BOT_TOKEN required");
if(Encoding.UTF8.GetByteCount(jwt)<32)throw new InvalidOperationException("JWT_SECRET must be at least 32 bytes");
builder.Host.UseSerilog((_,log)=>log.WriteTo.Console(new Serilog.Formatting.Compact.RenderedCompactJsonFormatter()));
builder.Services.AddDbContext<GameDb>(o=>o.UseNpgsql(builder.Configuration.GetConnectionString("Game")??throw new InvalidOperationException("ConnectionStrings__Game required")));
builder.Services.AddScoped<GameService>();
builder.Services.AddSingleton<IConnectionMultiplexer>(_=>ConnectionMultiplexer.Connect(builder.Configuration["REDIS"]??throw new InvalidOperationException("REDIS required")));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o=>{o.TokenValidationParameters=new(){ValidateIssuer=true,ValidIssuer="neon-tycoon",ValidateAudience=true,ValidAudience="neon-tycoon-web",ValidateIssuerSigningKey=true,IssuerSigningKey=new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt)),ValidateLifetime=true,ClockSkew=TimeSpan.FromSeconds(30)};});
builder.Services.AddAuthorization();builder.Services.AddOpenApi();
builder.Services.Configure<ForwardedHeadersOptions>(o=>{o.ForwardedHeaders=ForwardedHeaders.XForwardedFor|ForwardedHeaders.XForwardedProto;o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.16.0.0/12"));o.ForwardLimit=1;});
builder.Services.AddHealthChecks().AddCheck<DatabaseHealth>("postgres").AddCheck<RedisHealth>("redis");
builder.Services.AddHostedService<EventRotation>();
builder.Services.AddRateLimiter(o=>{o.RejectionStatusCode=429;o.AddPolicy("game",ctx=>RateLimitPartition.GetFixedWindowLimiter(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)??ctx.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=75,Window=TimeSpan.FromMinutes(1)}));o.AddPolicy("auth",ctx=>RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=10,Window=TimeSpan.FromMinutes(1)}));});
var origin=builder.Configuration["PUBLIC_ORIGIN"]??throw new InvalidOperationException("PUBLIC_ORIGIN required");builder.Services.AddCors(o=>o.AddDefaultPolicy(p=>p.WithOrigins(origin).AllowAnyHeader().AllowAnyMethod()));
var app=builder.Build();if(args.Contains("--migrate")){using var scope=app.Services.CreateScope();var database=scope.ServiceProvider.GetRequiredService<GameDb>();await database.Database.MigrateAsync();await Catalog.Seed(database);return;}app.UseForwardedHeaders();app.UseSerilogRequestLogging(o=>o.GetLevel=(ctx,_,_)=>ctx.Request.Path.StartsWithSegments("/api/auth")?Serilog.Events.LogEventLevel.Debug:Serilog.Events.LogEventLevel.Information);
app.Use(async(ctx,next)=>{try{await next();}catch(GameException ex){ctx.Response.StatusCode=ex.Status;await ctx.Response.WriteAsJsonAsync(new{error=ex.Message});}catch(DbUpdateException){ctx.Response.StatusCode=409;await ctx.Response.WriteAsJsonAsync(new{error="Concurrent operation; refresh and retry"});}catch(Npgsql.PostgresException ex) when(ex.SqlState=="40001"){ctx.Response.StatusCode=409;await ctx.Response.WriteAsJsonAsync(new{error="Concurrent operation; refresh and retry"});}catch(Exception ex){app.Logger.LogError(ex,"Request failed");ctx.Response.StatusCode=500;await ctx.Response.WriteAsJsonAsync(new{error="Internal server error"});}});
app.UseCors();app.UseAuthentication();app.UseAuthorization();app.UseRateLimiter();
app.Use(async(ctx,next)=>{if(ctx.Request.Method=="POST"&&ctx.Request.Path.StartsWithSegments("/api")&&!ctx.Request.Path.StartsWithSegments("/api/auth")&&ctx.User.Identity?.IsAuthenticated==true){var key=ctx.Request.Headers["Idempotency-Key"].ToString();if(!Guid.TryParse(key,out var parsed)){ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error="Idempotency-Key UUID required"});return;}var cache=ctx.RequestServices.GetRequiredService<IConnectionMultiplexer>().GetDatabase();var accepted=await cache.StringSetAsync("action:"+ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)+":"+parsed.ToString("N"),"1",TimeSpan.FromDays(1),When.NotExists);if(!accepted){ctx.Response.StatusCode=409;await ctx.Response.WriteAsJsonAsync(new{error="Action already submitted"});return;}}await next();});
app.MapHealthChecks("/health/live",new(){Predicate=_=>false});app.MapHealthChecks("/health/ready");
app.MapOpenApi().RequireAuthorization();
app.MapPost("/api/auth/telegram",async(TelegramLogin input,GameDb db,GameService game)=>{
 if(string.IsNullOrWhiteSpace(input.InitData)||input.InitData.Length>8192)throw new GameException("Invalid initData",401);
 var pairs=input.InitData.Split('&').Select(x=>x.Split('=',2)).ToArray();if(pairs.Any(x=>x.Length!=2))throw new GameException("Invalid initData",401);var fields=new Dictionary<string,string>();foreach(var pair in pairs){var key=Uri.UnescapeDataString(pair[0].Replace('+',' '));if(!fields.TryAdd(key,Uri.UnescapeDataString(pair[1].Replace('+',' '))))throw new GameException("Invalid initData",401);}
 if(!fields.Remove("hash",out var hash)||hash.Length!=64||!hash.All(Uri.IsHexDigit)||!fields.TryGetValue("auth_date",out var auth)||!long.TryParse(auth,out var stamp)||DateTimeOffset.UtcNow.ToUnixTimeSeconds()-stamp>3600||stamp-DateTimeOffset.UtcNow.ToUnixTimeSeconds()>60)throw new GameException("Telegram authentication expired",401);
 var check=string.Join('\n',fields.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>$"{x.Key}={x.Value}"));var secret=HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"),Encoding.UTF8.GetBytes(bot));var computed=HMACSHA256.HashData(secret,Encoding.UTF8.GetBytes(check));if(!CryptographicOperations.FixedTimeEquals(computed,Convert.FromHexString(hash)))throw new GameException("Invalid Telegram signature",401);
 if(!fields.TryGetValue("user",out var user))throw new GameException("Telegram user missing",401);
 long telegramId;string name;try{using var json=JsonDocument.Parse(user);var root=json.RootElement;if(!root.TryGetProperty("id",out var idProperty)||!idProperty.TryGetInt64(out telegramId)||telegramId<=0)throw new GameException("Invalid Telegram identity",401);name=root.TryGetProperty("first_name",out var first)?first.GetString()??"Runner":"Runner";name=name[..Math.Min(name.Length,64)];}catch(JsonException){throw new GameException("Invalid Telegram identity",401);}
 var player=await db.Players.SingleOrDefaultAsync(x=>x.TelegramId==telegramId);
 if(player is null){player=new(){TelegramId=telegramId,Name=name};db.Players.Add(player);await db.SaveChangesAsync();if(fields.TryGetValue("start_param",out var code)&&code.Length<=64){var referrer=await db.Players.SingleOrDefaultAsync(x=>x.ReferralCode==code);if(referrer is not null&&referrer.Id!=player.Id){db.Referrals.Add(new(){ReferrerId=referrer.Id,ReferredId=player.Id});referrer.Resources=await db.PlayerResources.SingleAsync(x=>x.PlayerId==referrer.Id);referrer.Resources.Credits=GameRules.Clamp(referrer.Resources.Credits+500);referrer.LifetimeCredits=GameRules.Clamp(referrer.LifetimeCredits+500);db.EconomyTransactions.Add(new(){PlayerId=referrer.Id,Kind="referral",Amount=500});}}await db.SaveChangesAsync();}
 var token=new JwtSecurityToken("neon-tycoon","neon-tycoon-web",[new Claim(ClaimTypes.NameIdentifier,player.Id.ToString())],expires:DateTime.UtcNow.AddHours(12),signingCredentials:new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt)),SecurityAlgorithms.HmacSha256));
 return new AuthDto(new JwtSecurityTokenHandler().WriteToken(token),await game.State(player.Id));
 }).RequireRateLimiting("auth");
var api=app.MapGroup("/api").RequireAuthorization().RequireRateLimiting("game");
long Id(ClaimsPrincipal user)=>long.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
api.MapGet("/player/me",(ClaimsPrincipal u,GameService g)=>g.State(Id(u)));
api.MapGet("/player/economy",(ClaimsPrincipal u,GameService g)=>g.State(Id(u)));
api.MapPost("/game/tap",(ClaimsPrincipal u,GameService g)=>g.Tap(Id(u)));
api.MapPost("/game/collect",(ClaimsPrincipal u,GameService g)=>g.State(Id(u)));
api.MapPost("/game/prestige",(ClaimsPrincipal u,GameService g)=>g.Prestige(Id(u)));
api.MapGet("/buildings",async(ClaimsPrincipal u,GameDb db)=>{var id=Id(u);var owned=await db.PlayerBuildings.Where(x=>x.PlayerId==id).ToListAsync();return Catalog.Buildings.Select(b=>new{b.Id,b.Name,b.Icon,b.District,b.RequiredLevel,b.Production,b.BaseCost,Count=owned.FirstOrDefault(x=>x.BuildingId==b.Id)?.Count??0,Level=owned.FirstOrDefault(x=>x.BuildingId==b.Id)?.Level??0,PurchaseCost=GameRules.Cost(b,owned.FirstOrDefault(x=>x.BuildingId==b.Id),false),UpgradeCost=GameRules.Cost(b,owned.FirstOrDefault(x=>x.BuildingId==b.Id),true)});});
api.MapPost("/buildings/{id}/purchase",(string id,ClaimsPrincipal u,GameService g)=>g.Build(Id(u),id,false));api.MapPost("/buildings/{id}/upgrade",(string id,ClaimsPrincipal u,GameService g)=>g.Build(Id(u),id,true));
api.MapGet("/research",async(ClaimsPrincipal u,GameDb db)=>{var id=Id(u);var owned=await db.PlayerResearch.Where(x=>x.PlayerId==id).Select(x=>x.ResearchId).ToListAsync();return Catalog.Research.Select(x=>new{x.Id,x.Name,x.Description,x.Cost,x.RequiredLevel,x.Prerequisite,Unlocked=owned.Contains(x.Id)});});
api.MapPost("/research/{id}/unlock",(string id,ClaimsPrincipal u,GameService g)=>g.Research(Id(u),id));
api.MapGet("/missions",async(ClaimsPrincipal u,GameDb db)=>{var id=Id(u);var states=await db.PlayerMissions.Where(x=>x.PlayerId==id).ToListAsync();return Catalog.Missions.Select(x=>{var s=states.FirstOrDefault(y=>y.MissionId==x.Id);var start=x.Period=="weekly"?DateTime.UtcNow.Date.AddDays(-((int)DateTime.UtcNow.DayOfWeek+6)%7):DateTime.UtcNow.Date;return new{x.Id,x.Name,x.Period,x.Target,x.Reward,Progress=s?.PeriodStart>=start?s.Progress:0,Claimed=s?.PeriodStart>=start&&s.Claimed};});});
api.MapPost("/missions/{id}/claim",(string id,ClaimsPrincipal u,GameService g)=>g.Mission(Id(u),id));
api.MapGet("/achievements",async(ClaimsPrincipal u,GameDb db,GameService g)=>{var p=await g.Load(Id(u));return Catalog.Achievements.Select(x=>new{x.Id,x.Name,x.Target,x.Reward,Progress=g.AchievementProgress(p,x),Claimed=p.Achievements.Any(y=>y.AchievementId==x.Id)});});
api.MapPost("/achievements/{id}/claim",(string id,ClaimsPrincipal u,GameService g)=>g.Achievement(Id(u),id));
api.MapGet("/leaderboard",async(GameDb db,IConnectionMultiplexer redis)=>{var cache=redis.GetDatabase();var value=await cache.StringGetAsync("leaderboard:v1");if(value.HasValue)return Results.Content(value.ToString(),"application/json");var top=await db.Players.OrderByDescending(x=>x.LifetimeCredits).Take(100).Select(x=>new{x.Name,x.Level,x.Prestige,x.LifetimeCredits}).ToListAsync();var data=JsonSerializer.Serialize(top,new JsonSerializerOptions(JsonSerializerDefaults.Web));await cache.StringSetAsync("leaderboard:v1",data,TimeSpan.FromSeconds(60));return Results.Content(data,"application/json");});
api.MapGet("/referrals",async(ClaimsPrincipal u,GameDb db)=>{var id=Id(u);return new{Count=await db.Referrals.CountAsync(x=>x.ReferrerId==id),Code=(await db.Players.SingleAsync(x=>x.Id==id)).ReferralCode};});
api.MapGet("/daily-rewards",async(ClaimsPrincipal u,GameDb db)=>{var id=Id(u);var p=await db.Players.SingleAsync(x=>x.Id==id);return new{p.DailyStreak,Available=p.LastDaily?.Date!=DateTime.UtcNow.Date,NextReward=100m*(p.LastDaily?.Date==DateTime.UtcNow.Date.AddDays(-1)?Math.Min(7,p.DailyStreak+1):1)*(1+p.Prestige*.15m)};});
api.MapPost("/daily-rewards/claim",(ClaimsPrincipal u,GameService g)=>g.Daily(Id(u)));
api.MapGet("/events",(GameDb db)=>db.GameEvents.Where(x=>x.EndsAt>DateTime.UtcNow).OrderBy(x=>x.StartsAt).Select(x=>new{x.Name,x.StartsAt,x.EndsAt,x.Multiplier}));
app.Run();
public sealed class EventRotation(IServiceScopeFactory scopes):BackgroundService
{
 protected override async Task ExecuteAsync(CancellationToken stop){using var timer=new PeriodicTimer(TimeSpan.FromHours(1));do{try{using var scope=scopes.CreateScope();var db=scope.ServiceProvider.GetRequiredService<GameDb>();var week=DateTime.UtcNow.Date.AddDays(-((int)DateTime.UtcNow.DayOfWeek+6)%7);if(!await db.GameEvents.AnyAsync(x=>x.StartsAt==week,stop)){db.GameEvents.Add(new(){Name="Neon Surge",StartsAt=week,EndsAt=week.AddDays(2),Multiplier=1.5m});await db.SaveChangesAsync(stop);}}catch(Exception ex){Console.Error.WriteLine($"Event rotation: {ex.GetType().Name}");} }while(await timer.WaitForNextTickAsync(stop));}
}

public sealed class DatabaseHealth(GameDb db):Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck{public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,CancellationToken cancellationToken=default)=>await db.Database.CanConnectAsync(cancellationToken)?Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy():Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy();}

public sealed class RedisHealth(IConnectionMultiplexer connection):Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck{public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,CancellationToken cancellationToken=default)=>await connection.GetDatabase().PingAsync()<TimeSpan.FromSeconds(3)?Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy():Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy();}
