using DotNetEnv;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
//using SimpleKerberos;

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
                durable: true,
                exclusive: true,
                autoDelete: true)).QueueName;

            channel.QueueBindAsync(replyQueue, ExchangeName, $"kerberos.client.{MyPrincipal}");

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (model, ea) =>
            {
                var body = ea.Body.ToArray();
                string msg = Encoding.UTF8.GetString(body);
                Console.WriteLine($"[Ответ от KDC] {msg}");
                channel.BasicAckAsync(deliveryTag: ea.DeliveryTag, multiple: false);
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
    

namespace SimpleKerberosClient
    {
        class Program
        {
            static void Main(string[] args)
            {
                Console.OutputEncoding = Encoding.UTF8;

                //var kdc = new KerberosKDC();

                string user = "alice@EXAMPLE.COM";
                string pass = "alicepass123";
                string service = "HTTP/web.example.com@EXAMPLE.COM";

                Console.WriteLine($"Клиент: {user}");
                Console.WriteLine($"Цель  : {service}\n");

                byte[] longTermKey = DeriveKey(pass);

                // ─── 1. Получение TGT ────────────────────────────────────────
                var asReq = new RequestJson
                {
                    Client = user,
                    Service = "krbtgt/EXAMPLE.COM@EXAMPLE.COM",
                    PreAuth = Convert.ToBase64String(
                        Encrypt(DateTime.UtcNow.ToString("o"), longTermKey))
                };

                byte[] asReqBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(asReq));
                byte[] asRespBytes = kdc.ProcessASRequest(asReqBytes);

                var asResp = JsonSerializer.Deserialize<Response>(Encoding.UTF8.GetString(asRespBytes));
                if (!asResp!.Success)
                {
                    Console.WriteLine("AS ошибка: " + asResp.Error);
                    return;
                }

                Console.WriteLine("TGT получен");

                var tgtReply = JsonSerializer.Deserialize<ReplyPart>(
                    Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(asResp.Data!), longTermKey)));

                byte[] tgtTicket = tgtReply!.Ticket;
                byte[] keyTG = tgtReply.SessionKey;

                // ─── 2. Получение сервисного билета ──────────────────────────
                var auth = new Authenticator { Time = DateTime.UtcNow };
                byte[] encAuth = Encrypt(JsonSerializer.Serialize(auth), keyTG);

                var tgsReq = new RequestJson
                {
                    TGT = Convert.ToBase64String(tgtTicket),
                    Authenticator = Convert.ToBase64String(encAuth),
                    Service = service
                };

                byte[] tgsReqBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tgsReq));
                byte[] tgsRespBytes = kdc.ProcessTGSRequest(tgsReqBytes);

                var tgsResp = JsonSerializer.Deserialize<Response>(Encoding.UTF8.GetString(tgsRespBytes));
                if (!tgsResp!.Success)
                {
                    Console.WriteLine("TGS ошибка: " + tgsResp.Error);
                    return;
                }

                Console.WriteLine("Сервисный билет получен");

                var serviceReply = JsonSerializer.Deserialize<ReplyPart>(
                    Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(tgsResp.Data!), keyTG)));

                Console.WriteLine("\nУспех!");
                Console.WriteLine($"Сессионный ключ с сервисом: {Convert.ToBase64String(serviceReply!.SessionKey)[..16]}...");
                Console.WriteLine("Теперь можно отправить AP-REQ сервису");
            }

            // ─── Вспомогательные методы клиента (должны совпадать с сервером) ───

            private static byte[] DeriveKey(string password)
            {
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                return sha256.ComputeHash(Encoding.UTF8.GetBytes(password + "@EXAMPLE.COM"));
            }

            private static byte[] Encrypt(string plaintext, byte[] key)
            {
                byte[] data = Encoding.UTF8.GetBytes(plaintext);
                using var aes = System.Security.Cryptography.Aes.Create();
                aes.Key = key;
                aes.GenerateIV();
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;

                using var ms = new MemoryStream();
                ms.Write(aes.IV, 0, aes.IV.Length);

                using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    cs.Write(data, 0, data.Length);

                return ms.ToArray();
            }

            private static byte[] Decrypt(byte[] data, byte[] key)
            {
                if (data.Length < 16) throw new Exception("Неверные данные");

                byte[] iv = data.AsSpan(0, 16).ToArray();
                using var aes = System.Security.Cryptography.Aes.Create();
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;

                using var ms = new MemoryStream(data, 16, data.Length - 16);
                using var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
                using var result = new MemoryStream();
                cs.CopyTo(result);
                return result.ToArray();
            }
        }

        public class Response
        {
            public bool Success { get; set; }
            public string? Data { get; set; }
            public string? Error { get; set; }
        }
        
        // ───────────────────────────────────────────────
        // Криптография
        // ───────────────────────────────────────────────
        class Help
        {
            private byte[] Encrypt(byte[] plaintext, byte[] key)
            {
                using var aes = Aes.Create();
                aes.Key = key;
                aes.GenerateIV();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var ms = new MemoryStream();
                ms.Write(aes.IV, 0, aes.IV.Length);

                using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    cs.Write(plaintext, 0, plaintext.Length);

                return ms.ToArray();
            }

            private byte[] Decrypt(byte[] ciphertext, byte[] key)
            {
                if (ciphertext.Length < 16) throw new Exception("Слишком короткие данные");

                byte[] iv = ciphertext[..16];
                using var aes = Aes.Create();
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var ms = new MemoryStream(ciphertext, 16, ciphertext.Length - 16);
                using var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
                using var result = new MemoryStream();
                cs.CopyTo(result);
                return result.ToArray();
            }

            private byte[] DeriveKey(string password)
            {
                using var sha256 = SHA256.Create();
                return sha256.ComputeHash(Encoding.UTF8.GetBytes(password + "@EXAMPLE.COM"));
            }

            private bool ValidatePreAuth(string user, byte[] encTs, byte[] key)
            {
                try
                {
                    var ts = Encoding.UTF8.GetString(Decrypt(encTs, key));
                    var time = DateTime.Parse(ts);
                    return Math.Abs((DateTime.UtcNow - time).TotalMinutes) <= 5;
                }
                catch { return false; }
            }

            private bool ValidateAuthenticator(byte[] encAuth, byte[] sessionKey)
            {
                try
                {
                    var json = Encoding.UTF8.GetString(Decrypt(encAuth, sessionKey));
                    var auth = JsonSerializer.Deserialize<Authenticator>(json);
                    return Math.Abs((DateTime.UtcNow - auth!.Time).TotalMinutes) <= 5;
                }
                catch { return false; }
            }

            private static byte[] SuccessResponseBytes(string base64Data) =>
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Success = true, Data = base64Data }));

            private static byte[] ErrorResponseBytes(string msg) =>
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Success = false, Error = msg }));
        }

    // Общие модели
    public class RequestJson
    {
        public string? Client { get; set; }
        public string? Service { get; set; }
        public string? PreAuth { get; set; }        // base64
        public string? TGT { get; set; }           // base64
        public string? Authenticator { get; set; } // base64
    }

    public class ReplyPart
    {
        public byte[] SessionKey { get; set; } = Array.Empty<byte>();
        public byte[] Ticket { get; set; } = Array.Empty<byte>();
        public DateTime Start { get; set; }
        public DateTime End { get; set; }

        public byte[] ToJsonBytes() => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this));
    }

    public class TicketContent
    {
        public string? Client { get; set; }
        public byte[] SessionKey { get; set; } = Array.Empty<byte>();
        public string? Service { get; set; }
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public DateTime RenewTill { get; set; }

        public byte[] ToJsonBytes() => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this));
    }

    public class Authenticator
    {
        public DateTime Time { get; set; } = DateTime.UtcNow;
    }
}
}