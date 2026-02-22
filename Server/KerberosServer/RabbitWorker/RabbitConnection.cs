//using Microsoft.Extensions.Configuration;
//using RabbitMQ.Client;
//using System;

//public class RabbitConnection : IDisposable
//{
//    private readonly IConnection _connection;
//    public IModel Channel { get; }

//    public RabbitConnection(IConfiguration config)
//    {
//        var factory = new ConnectionFactory
//        {
//            HostName = config["RABBITMQ_HOST"] ?? "localhost",
//            Port = int.Parse(config["RABBITMQ_PORT"] ?? "5672"),
//            UserName = config["RABBITMQ_USERNAME"] ?? "guest",
//            Password = config["RABBITMQ_PASSWORD"] ?? "guest",
//            VirtualHost = config["RABBITMQ_VIRTUAL_HOST"] ?? "/",
//            AutomaticRecoveryEnabled = true
//        };

//        _connection = factory.CreateConnection("KerberosClient");
//        Channel = _connection.CreateModel();

//        // Объявляем exchange один раз (idempotent)
//        Channel.ExchangeDeclare(ExchangeName, "topic", durable: true, autoDelete: false);
//    }

//    public void Dispose()
//    {
//        Channel?.Close();
//        _connection?.Close();
//    }
//}