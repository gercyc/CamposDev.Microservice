namespace CamposDev.Microservice.RabbitMq.Extensions;

using System.Reflection;
using CamposDev.Microservice.RabbitMq.Contracts;
using Microsoft.Extensions.DependencyInjection;

public static class AmqpExtensions
{
    public static void AddEventHandlersFrom(this IServiceCollection services, Assembly assembly)
    {
        var handlerType = typeof(IMessageHandler);
        var types = assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && handlerType.IsAssignableFrom(t));

        foreach (var t in types)
        {
            services.AddScoped(handlerType, t); // registra como IMessageHandler
        }
    }
    public static bool Matches(string pattern, string routingKey)
    {
        var p = pattern.Split('.');
        var r = routingKey.Split('.');
        int i = 0, j = 0;

        while (i < p.Length && j < r.Length)
        {
            if (p[i] == "#") return true;
            if (p[i] != "*" && p[i] != r[j]) return false;
            i++;
            j++;
        }

        if (i == p.Length && j == r.Length) return true;
        if (i == p.Length - 1 && p[i] == "#") return true;
        return false;
    }
    
}