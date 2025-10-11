namespace CamposDev.Microservice.RabbitMq.Persistence;

using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

internal sealed class DbConnectionFactory(IConfiguration config, IOptions<DatabaseOptions> opts)
    : IDbConnectionFactory
{
    private readonly DatabaseOptions _databaseOptions = opts.Value;

    public IDbConnection Create()
    {
        var cs = config.GetConnectionString(_databaseOptions.ConnectionStringName)
                 ?? throw new InvalidOperationException($"ConnectionString '{_databaseOptions.ConnectionStringName}' não encontrada.");

        return _databaseOptions.Provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)
            ? new SqlConnection(cs)
            : new NpgsqlConnection(cs);
    }

    public IDbConnection CreateOpen()
    {
        var conn = Create();
        conn.Open();
        return conn;
    }
}