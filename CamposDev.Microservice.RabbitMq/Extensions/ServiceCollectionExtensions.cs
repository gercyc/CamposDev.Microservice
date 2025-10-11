namespace CamposDev.Microservice.RabbitMq.Extensions;

using System.Reflection;
using CamposDev.Microservice.RabbitMq.Contracts;
using Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra serviços marcados com IScopedService / ITransientService.
    /// Para cada classe concreta marcada, registra todas as interfaces (exceto os marcadores).
    /// Se nenhuma interface for encontrada, pode opcionalmente registrar o próprio tipo (alsoRegisterSelf).
    /// </summary>
    public static IServiceCollection AddServicesFrom(
        this IServiceCollection services,
        Assembly assembly,
        bool alsoRegisterSelf = false,
        Func<Type, bool>? typeFilter = null)
    {
        return services.AddServicesFrom(new[] { assembly }, alsoRegisterSelf, typeFilter);
    }

    public static IServiceCollection AddServicesFrom(
        this IServiceCollection services,
        IEnumerable<Assembly> assemblies,
        bool alsoRegisterSelf = false,
        Func<Type, bool>? typeFilter = null)
    {
        var scopedMarker = typeof(IScopedService);
        var transientMarker = typeof(ITransientService);

        var types = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => scopedMarker.IsAssignableFrom(t) || transientMarker.IsAssignableFrom(t));

        if (typeFilter is not null)
            types = types.Where(typeFilter);

        foreach (var impl in types)
        {
            var isScoped = scopedMarker.IsAssignableFrom(impl);
            var isTransient = transientMarker.IsAssignableFrom(impl);

            if (isScoped && isTransient)
                throw new InvalidOperationException(
                    $"Tipo {impl.FullName} não pode implementar IScopedService e ITransientService ao mesmo tempo.");

            var lifetime = isScoped ? ServiceLifetime.Scoped : ServiceLifetime.Transient;

            var serviceInterfaces = impl.GetInterfaces()
                .Where(i => i != scopedMarker && i != transientMarker)
                .Where(i => !i.IsGenericTypeDefinition)
                .ToArray();

            // Se a classe não expõe interfaces (além dos marcadores), opcionalmente registra o próprio tipo
            if (serviceInterfaces.Length == 0)
            {
                if (alsoRegisterSelf)
                    services.Add(new ServiceDescriptor(impl, impl, lifetime));

                continue;
            }

            // Registra contra todas as interfaces (suporta múltiplas implementações por interface)
            foreach (var itf in serviceInterfaces)
            {
                services.Add(new ServiceDescriptor(itf, impl, lifetime));
            }
        }

        return services;
    }
}