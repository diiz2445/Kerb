using DotNetEnv;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
namespace ClientBob
{
    public class Program
    {
        //private static readonly byte[] KeyBob = { 0xf8, 0x0b, 0x68, 0x2d, 0xdb, 0x63, 0xfc, 0x6f, 0xcb, 0x94, 0x05, 0xc0, 0x70, 0x7c, 0x86, 0x96 };
        public static Dictionary<string, byte[]> ConnectedChats = new Dictionary<string, byte[]>();
        static async Task Main(string[] args)
        {
            
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
            string ChatRoutingKey = "kerberos.chat.bob";
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

            await channel.QueueBindAsync(queue: queueName, exchange: exchangeName, routingKey: ReplyroutingKey);
            await channel.QueueBindAsync(
                                        queue: queueName,
                                        exchange: exchangeName,
                                        routingKey: ChatRoutingKey                // kerberos.chat.alice
            );


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
                if (routingKey == ReplyroutingKey)
                {
                    
                    string[] ParsedMessage = Recievedmessage.Split(",");
                    string MyRecievdData = KerberosCrypto.Decrypt(ParsedMessage[1], parsedKey);

                    string[] MyData = MyRecievdData.Split(',');
                    Console.WriteLine(MyRecievdData);

                    Byte[] SessionKey = Convert.FromBase64String(MyData[2]);


                    ConnectedChats.Add(MyData[3], SessionKey);
                    string ToBob = "TEST_MESSAGE";
                    string msg = KerberosCrypto.Encrypt(ToBob,SessionKey);
                    Publish(channel, MyData[3], msg);
                }
                if (routingKey == ChatRoutingKey)
                {
                    Console.WriteLine();
                    //ChatMessage.PrintMessage(Recievedmessage, ConnectedChats);
                    ChatMessage message = new ChatMessage(Recievedmessage);
                    ChatMessage.PrintMessage(Recievedmessage, ConnectedChats);
                }
                return Task.CompletedTask;
            };

            await channel.BasicConsumeAsync(queueName, autoAck: true, consumer: consumer);
            Console.ReadLine();
        }


        public static byte[] ParseHexStringWith0x(string input)
        {
            var cleaned = input
                .Replace("0x", "")
                .Replace(" ", "")
                .Replace(",", "");

            return Convert.FromHexString(cleaned);
        }
        public static void Publish(IChannel channel,string to,string message)
        {
            string RoutingChatKey = "kerberos.chat." + to;
            byte[] messageToSend = Encoding.UTF8.GetBytes(ChatMessage.ChatMessageString(message,"bob"));
            channel.BasicPublishAsync("kerberos.exchange", RoutingChatKey, messageToSend);
        }
    }

    public class ChatMessage
    {
        string from;
        DateTime time;
        string encryptedMessage;
        public ChatMessage(string message)
        {
            string[] Temp = message.Split('|');
            from = Temp[0];
            time = DateTime.Parse(Temp[1]);
            encryptedMessage = Temp[4];
        }
        public static string ChatMessageString(string message,string From)
        {
            DateTime time = DateTime.UtcNow;
            string from = From;
            return $"{from}|{time}|{message}";
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

        // ====================== ЧАТ ======================
        public static string EncryptChat(string message, byte[] sessionKey) => Encrypt(message, sessionKey);
        public static string DecryptChat(string encMessage, byte[] sessionKey) => Decrypt(encMessage, sessionKey);
    }
}
