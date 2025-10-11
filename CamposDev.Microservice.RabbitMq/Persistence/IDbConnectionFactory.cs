namespace CamposDev.Microservice.RabbitMq.Persistence;

using System.Data;

public interface IDbConnectionFactory
{
    IDbConnection Create();          // retorna conexão FECHADA (caller abre quando quiser)
    IDbConnection CreateOpen();      // retorna conexão ABERTA
}