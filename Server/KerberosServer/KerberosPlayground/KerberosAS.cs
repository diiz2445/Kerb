using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KerberosPlayground
{
    /// <summary>
    /// Единый KDC-сервер, который содержит AS и TGS (как в реальном Kerberos)
    /// </summary>
    public class KerberosKDC
    {
        // Хранилище пользователей: principal → долгосрочный ключ
        private readonly Dictionary<string, byte[]> _userKeys = new();

        // Хранилище сервисов: service principal → долгосрочный ключ
        private readonly Dictionary<string, byte[]> _serviceKeys = new();

        // Ключ krbtgt (TGT шифруется именно им)
        private readonly byte[] _krbtgtKey;

        // Зарегистрированные TGT (для простоты храним в памяти)
        // В реальности — база + проверка revocation + timeout
        private readonly Dictionary<string, TicketContent> _activeTGTs = new(); // sessionKey → ticket content

        public KerberosKDC()
        {
            _krbtgtKey = DeriveKey("krbtgt@EXAMPLE.COM-VeryLongAndSecureKey-2025!");

            // Примеры пользователей
            RegisterUser("alice@EXAMPLE.COM", "alicepass123");
            RegisterUser("bob@EXAMPLE.COM", "bobsecret456");

            // Примеры сервисов
            RegisterService("HTTP/web.example.com@EXAMPLE.COM", "service-http-secret-789");
            RegisterService("MSSQLSvc/sql.example.com:1433@EXAMPLE.COM", "sql-service-key-321");
        }

        public void RegisterUser(string principal, string password)
        {
            _userKeys[principal.ToUpperInvariant()] = DeriveKey(password);
        }

        public void RegisterService(string servicePrincipal, string password)
        {
            _serviceKeys[servicePrincipal.ToUpperInvariant()] = DeriveKey(password);
        }

        // ───────────────────────────────────────────────
        // AS часть — выдача TGT
        // ───────────────────────────────────────────────

        public ASResponse RequestTGT(ASRequest req)
        {
            string client = req.ClientPrincipal?.ToUpperInvariant();
            if (string.IsNullOrEmpty(client) || !_userKeys.TryGetValue(client, out byte[] clientKey))
                return Error("Пользователь не найден");

            if (!req.RequestedService.Equals("krbtgt/EXAMPLE.COM@EXAMPLE.COM", StringComparison.OrdinalIgnoreCase))
                return Error("AS выдаёт только TGT");

            // Проверка pre-authentication
            if (!ValidatePreAuth(client, req.PreAuthEncryptedTimestamp, clientKey))
                return Error("Pre-authentication failed");

            byte[] sessionKeyC_TGS = GenerateSessionKey();

            DateTime now = DateTime.UtcNow;
            DateTime end = now.AddHours(10);

            var tgtContent = new TicketContent
            {
                Client = client,
                SessionKey = sessionKeyC_TGS,
                Service = "krbtgt/EXAMPLE.COM@EXAMPLE.COM",
                StartTime = now,
                EndTime = end,
                RenewTill = end.AddDays(7)
            };

            // Сохраняем TGT в "базе" (по session key как идентификатору)
            string tgtId = Convert.ToBase64String(sessionKeyC_TGS);
            _activeTGTs[tgtId] = tgtContent;

            byte[] encryptedTGT = Encrypt(tgtContent.ToJsonBytes(), _krbtgtKey);

            var clientPart = new ClientPart
            {
                SessionKey = sessionKeyC_TGS,
                Ticket = encryptedTGT,
                StartTime = now,
                EndTime = end
            };

            byte[] encryptedToClient = Encrypt(clientPart.ToJsonBytes(), clientKey);

            return new ASResponse
            {
                Success = true,
                EncryptedClientPart = encryptedToClient
            };
        }

        // ───────────────────────────────────────────────
        // TGS часть — выдача сервисного билета
        // ───────────────────────────────────────────────

        public TGSResponse RequestServiceTicket(TGSRequest req)
        {
            if (req.TGT == null || req.TGT.Length == 0)
                return ErrorTGS("TGT отсутствует");

            // Расшифровываем TGT ключом krbtgt
            byte[] tgtPlain;
            try
            {
                tgtPlain = Decrypt(req.TGT, _krbtgtKey);
            }
            catch
            {
                return ErrorTGS("Невалидный или повреждённый TGT");
            }

            var tgtContent = JsonSerializer.Deserialize<TicketContent>(Encoding.UTF8.GetString(tgtPlain));
            if (tgtContent == null || DateTime.UtcNow > tgtContent.EndTime)
                return ErrorTGS("TGT истёк или повреждён");

            // Проверяем аутентификатор клиента
            if (!ValidateAuthenticator(req.Authenticator, tgtContent.SessionKey))
                return ErrorTGS("Аутентификатор не прошёл проверку");

            string service = req.ServicePrincipal?.ToUpperInvariant();
            if (string.IsNullOrEmpty(service) || !_serviceKeys.TryGetValue(service, out byte[] serviceKey))
                return ErrorTGS("Сервис не найден");

            byte[] sessionKeyC_Service = GenerateSessionKey();

            DateTime now = DateTime.UtcNow;
            DateTime end = now.AddHours(8);

            var serviceTicketContent = new TicketContent
            {
                Client = tgtContent.Client,
                SessionKey = sessionKeyC_Service,
                Service = service,
                StartTime = now,
                EndTime = end,
                RenewTill = end.AddHours(24)
            };

            byte[] encryptedServiceTicket = Encrypt(serviceTicketContent.ToJsonBytes(), serviceKey);

            var clientReplyPart = new ClientServicePart
            {
                SessionKey = sessionKeyC_Service,
                Ticket = encryptedServiceTicket,
                StartTime = now,
                EndTime = end
            };

            byte[] encryptedToClient = Encrypt(clientReplyPart.ToJsonBytes(), tgtContent.SessionKey);

            return new TGSResponse
            {
                Success = true,
                EncryptedClientPart = encryptedToClient
            };
        }

        // ───────────────────────────────────────────────
        // Вспомогательные методы криптографии (Aes-CBC из предыдущего примера)
        // ───────────────────────────────────────────────

        private byte[] Encrypt(byte[] data, byte[] key)
        {
            using var aes = Aes.Create();
            aes.Key = key.Length switch
            {
                16 => key,
                24 => key,
                32 => key,
                _ => throw new ArgumentException("Ключ должен быть 16, 24 или 32 байта")
            };
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.GenerateIV(); // каждый раз новый IV!

            using var ms = new MemoryStream();
            ms.Write(aes.IV, 0, aes.IV.Length); // IV в начале!

            using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                cs.Write(data, 0, data.Length);
            }

            return ms.ToArray();
        }

        private byte[] Decrypt(byte[] data, byte[] key)
        {
            if (data.Length < 16) throw new CryptographicException("Слишком короткие данные");

            byte[] iv = new byte[16];
            Array.Copy(data, 0, iv, 0, 16);

            using var aes = Aes.Create();
            aes.Key = key.Length switch
            {
                16 => key,
                24 => key,
                32 => key,
                _ => throw new ArgumentException("Неверная длина ключа")
            };
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var ms = new MemoryStream(data, 16, data.Length - 16);
            using var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
            using var result = new MemoryStream();
            cs.CopyTo(result);

            return result.ToArray();
        }

        private byte[] DeriveKey(string password)
        {
            // Упрощённо — в реальности PBKDF2 + Kerberos string-to-key
            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(Encoding.UTF8.GetBytes(password + "EXAMPLE.COM"));
        }

        private byte[] GenerateSessionKey() => RandomNumberGenerator.GetBytes(32);

        private bool ValidatePreAuth(string principal, byte[] encTs, byte[] clientKey)
        {
            try
            {
                byte[] ts = Decrypt(encTs, clientKey);
                var time = DateTime.Parse(Encoding.UTF8.GetString(ts));
                return Math.Abs((DateTime.UtcNow - time).TotalMinutes) <= 5;
            }
            catch
            {
                return false;
            }
        }

        private bool ValidateAuthenticator(byte[] encAuth, byte[] sessionKey)
        {
            try
            {
                byte[] auth = Decrypt(encAuth, sessionKey);
                var authObj = JsonSerializer.Deserialize<Authenticator>(Encoding.UTF8.GetString(auth));
                return Math.Abs((DateTime.UtcNow - authObj.Timestamp).TotalMinutes) <= 5;
            }
            catch
            {
                return false;
            }
        }

        private ASResponse Error(string msg) => new() { Success = false, ErrorMessage = msg };
        private TGSResponse ErrorTGS(string msg) => new() { Success = false, ErrorMessage = msg };
    }

    // DTO-структуры (упрощённые)

    public class ASRequest
    {
        public string ClientPrincipal { get; set; }
        public string RequestedService { get; set; } = "krbtgt/EXAMPLE.COM@EXAMPLE.COM";
        public byte[] PreAuthEncryptedTimestamp { get; set; }
    }

    public class ASResponse
    {
        public bool Success { get; set; }
        public byte[] EncryptedClientPart { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class TGSRequest
    {
        public byte[] TGT { get; set; }
        public byte[] Authenticator { get; set; }
        public string ServicePrincipal { get; set; }
    }

    public class TGSResponse
    {
        public bool Success { get; set; }
        public byte[] EncryptedClientPart { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class ClientPart
    {
        public byte[] SessionKey { get; set; }
        public byte[] Ticket { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public byte[] ToJsonBytes() => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this));
    }

    public class ClientServicePart
    {
        public byte[] SessionKey { get; set; }
        public byte[] Ticket { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public byte[] ToJsonBytes() => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this));
    }

    public class TicketContent
    {
        public string Client { get; set; }
        public byte[] SessionKey { get; set; }
        public string Service { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public DateTime RenewTill { get; set; }
        public byte[] ToJsonBytes() => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this));
    }

    public class Authenticator
    {
        public DateTime Timestamp { get; set; }
    }
}