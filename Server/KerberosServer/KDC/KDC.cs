using DotNetEnv;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KerberosServer.KDC
{
    internal class KDC
    {
        public static async Task GetKDCAsync()
        {
            Console.WriteLine("Kerberos KDC - простой сервер RabbitMQ (один файл)");
            Console.WriteLine("Запуск... Ctrl+C для выхода\n");

            string envPath = Path.Combine(Directory.GetCurrentDirectory(), "Rabbit.env");
            // 1. Загружаем .env + переменные окружения
            //Env.Load(); // загружает .env из текущей директории
            Env.Load(envPath);
            var config = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();

            // ─────────────────────────────────────────────── 
            string hostName = config["RABBITMQ_HOST"] ?? "localhost";
            string portStr = config["RABBITMQ_PORT"] ?? "5672";
            string username = config["RABBITMQ_USERNAME"] ?? "guest";
            string password = config["RABBITMQ_PASSWORD"] ?? "guest";
            string virtualHost = config["RABBITMQ_VIRTUAL_HOST"] ?? "/";
            string exchangeName = config["KERBEROS_EXCHANGE_NAME"] ?? "kerberos.exchange";
            string topicPattern = config["KERBEROS_TOPIC_PATTERN"] ?? "kerberos.client.#";
            //string queueName = config["KERBEROS_QUEUE_NAME"] ?? "kdc.requests";
            // ───────────────────────────────────────────────

            int port = int.TryParse(portStr, out int p) ? p : 5672;

            var factory = new ConnectionFactory
            {
                HostName = hostName,
                Port = port,
                UserName = username,
                Password = password,
                VirtualHost = virtualHost,
                AutomaticRecoveryEnabled = true,

            };

            using var connection = await factory.CreateConnectionAsync();
            using var channel = await connection.CreateChannelAsync();

            await channel.ExchangeDeclareAsync(exchange: exchangeName, type: ExchangeType.Topic, durable: true);

            // declare a server-named queue
            QueueDeclareOk queueDeclareResult = await channel.QueueDeclareAsync();
            string queueName = queueDeclareResult.QueueName;

            await channel.QueueBindAsync(queue: queueName, exchange: "topic_logs", routingKey: topicPattern);
            

            Console.WriteLine(" [*] Waiting for messages. To exit press CTRL+C");

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (model, ea) =>
            {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                var routingKey = ea.RoutingKey;
                Console.WriteLine($" [x] Received '{routingKey}':'{message}'");
                return Task.CompletedTask;
            };

            await channel.BasicConsumeAsync(queueName, autoAck: true, consumer: consumer);
            Console.ReadLine();
        }
        
    }
}
