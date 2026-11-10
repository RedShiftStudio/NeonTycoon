using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace NeonTycoon.Infrastructure;
public sealed class GameDbFactory:IDesignTimeDbContextFactory<GameDb>
{
 public GameDb CreateDbContext(string[] args)=>new(new DbContextOptionsBuilder<GameDb>().UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Game")??"Host=localhost;Database=neontycoon;Username=postgres;Password=local-design-only").Options);
}
