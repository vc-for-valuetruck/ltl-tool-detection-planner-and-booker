using Microsoft.EntityFrameworkCore;

namespace MyApp.Api.Data;

/// <summary>
/// Application database context. Add your DbSet&lt;T&gt; entities here as your
/// application grows. The template ships with no entities so it stays generic.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
}
