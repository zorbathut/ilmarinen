using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ilmarinen.Database;

public class IlmarinenDbContextFactory : IDesignTimeDbContextFactory<IlmarinenDbContext>
{
    public IlmarinenDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<IlmarinenDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=ilmarinen;Username=ilmarinen;Password=ilmarinen");
        return new IlmarinenDbContext(optionsBuilder.Options);
    }
}
