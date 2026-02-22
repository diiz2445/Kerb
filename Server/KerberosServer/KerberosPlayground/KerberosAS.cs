using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimpleKerberos
{
    public class KerberosKDC
    {
        private readonly Dictionary<string, byte[]> _userKeys = new();
        private readonly Dictionary<string, byte[]> _serviceKeys = new();
        private readonly byte[] _krbtgtKey;
        private readonly Dictionary<string, TicketContent> _activeTGTs = new();

        public KerberosKDC()
        {
            _krbtgtKey = DeriveKey("krbtgt/EXAMPLE.COM#super-secure-2025-key!");

            RegisterUser("alice@EXAMPLE.COM", "alicepass123");
            RegisterUser("bob@EXAMPLE.COM", "bobsecret2025");

            RegisterService("HTTP/web.example.com@EXAMPLE.COM", "http-svc-pass-789");
            RegisterService("MSSQLSvc/sql.example.com:1433@EXAMPLE.COM", "mssql-svc-key-321");
        }

        public void RegisterUser(string principal, string password)
        {
            _userKeys[principal.ToUpperInvariant()] = DeriveKey(password);
        }

        public void RegisterService(string servicePrincipal, string password)
        {
            _serviceKeys[servicePrincipal.ToUpperInvariant()] = DeriveKey(password);
        }

        public byte[] ProcessASRequest(byte[] requestJsonBytes) => ProcessRequest(requestJsonBytes, true);
        public byte[] ProcessTGSRequest(byte[] requestJsonBytes) => ProcessRequest(requestJsonBytes, false);

        private byte[] ProcessRequest(byte[] requestJsonBytes, bool isAS)
        {
            try
            {
                string json = Encoding.UTF8.GetString(requestJsonBytes);
                var req = JsonSerializer.Deserialize<RequestJson>(json)
                    ?? throw new Exception("Невалидный JSON");

                if (isAS)
                {
                    return HandleAS(req);
                }
                else
                {
                    return HandleTGS(req);
                }
            }
            catch (Exception ex)
            {
                return ErrorResponseBytes(ex.Message);
            }
        }

        private byte[] HandleAS(RequestJson req)
        {
            string client = req.Client?.ToUpperInvariant();
            if (string.IsNullOrEmpty(client) || !_userKeys.TryGetValue(client, out var clientKey))
                return ErrorResponseBytes("Пользователь не найден");

            if (!string.Equals(req.Service, "krbtgt/EXAMPLE.COM@EXAMPLE.COM", StringComparison.OrdinalIgnoreCase))
                return ErrorResponseBytes("AS выдаёт только TGT");

            byte[] encTs = Convert.FromBase64String(req.PreAuth ?? "");
            if (!ValidatePreAuth(client, encTs, clientKey))
                return ErrorResponseBytes("Pre-authentication failed");

            byte[] sessionKey = RandomNumberGenerator.GetBytes(32);

            var now = DateTime.UtcNow;
            var tgt = new TicketContent
            {
                Client = client,
                SessionKey = sessionKey,
                Service = "krbtgt/EXAMPLE.COM@EXAMPLE.COM",
                Start = now,
                End = now.AddHours(10),
                RenewTill = now.AddDays(7)
            };

            string tgtId = Convert.ToBase64String(sessionKey);
            _activeTGTs[tgtId] = tgt;

            byte[] encTGT = Encrypt(tgt.ToJsonBytes(), _krbtgtKey);

            var reply = new ReplyPart
            {
                SessionKey = sessionKey,
                Ticket = encTGT,
                Start = now,
                End = now.AddHours(10)
            };

            byte[] encReply = Encrypt(reply.ToJsonBytes(), clientKey);

            return SuccessResponseBytes(Convert.ToBase64String(encReply));
        }

        private byte[] HandleTGS(RequestJson req)
        {
            if (string.IsNullOrEmpty(req.TGT))
                return ErrorResponseBytes("TGT отсутствует");

            byte[] tgtBytes = Convert.FromBase64String(req.TGT);
            byte[] tgtPlain = Decrypt(tgtBytes, _krbtgtKey);
            var tgt = JsonSerializer.Deserialize<TicketContent>(Encoding.UTF8.GetString(tgtPlain))
                ?? throw new Exception("Повреждён TGT");

            if (DateTime.UtcNow > tgt.End)
                return ErrorResponseBytes("TGT истёк");

            byte[] encAuth = Convert.FromBase64String(req.Authenticator ?? "");
            if (!ValidateAuthenticator(encAuth, tgt.SessionKey))
                return ErrorResponseBytes("Аутентификатор недействителен");

            string svc = req.Service?.ToUpperInvariant();
            if (string.IsNullOrEmpty(svc) || !_serviceKeys.TryGetValue(svc, out var svcKey))
                return ErrorResponseBytes("Сервис не найден");

            byte[] sessionKeyCS = RandomNumberGenerator.GetBytes(32);

            var now = DateTime.UtcNow;
            var ticket = new TicketContent
            {
                Client = tgt.Client,
                SessionKey = sessionKeyCS,
                Service = svc,
                Start = now,
                End = now.AddHours(8),
                RenewTill = now.AddHours(24)
            };

            byte[] encTicket = Encrypt(ticket.ToJsonBytes(), svcKey);

            var reply = new ReplyPart
            {
                SessionKey = sessionKeyCS,
                Ticket = encTicket,
                Start = now,
                End = now.AddHours(8)
            };

            byte[] encReply = Encrypt(reply.ToJsonBytes(), tgt.SessionKey);

            return SuccessResponseBytes(Convert.ToBase64String(encReply));
        }

        // ───────────────────────────────────────────────
        // Криптография
        // ───────────────────────────────────────────────

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