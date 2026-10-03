using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AisFullExtractor
{
    /// <summary>
    /// Decrypts the AIS JSON file downloaded from the Income Tax e-filing portal.
    /// This is a C# port of decrypt_ais.py: same file layout (IV[32 hex] + salt[32 hex] + ciphertext),
    /// same PBKDF2-HMACSHA256 (1000 iterations, 32-byte key) + AES-256-CBC/PKCS7 scheme,
    /// and the same three password-construction fallbacks the script tries.
    /// </summary>
    public static class AisDecryptor
    {
        private const string PasswordMiddle = "GQ39%*g";

        public class AisDecryptionException : Exception
        {
            public AisDecryptionException(string message, Exception inner = null) : base(message, inner) { }
        }

        /// <summary>
        /// Decrypts the file and returns the decrypted AIS JSON text.
        /// </summary>
        /// <param name="filePath">Path to the encrypted AIS JSON file.</param>
        /// <param name="pan">10-character PAN.</param>
        /// <param name="dob">8-digit date of birth, ddmmyyyy.</param>
        public static string DecryptFile(string filePath, string pan, string dob)
        {
            if (string.IsNullOrWhiteSpace(pan) || pan.Length != 10)
                throw new AisDecryptionException("PAN must be exactly 10 characters.");
            if (string.IsNullOrWhiteSpace(dob) || dob.Length != 8 || !IsAllDigits(dob))
                throw new AisDecryptionException("Date of birth must be exactly 8 digits (ddmmyyyy).");

            string raw = File.ReadAllText(filePath, Encoding.UTF8).Trim();

            // Some tools export the encrypted AIS string wrapped as a JSON string literal.
            if (raw.Length >= 2 && raw.StartsWith("\"") && raw.EndsWith("\""))
            {
                try { raw = JsonConvert.DeserializeObject<string>(raw); }
                catch { /* fall through and let the length check below report the real problem */ }
            }

            if (raw.Length < 64)
                throw new AisDecryptionException(
                    "Encrypted AIS file is too short. Expected IV (32 hex chars) + salt (32 hex chars) + ciphertext.");

            string ivHex = raw.Substring(0, 32);
            string saltHex = raw.Substring(32, 32);
            string cipherPart = raw.Substring(64);

            byte[] iv, salt;
            try
            {
                iv = HexToBytes(ivHex);
                salt = HexToBytes(saltHex);
            }
            catch (Exception ex)
            {
                throw new AisDecryptionException(
                    "The first 64 characters of the AIS file must be hexadecimal IV + salt.", ex);
            }

            byte[] cipherText;
            try
            {
                cipherText = Convert.FromBase64String(cipherPart);
            }
            catch
            {
                try { cipherText = HexToBytes(cipherPart); }
                catch (Exception ex)
                {
                    throw new AisDecryptionException("Ciphertext must be valid Base64 or hexadecimal data.", ex);
                }
            }

            if (cipherText.Length == 0)
                throw new AisDecryptionException("Ciphertext payload is empty.");

            // Same candidate order the Python script tries.
            string[] candidates =
            {
                pan.ToLowerInvariant() + PasswordMiddle + dob,
                pan.ToLowerInvariant() + dob,
                pan.ToUpperInvariant() + dob
            };

            Exception lastError = null;
            foreach (var candidate in candidates)
            {
                try
                {
                    string plaintext = DecryptWithPassword(iv, salt, cipherText, candidate);
                    // Confirm it actually decrypted to valid JSON before accepting it.
                    JToken.Parse(plaintext);
                    return plaintext;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            throw new AisDecryptionException(
                "Decryption failed. Please check the PAN and date of birth, and confirm this is the encrypted " +
                "AIS JSON downloaded from the Income Tax e-filing portal.", lastError);
        }

        private static string DecryptWithPassword(byte[] iv, byte[] salt, byte[] cipherText, string password)
        {
            byte[] key;
            using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 1000, HashAlgorithmName.SHA256))
            {
                key = pbkdf2.GetBytes(32); // AES-256
            }

            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.IV = iv;

                using (var decryptor = aes.CreateDecryptor())
                using (var ms = new MemoryStream(cipherText))
                using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                using (var sr = new StreamReader(cs, Encoding.UTF8))
                {
                    return sr.ReadToEnd();
                }
            }
        }

        private static bool IsAllDigits(string s)
        {
            foreach (var c in s)
                if (c < '0' || c > '9') return false;
            return true;
        }

        private static byte[] HexToBytes(string hex)
        {
            if (hex.Length % 2 != 0) throw new FormatException("Invalid hex length.");
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}
