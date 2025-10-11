namespace CamposDev.Microservice.RabbitMq.Persistence;

public enum DatabaseProvider { Postgres, SqlServer }

public sealed class DatabaseOptions
{
    public string Provider { get; set; } = "SqlServer";      // "Postgres" | "SqlServer"
    public string ConnectionStringName { get; set; } = "DefaultConnection";
    public int CommandTimeoutSeconds { get; set; } = 30;
    public bool EnableDetailedErrors { get; set; } = false;
    public bool EnableSensitiveDataLogging { get; set; } = false;
    public bool UseDbContextPool { get; set; } = true;
}