namespace CamposDev.Microservice.RabbitMq.Extensions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

public static class SerilogExtensions
{
    /// <summary>
    /// Configura o Serilog como provedor de logging com console e configuração do appsettings.
    /// </summary>
    /// <param name="hostBuilder">O host builder da aplicação</param>
    /// <param name="configuration">Configuração da aplicação para ler settings do Serilog</param>
    /// <param name="consoleTheme">Tema do console (padrão: AnsiConsoleTheme.Sixteen)</param>
    /// <returns>O host builder configurado</returns>
    public static IHostBuilder AddSerilog(
        this IHostBuilder hostBuilder,
        IConfiguration configuration,
        AnsiConsoleTheme? consoleTheme = null)
    {
        return hostBuilder.UseSerilog((_, config) =>
        {
            config.WriteTo.Console(theme: consoleTheme ?? AnsiConsoleTheme.Sixteen)
                .ReadFrom.Configuration(configuration);
        });
    }

    /// <summary>
    /// Configura o Serilog como provedor de logging com console e configuração do appsettings.
    /// </summary>
    /// <param name="hostBuilder">O host builder da aplicação</param>
    /// <param name="configuration">Configuração da aplicação para ler settings do Serilog</param>
    /// <param name="consoleTheme">Tema do console (padrão: AnsiConsoleTheme.Sixteen)</param>
    /// <returns>O host builder configurado</returns>
    public static IServiceCollection AddSerilog(
        this IServiceCollection hostBuilder,
        IConfiguration configuration,
        AnsiConsoleTheme? consoleTheme = null)
    {
        return hostBuilder.AddSerilog(config =>
        {
            config.WriteTo.Console(theme: AnsiConsoleTheme.Sixteen)
                    .ReadFrom.Configuration(configuration);

        }); 
    }
}