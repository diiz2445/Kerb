using DotNetEnv;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Client
{
    public class Program
    {
        public static Dictionary<string, byte[]> ConnectedChats = new Dictionary<string, byte[]>();
        static async Task Main(string[] args)
        {
            Console.WriteLine("Клиент для Kerberso");
            Console.WriteLine("Запуск... Ctrl+C для выхода\n");

            Dictionary<string, byte[]> ConnectedChats= new Dictionary<string, byte[]>();

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
            string ReplyroutingKey = $"kerberos.client.Reply.alice";
            string ChatRoutingKey = "kerberos.chat.alice";
            byte[] parsedKey = ParseHexStringWith0x(config["CRYPT_KEY"]);
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

            await channel.QueueBindAsync(queue: queueName, exchange: exchangeName, routingKey:ReplyroutingKey );
            await channel.QueueBindAsync(
                                        queue: queueName,
                                        exchange: exchangeName,
                                        routingKey: ChatRoutingKey                // kerberos.chat.alice
            );

            Console.WriteLine(DateTime.UtcNow);

            string body = $"23.02.2026 18:22:12|alice,bob";//Сообщение для KDC о желании поговорить с Бобиком
            string To = "bob";
            byte[] bytes = Encoding.UTF8.GetBytes( body );
            string TopicName = topicPattern + "alice";
            Thread.Sleep(2000);
            channel.BasicPublishAsync(exchangeName, routingKey: TopicName,body:bytes);
            Console.WriteLine($"Message Published at {ReplyroutingKey}");



            //Получаем сообщение
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (model, ea) =>
            {
                Console.WriteLine(topicPattern);
                var body = ea.Body.ToArray();
                var Recievedmessage = Encoding.UTF8.GetString(body);
                var routingKey = ea.RoutingKey;
                Console.WriteLine($" [x] Received '{routingKey}':'{Recievedmessage}'");

                if (routingKey == ReplyroutingKey)//Если ответ от KDC
                {
                    string[] ParsedMessage = Recievedmessage.Split(",");
                    string MyRecievdData = KerberosCrypto.Decrypt(ParsedMessage[0], parsedKey);

                    string[] MyData = MyRecievdData.Split(',');
                    Console.WriteLine(MyRecievdData);

                    byte[] SessionKey = Convert.FromBase64String(MyData[2]);


                    //Готовим сообщение для Боба
                    string SessionPart = KerberosCrypto.Encrypt(MyData[0] + ",Alice", SessionKey);
                    string MessageForBob = SessionPart + "," + ParsedMessage[1];
                    byte[] bytesMessageForBob = Encoding.UTF8.GetBytes(MessageForBob);

                    //Публикуем Бобу сообщение
                    channel.BasicPublishAsync(exchangeName, "kerberos.client.Reply.bob", bytesMessageForBob);

                    try { ConnectedChats.Add(To, SessionKey); }
                    catch { }

                }
                if(routingKey==ChatRoutingKey)
                {
                    Console.WriteLine();
                    
                    ChatMessage message = new ChatMessage(Recievedmessage);
                    ChatMessage.PrintMessage(Recievedmessage, ConnectedChats);

                    string NewMessage = ChatMessage.ChatMessageString(KerberosCrypto.Encrypt("I see you", ConnectedChats[message.from]), "alice");
                    Publish(channel, message.from,"alice", NewMessage);
                }

                    return Task.CompletedTask;
            };

            await channel.BasicConsumeAsync(queueName, autoAck: true, consumer: consumer);
            Console.ReadLine();
        }
        public static void Publish(IChannel channel, string to, string from, string message)
        {
            string RoutingChatKey = "kerberos.chat." + to;
            byte[] messageToSend = Encoding.UTF8.GetBytes(ChatMessage.ChatMessageString(message, from));
            channel.BasicPublishAsync("kerberos.exchange", RoutingChatKey, messageToSend);
        }
        public static byte[] ParseHexStringWith0x(string input)
        {
            var cleaned = input
                .Replace("0x", "")
                .Replace(" ", "")
                .Replace(",", "");

            return Convert.FromHexString(cleaned);
        }
    }


    public class ChatMessage
    {
        public string from;
        public DateTime time;
        string encryptedMessage;
        public ChatMessage(string message)
        {
            string[] Temp = message.Split('|');
            from = Temp[0];
            time = DateTime.Parse(Temp[1]);
            encryptedMessage = Temp[2];
        }
        public static string ChatMessageString(string message, string to)
        {
            DateTime time = DateTime.UtcNow;
            string To = to;
            return $"{To}|{time}|{message}";
        }
        public static string ChatMessageSendString(string message, string to)
        {
            DateTime time = DateTime.UtcNow;
            string To = to;
            return $"{To}|{time}|{message}";
        }
        public static void PrintMessage(string message, Dictionary<string, byte[]> Keys)
        {
            ChatMessage chatMessage = new ChatMessage(message);
            string decryptedMessage = KerberosCrypto.Decrypt(chatMessage.encryptedMessage, Keys[chatMessage.from]);
            Console.WriteLine($"{chatMessage.time.ToString()} {chatMessage.from}: {decryptedMessage}");
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

        
    }
}
