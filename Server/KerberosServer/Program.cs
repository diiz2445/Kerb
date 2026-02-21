using System;
using System.IO;
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
            await RunAsync();
            Console.WriteLine("Нажмите Enter для завершения...");
            Console.ReadLine();
        }

        private static async Task RunAsync()
        {
            // Путь к файлу .env (можно изменить на нужный)
            string envPath = Path.Combine(Directory.GetCurrentDirectory(), "Rabbit.env");

            // Загружаем переменные из .env
            Env.Load(envPath);

            var config = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();

            // Читаем настройки
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
                VirtualHost = virtualHost

            };

            await using var connection = await factory.CreateConnectionAsync("KDC-Demo");
            await using var channel = await connection.CreateChannelAsync();

            // Объявляем exchange (topic)
            await channel.ExchangeDeclareAsync(
                exchange: exchangeName,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                arguments: null);

            // Очередь для запросов к KDC
            await channel.QueueDeclareAsync(
                queue: queueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null);

            await channel.QueueBindAsync(queueName, exchangeName, topicPattern);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (model, ea) =>
            {
                try
                {
                    var body = ea.Body.ToArray();
                    string routingKey = ea.RoutingKey;

                    if (!routingKey.StartsWith("kerberos.client.") || routingKey.Length <= "kerberos.client.".Length)
                    {
                        Console.WriteLine($"Некорректный routing key: {routingKey} → игнорируем");
                        await channel.BasicAckAsync(ea.DeliveryTag, false);
                        return;
                    }

                    string clientPrincipal = routingKey.Substring("kerberos.client.".Length);

                    string message = Encoding.UTF8.GetString(body);
                    Console.WriteLine($"[{routingKey}] ← {message.Trim()}");

                    // Парсим JSON и определяем тип запроса
                    string responseType = "ERROR";
                    string responseContent = "Неизвестный запрос";

                    try
                    {
                        using var doc = JsonDocument.Parse(message);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("type", out var typeProp) &&
                            typeProp.ValueKind == JsonValueKind.String)
                        {
                            string reqType = typeProp.GetString()?.Trim().ToUpperInvariant() ?? "";

                            switch (reqType)
                            {
                                case "AS-REQ":
                                case "AS_REQ":
                                case "ASREQUEST":
                                    responseType = "AS-REP";
                                    responseContent = $"TGT выдан для {clientPrincipal} @ {realm} (демо)";
                                    break;

                                case "TGS-REQ":
                                case "TGS_REQ":
                                case "TGSREQUEST":
                                    responseType = "TGS-REP";
                                    responseContent = $"Сервисный тикет выдан для {clientPrincipal} (демо)";
                                    break;

                                case "AP-REQ":
                                case "AP_REQ":
                                case "APREQUEST":
                                    responseType = "AP-REP";
                                    responseContent = "Взаимная аутентификация подтверждена (демо)";
                                    break;

                                default:
                                    responseContent = $"Неизвестный тип запроса: '{reqType}'";
                                    break;
                            }
                        }
                        else
                        {
                            responseContent = "Отсутствует поле 'type' или оно не строка";
                        }
                    }
                    catch (JsonException jsonEx)
                    {
                        responseContent = $"Невалидный JSON: {jsonEx.Message}";
                    }
                    catch (Exception ex)
                    {
                        responseContent = $"Ошибка обработки: {ex.Message}";
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

                    string jsonResponse = JsonSerializer.Serialize(responseObj);
                    var replyBody = Encoding.UTF8.GetBytes(jsonResponse);

                    string replyRoutingKey = $"kerberos.client.{clientPrincipal}";

                    await channel.BasicPublishAsync(
                        exchange: exchangeName,
                        routingKey: replyRoutingKey,
                        body: replyBody);

                    Console.WriteLine($" → {responseType} отправлен клиенту {clientPrincipal} (routing: {replyRoutingKey})");

                    await channel.BasicAckAsync(ea.DeliveryTag, false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Критическая ошибка обработки: {ex.Message}");
                    await channel.BasicNackAsync(ea.DeliveryTag, false, true);
                }
            };

            await channel.BasicConsumeAsync(queueName, autoAck: false, consumer);

            Console.WriteLine("KDC запущен и слушает запросы...");
        }
    }
}