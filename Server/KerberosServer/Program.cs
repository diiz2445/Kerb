using DotNetEnv;
using KerberosServer.BasicRabbit;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Text;
using System.Threading;

namespace KerberosKdcSimple
{
    class Program
    {
        static async Task Main(string[] args)
        {
            //await ReceiveLogsTopic.Run(["#"]);


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
            string queueName = config["KERBEROS_QUEUE_NAME"] ?? "kdc.requests";
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

            // Объявляем exchange
            channel.ExchangeDeclareAsync(exchangeName, "topic", durable: true, autoDelete: false);

            // Очередь для входящих запросов от клиентов
            channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false);

            // Биндим очередь на routing key для запросов
            channel.QueueBindAsync(queueName, exchangeName, "kerberos.request");

            // Потребитель
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (model, ea) =>
            {
                try
                {
                    var body = ea.Body.ToArray();
                    string message = Encoding.UTF8.GetString(body);

                    // Пытаемся понять, от кого пришло 
                    string clientId = ea.BasicProperties.ReplyTo ?? "unknown-client";

                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Запрос от {clientId,-12} → {message}");

                    // Здесь должна быть логика Kerberos (AS-REQ → AS-REP и т.д.)
                    // Для примера — просто эхо с префиксом
                    string responseText = $"KDC answer for {clientId}: ticket={Guid.NewGuid():N}";


                    // Отправляем ответ конкретному клиенту
                    string replyRoutingKey = $"kerberos.client.{clientId}";

                    channel.BasicPublishAsync(
                        exchange: exchangeName,
                        routingKey: replyRoutingKey,
                        body: Encoding.UTF8.GetBytes(responseText));

                    Console.WriteLine($"          Ответ → {replyRoutingKey,-25}  {responseText}");

                    channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка обработки: {ex.Message}");
                    try { channel.BasicNackAsync(ea.DeliveryTag, false, true); }
                    catch { }
                }
            };

            channel.BasicConsumeAsync(queueName, autoAck: false, consumer);

            Console.WriteLine($"Слушаем очередь: {queueName}");
            Console.WriteLine($"Exchange:     {exchangeName} (topic)");
            Console.WriteLine($"Routing для запросов: kerberos.request");
            Console.WriteLine($"Routing для ответов:  kerberos.client.<clientId>\n");

            // Держим процесс живым
            Console.CancelKeyPress += (s, e) =>
            {
                Console.WriteLine("\nОстановка сервера...");
                e.Cancel = true;
            };

            while (true)
            {
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                    break;

                Thread.Sleep(300);
            }

            Console.WriteLine("Завершение работы.");
        }
    }
}