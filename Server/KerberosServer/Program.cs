using DotNetEnv;
using KerberosServer.BasicRabbit;
using KerberosServer.KDC;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace KerberosKdcSimple
{
    class Program
    {
        private static readonly byte[] KeyAlice = { 0x06, 0xbd, 0xf0, 0xf9, 0xb7, 0x32, 0x97, 0x6f, 0xd8, 0x25, 0x23, 0xb1, 0xf0, 0xee, 0x19, 0x30 };
        private static readonly byte[] KeyBob = { 0xf8, 0x0b, 0x68, 0x2d, 0xdb, 0x63, 0xfc, 0x6f, 0xcb, 0x94, 0x05, 0xc0, 0x70, 0x7c, 0x86, 0x96 };
        

        static async Task Main(string[] args)
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
            string ttl = config["MESSAGE_TTL"] ?? "5";
            string replyTopicPattern = config["REPLY_TOPIC_PATTERN"] ?? "kerberos.client.#.reply";
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

            await channel.QueueBindAsync(queue: queueName, exchange: "kerberos.exchange", routingKey: topicPattern);
            Console.WriteLine(DateTime.UtcNow);

            Console.WriteLine(" [*] Waiting for messages. To exit press CTRL+C");

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (model, ea) =>
            {
                Console.WriteLine(topicPattern);
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                var routingKey = ea.RoutingKey;
                Console.WriteLine($" [x] Received '{routingKey}':'{message}'");


                MessageBody messageBody;
                try
                {
                    messageBody = new MessageBody(message);
                    if (messageBody.Date - DateTime.UtcNow < TimeSpan.FromMinutes(double.Parse(ttl)))//Проверка, что время между отправкой и получением менее 5 минут
                    {
                        /*
                            Парсим сообщение
                            Проверяем временную метку<5 минут по стандарту
                            формируем пакет для Алисы и Бобика, шифруем соответствующими ключами
                        */

                        //парсим на части
                        var msg = messageBody.Body;
                        var parts = msg.Split(',');
                        if (parts.Length == 2)//Первое подключение, где Алиса говорит, что хочет поговорить с Бобиком
                        {
                            Console.WriteLine("Count:2\n");
                            string from = parts[0].Trim().ToLower();
                            string to = parts[1].Trim().ToLower();
                            //Далее идет отправка сообщения назад

                            //Собираем сообщение для отправки назад
                            DateTime dateServ = DateTime.UtcNow;
                            string messageTTL = ttl;
                            byte[] sessionKey = KerberosCrypto.GenerateSessionKey();
                            string BackMessage = dateServ+","+messageTTL+","+sessionKey.ToString();

                            string EncryptedAlice = KerberosCrypto.Encrypt(BackMessage + "," + to,KeyAlice);
                            string EncryptedBob = KerberosCrypto.Encrypt(BackMessage + "," + from, KeyBob);
                            //Отправка сообщения назад

                            string ReplyroutingKey = $"kerberos.client.{parts[0]}.Reply";
                            channel.BasicPublishAsync("kerberos.exchange", routingKey, body);


                        }
                        else if (parts.Length == 4)//Добавлен тип аутентификации, автор сообщения
                        {
                            string messageType = parts[0].Trim().ToLower();//тип
                            string sessionKey = parts[1].Trim().ToLower();//автор
                            string MessageTrom = parts[2].Trim().ToLower();
                            string MessageTo = parts[3].Trim().ToLower();
                            KerberosCrypto.Decrypt(MessageTrom, KeyAlice);//Поправить на подстановку исходя

                        }
                    }

                }
                catch(Exception e) { Console.WriteLine(e.Message); }

                return Task.CompletedTask;

            };

            await channel.BasicConsumeAsync(queueName, autoAck: true, consumer: consumer);
            Console.ReadLine();
        }
        
    }
    class MessageBody
    {
        internal DateTime Date;
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