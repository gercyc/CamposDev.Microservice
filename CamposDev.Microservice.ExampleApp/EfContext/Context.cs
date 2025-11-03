using Microsoft.EntityFrameworkCore;

namespace CamposDev.Microservice.ExampleApp.EfContext;

public class Context(DbContextOptions<Context> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        
    }
}