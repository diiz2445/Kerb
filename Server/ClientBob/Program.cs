using DotNetEnv;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
namespace ClientBob
{
    internal class Program
    {
        private static readonly byte[] KeyBob = { 0xf8, 0x0b, 0x68, 0x2d, 0xdb, 0x63, 0xfc, 0x6f, 0xcb, 0x94, 0x05, 0xc0, 0x70, 0x7c, 0x86, 0x96 };

        static async Task Main(string[] args)
        {
            bool Connected = false;
            Console.WriteLine("Клиент для Kerberso");
            Console.WriteLine("Запуск... Ctrl+C для выхода\n");

            string envPath = Path.Combine(Directory.GetCurrentDirectory(), "RabbitClient.env");
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
            string topicPattern = config["KERBEROS_TOPIC_PATTERN"] ?? "kerberos.client.Forward.";
            string ttl = config["MESSAGE_TTL"] ?? "5";
            string replyTopicPattern = config["REPLY_TOPIC_PATTERN"] ?? "kerberos.client.#.reply";
            string ReplyroutingKey = $"kerberos.client.Reply.bob";
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

            QueueDeclareOk queueDeclareResult = await channel.QueueDeclareAsync();
            string queueName = queueDeclareResult.QueueName;

            await channel.QueueBindAsync(queue: queueName, exchange: exchangeName, routingKey: ReplyroutingKey);
            Console.WriteLine(DateTime.UtcNow);

            
           
            string TopicName = topicPattern + "bob";
            Thread.Sleep(3000);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (model, ea) =>
            {
                Console.WriteLine(topicPattern);
                var body = ea.Body.ToArray();
                var Recievedmessage = Encoding.UTF8.GetString(body);
                var routingKey = ea.RoutingKey;
                Console.WriteLine($" [x] Received '{routingKey}':'{Recievedmessage}'");

                string[] ParsedMessage = Recievedmessage.Split(",");
                string MyRecievdData = KerberosCrypto.Decrypt(ParsedMessage[1], KeyBob);

                string[] MyData = MyRecievdData.Split(',');
                Console.WriteLine(MyRecievdData);

                Byte[] SessionKey = Convert.FromBase64String(MyData[2]);
                return Task.CompletedTask;
            };

            await channel.BasicConsumeAsync(queueName, autoAck: true, consumer: consumer);
            Console.ReadLine();
        }

    }

    class MessageBody
    {
        internal DateTime Date;
        internal string From;
        internal string Body;

        public MessageBody(string message)
        {
            string[] strings = message.Split('|');
            this.Date = DateTime.Parse(strings[0]);
            this.Body = strings[1];
        }
    }

    public static class KerberosCrypto
    {
        public static string Encrypt(string plainText, byte[] key)
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            byte[] ciphertext = new byte[plainBytes.Length];
            byte[] tag = new byte[16];

            using var aes = new AesGcm(key);
            aes.Encrypt(nonce, plainBytes, ciphertext, tag);

            byte[] result = new byte[12 + 16 + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, result, 0, 12);
            Buffer.BlockCopy(tag, 0, result, 12, 16);
            Buffer.BlockCopy(ciphertext, 0, result, 28, ciphertext.Length);

            return Convert.ToBase64String(result);
        }

        public static string Decrypt(string base64Cipher, byte[] key)
        {
            byte[] combined = Convert.FromBase64String(base64Cipher);
            byte[] nonce = new byte[12];
            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[combined.Length - 28];

            Buffer.BlockCopy(combined, 0, nonce, 0, 12);
            Buffer.BlockCopy(combined, 12, tag, 0, 16);
            Buffer.BlockCopy(combined, 28, ciphertext, 0, ciphertext.Length);

            byte[] plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return Encoding.UTF8.GetString(plaintext);
        }

        public static byte[] GenerateSessionKey()
        {
            byte[] key = new byte[16];
            RandomNumberGenerator.Fill(key);
            return key;
        }

        // ====================== ТИКЕТЫ ======================
        public static string EncryptTicket(double ts, int lifetime, byte[] sessionKey, string target, byte[] longTermKey)
        {
            var ticket = new
            {
                ts = ts,
                lt = lifetime,
                sk = Convert.ToBase64String(sessionKey),
                tgt = target
            };
            string json = JsonSerializer.Serialize(ticket);
            return Encrypt(json, longTermKey);
        }

        public static (double Ts, int Lt, byte[] SessionKey, string Target) DecryptTicket(string encTicket, byte[] longTermKey)
        {
            string json = Decrypt(encTicket, longTermKey);
            var t = JsonSerializer.Deserialize<JsonElement>(json);
            return (
                t.GetProperty("ts").GetDouble(),
                t.GetProperty("lt").GetInt32(),
                Convert.FromBase64String(t.GetProperty("sk").GetString()!),
                t.GetProperty("tgt").GetString()!
            );
        }

        // ====================== АУТЕНТИФИКАТОР ======================
        public static string EncryptAuthenticator(string clientName, double ts, byte[] sessionKey)
        {
            var auth = new { client = clientName, ts = ts };
            return Encrypt(JsonSerializer.Serialize(auth), sessionKey);
        }

        public static (string ClientName, double Ts) DecryptAuthenticator(string encAuth, byte[] sessionKey)
        {
            string json = Decrypt(encAuth, sessionKey);
            var a = JsonSerializer.Deserialize<JsonElement>(json);
            return (a.GetProperty("client").GetString()!, a.GetProperty("ts").GetDouble());
        }

        // ====================== ЧАТ ======================
        public static string EncryptChat(string message, byte[] sessionKey) => Encrypt(message, sessionKey);
        public static string DecryptChat(string encMessage, byte[] sessionKey) => Decrypt(encMessage, sessionKey);
    }
}
