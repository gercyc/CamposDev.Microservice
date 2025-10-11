using CamposDev.Microservice.RabbitMq.Contracts;
using CamposDev.Microservice.RabbitMq.Persistence;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace Demo.MicroserviceAspnet.Repositories;

public interface IClienteRepositoryDapper : IScopedService
{
    Task<int> UpdateNomeAsync(long id, string novoNome, CancellationToken ct);
}

public sealed class ClienteRepositoryDapper(IDbConnectionFactory factory) : IClienteRepositoryDapper
{
    public async Task<int> UpdateNomeAsync(long id, string novoNome, CancellationToken ct)
    {
        using var conn = factory.CreateOpen();
        var sql = "update IntegracoesSAPClientes set ChaveSAP = @nome where CodigoCliente = @id";
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { id, nome = novoNome }, cancellationToken: ct));
    }
}