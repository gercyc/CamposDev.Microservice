namespace CamposDev.Microservice.RabbitMq.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionDatabaseExtensions
{
    /// <summary>
    /// Registra EF Core (DbContext) e Dapper (IDbConnectionFactory) lendo a seção "Database".
    /// </summary>
    public static IServiceCollection AddPersistence<TDbContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "Database") where TDbContext : DbContext
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(sectionName));

        // EF Core
        services.AddDbContextCore<TDbContext>(configuration, sectionName);

        // Dapper
        services.AddScoped<IDbConnectionFactory, DbConnectionFactory>();

        return services;
    }

    private static IServiceCollection AddDbContextCore<TDbContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName) where TDbContext : DbContext
    {
        var options = configuration.GetSection(sectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
        var cs = configuration.GetConnectionString(options.ConnectionStringName)
                 ?? throw new InvalidOperationException($"ConnectionString '{options.ConnectionStringName}' não encontrada.");

        var builder = services; // decide pool ou não

        if (options.UseDbContextPool)
        {
            builder = services.AddDbContextPool<TDbContext>((sp, db) =>
            {
                ConfigureProvider(db, options, cs);
            });
        }
        else
        {
            builder = services.AddDbContext<TDbContext>((sp, db) =>
            {
                ConfigureProvider(db, options, cs);
            });
        }

        return builder;
    }

    private static void ConfigureProvider(DbContextOptionsBuilder db, DatabaseOptions opt, string cs)
    {
        var isSqlServer = opt.Provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase);

        if (isSqlServer)
        {
            db.UseSqlServer(cs, o =>
            {
                o.CommandTimeout(opt.CommandTimeoutSeconds);
                // o.MigrationsAssembly("SeuProjeto.Migrations"); // opcional
            });
        }
        else
        {
            db.UseNpgsql(cs, o =>
            {
                o.CommandTimeout(opt.CommandTimeoutSeconds);
                // o.MigrationsAssembly("SeuProjeto.Migrations"); // opcional
            });
        }

        db.EnableDetailedErrors(opt.EnableDetailedErrors);
        db.EnableSensitiveDataLogging(opt.EnableSensitiveDataLogging);
    }
}