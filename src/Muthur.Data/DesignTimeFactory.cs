using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Muthur.Data;

/// <summary>Used only by `dotnet ef`; lets migrations be generated without starting the server.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<MuthurDb>
{
    public MuthurDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MuthurDb>().UseSqlite("Data Source=design-time.db").Options);
}
