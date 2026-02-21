using DotNetEnv;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using System.Text;

namespace KerberosServer.RabbitWorker
{
    internal class EmitLogTopic
    {
        public static async Task Run(string[] args)
        {

            string envPath = Path.Combine(Directory.GetCurrentDirectory(), "Rabbit.env");
            // 1. Загружаем .env + переменные окружения
            //Env.Load(); // загружает .env из текущей директории
            Env.Load(envPath);
            var config = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();

            // 2. Читаем все нужные параметры
            string hostName = config["RABBITMQ_HOST"] ?? "localhost";
            string portStr = config["RABBITMQ_PORT"] ?? "5672";
            string username = config["RABBITMQ_USERNAME"] ?? "guest";
            string password = config["RABBITMQ_PASSWORD"] ?? "guest";
            string virtualHost = config["RABBITMQ_VIRTUAL_HOST"] ?? "/";

            string exchangeName = config["KERBEROS_EXCHANGE_NAME"] ?? "kerberos.exchange";
            string topicPattern = config["KERBEROS_TOPIC_PATTERN"] ?? "kerberos.client.#";
            string queueName = config["KERBEROS_QUEUE_NAME"] ?? "kdc.requests";

            string realm = config["KDC_REALM"] ?? "EXAMPLE.COM";

            var factory = new ConnectionFactory
            {
                HostName = hostName,
                UserName = username,
                Password = password,
                Port = int.Parse(portStr),
            };
            using var connection = await factory.CreateConnectionAsync();
            using var channel = await connection.CreateChannelAsync();


            await channel.ExchangeDeclareAsync(exchange: "topic_logs", type: ExchangeType.Topic, durable: true);

            var routingKey = (args.Length > 0) ? args[0] : "anonymous.info";
            var message = (args.Length > 1) ? string.Join(" ", args.Skip(1).ToArray()) : "Hello World!";
            var body = Encoding.UTF8.GetBytes(message);
            await channel.BasicPublishAsync(exchange: "topic_logs", routingKey: routingKey, body: body);
            Console.WriteLine($" [x] Sent '{routingKey}':'{message}'");
        }
    }
}