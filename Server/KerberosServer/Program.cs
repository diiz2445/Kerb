using System;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using DotNetEnv;
using System.Threading.Tasks;

namespace KerberosOverRabbitMq.Kdc
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
             Run();
            Client.Client.Run();
            
            while (true)
            {
            }
        }
        public static async void Run()
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

            if (!int.TryParse(portStr, out int port))
                port = 5672;

            Console.WriteLine("KDC запускается с настройками:");
            Console.WriteLine($"  RabbitMQ: {username}@{hostName}:{port}/{virtualHost}");
            Console.WriteLine($"  Exchange: {exchangeName}");
            Console.WriteLine($"  Queue:    {queueName}");
            Console.WriteLine($"  Pattern:  {topicPattern}");
            Console.WriteLine($"  Realm:    {realm}\n");

            var factory = new ConnectionFactory
            {
                HostName = hostName,
                Port = port,
                UserName = username,
                Password = password,
                VirtualHost = virtualHost,

            };

            using var connection = await factory.CreateConnectionAsync();
            using var channel = await connection.CreateChannelAsync();


            // Очередь для входящих запросов
            await channel.ExchangeDeclareAsync(
                            exchange: "topic_logs",
                            type: ExchangeType.Topic,
                            durable: true); channel.QueueBindAsync(queueName, exchangeName, topicPattern);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (model, ea) =>
            {
                try
                {
                    var body = ea.Body.ToArray();
                    string routingKey = ea.RoutingKey; // kerberos.client.alice

                    if (!routingKey.StartsWith("kerberos.client."))
                    {
                        channel.BasicAckAsync(ea.DeliveryTag, false);
                        return;
                    }

                    string clientPrincipal = routingKey["kerberos.client.".Length..];

                    string message = Encoding.UTF8.GetString(body);
                    Console.WriteLine($"[{routingKey}] ← {message.Trim()}");

                    // Здесь должна быть реальная логика парсинга Kerberos-сообщений
                    string responseType = "ERROR";
                    string responseContent = "Неизвестный запрос";

                    if (message.Contains("AS-REQ"))
                    {
                        responseType = "AS-REP";
                        responseContent = $"TGT выдан для {clientPrincipal} @ {realm} (демо)";
                    }
                    else if (message.Contains("TGS-REQ"))
                    {
                        responseType = "TGS-REP";
                        responseContent = $"Сервисный тикет выдан для {clientPrincipal} (демо)";
                    }
                    else if (message.Contains("AP-REQ"))
                    {
                        responseType = "AP-REP";
                        responseContent = "Взаимная аутентификация подтверждена (демо)";
                    }

                    var responseObj = new
                    {
                        type = responseType,
                        from = "KDC",
                        to = clientPrincipal,
                        realm = realm,
                        content = responseContent,
                        timestamp = DateTime.UtcNow.ToString("o")
                    };

                    string json = JsonSerializer.Serialize(responseObj);
                    var replyBody = Encoding.UTF8.GetBytes(json);

                    string replyRoutingKey = $"kerberos.client.{clientPrincipal}";

                    await channel.BasicPublishAsync(
                        exchange: exchangeName,
                        routingKey: replyRoutingKey,
                        body: replyBody);

                    Console.WriteLine($" → {responseType} отправлен клиенту {clientPrincipal}");

                    channel.BasicAckAsync(ea.DeliveryTag, false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Ошибка обработки: {ex.Message}");
                    channel.BasicAckAsync(ea.DeliveryTag, false);
                }

                await Task.CompletedTask;
            };

            await channel.BasicConsumeAsync(queueName, autoAck: false, consumer);

            Console.WriteLine("KDC готов принимать запросы. Нажмите Enter для завершения...");
            Console.ReadLine();
        }

    }
}