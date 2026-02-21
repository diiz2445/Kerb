using DotNetEnv;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Text;
using System.Text.Json;

namespace KerberosOverRabbitMq.Client
{
    class Client
    {
        

        public static async void Run()
        {
            string ExchangeName = "kerberos.exchange";
            string MyPrincipal = "alice";
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

            channel.ExchangeDeclareAsync(ExchangeName, "topic", durable: true, autoDelete: false);

            // Вариант 3 — если метод уже async и ты хочешь минимум строк
            string replyQueue = (await channel.QueueDeclareAsync($"client.{MyPrincipal}.replies",
                durable: false,
                exclusive: true,
                autoDelete: true)).QueueName;

            channel.QueueBindAsync(replyQueue, ExchangeName, $"kerberos.client.{MyPrincipal}");

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (model, ea) =>
            {
                var body = ea.Body.ToArray();
                string msg = Encoding.UTF8.GetString(body);
                Console.WriteLine($"[Ответ от KDC] {msg}");
            };

            channel.BasicConsumeAsync(replyQueue, autoAck: true, consumer);

            Console.WriteLine($"Клиент {MyPrincipal} готов. Вводите команды:");

            while (true)
            {
                Console.Write("> ");
                string input = Console.ReadLine()?.Trim().ToLower();

                if (string.IsNullOrEmpty(input)) continue;
                if (input == "exit") break;

                string messageType;
                if (input == "as") messageType = "AS-REQ";
                else if (input == "tgs") messageType = "TGS-REQ";
                else if (input == "ap") messageType = "AP-REQ";
                else
                {
                    Console.WriteLine("Команды: as | tgs | ap | exit");
                    continue;
                }

                var payload = new
                {
                    type = messageType,
                    principal = MyPrincipal,
                    realm = "EXAMPLE.COM",
                    timestamp = DateTime.UtcNow.ToString("o"),
                    // Здесь в реальности — ASN.1 + шифрование
                };

                string json = JsonSerializer.Serialize(payload);
                var body = Encoding.UTF8.GetBytes(json);

                
                string routingKey = $"kerberos.client.{MyPrincipal}";
                channel.BasicPublishAsync(ExchangeName, routingKey, body);

                Console.WriteLine($"Отправлен {messageType} → {routingKey}");
            }
        }
    }
}