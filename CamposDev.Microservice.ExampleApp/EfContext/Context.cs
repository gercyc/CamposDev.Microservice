using Microsoft.EntityFrameworkCore;

namespace Demo.MicroserviceAspnet.EfContext;

public class Context(DbContextOptions<Context> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        
    }
}