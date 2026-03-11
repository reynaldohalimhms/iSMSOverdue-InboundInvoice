using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace iSMSOverdue.InboundInvoice.Helper
{
    public class CryptoHelper
    {
        private static string key = string.Empty;

        public CryptoHelper(string _key)
        {
            key = _key;
        }

        public string Encrypt(string plainText)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = Encoding.UTF8.GetBytes(key);
                aes.IV = aes.Key;

                var enc = aes.CreateEncryptor(aes.Key, aes.IV);
                using (var ms = new MemoryStream())
                using (var cs = new CryptoStream(ms, enc, CryptoStreamMode.Write))
                using (var sw = new StreamWriter(cs))
                {
                    sw.Write(plainText);
                    sw.Flush();
                    cs.FlushFinalBlock();
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        public string Decrypt(string cipherTextBase64)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = Encoding.UTF8.GetBytes(key);
                aes.IV = aes.Key;

                var dec = aes.CreateDecryptor(aes.Key, aes.IV);
                using (var ms = new MemoryStream(Convert.FromBase64String(cipherTextBase64)))
                using (var cs = new CryptoStream(ms, dec, CryptoStreamMode.Read))
                using (var sr = new StreamReader(cs))
                {
                    return sr.ReadToEnd();
                }
            }
        }

        public string SHA256Encrypt(string text)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            var textBytes = Encoding.UTF8.GetBytes(text);
            using (var h = new HMACSHA256(keyBytes))
            {
                var hash = h.ComputeHash(textBytes);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}