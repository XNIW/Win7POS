using System;
using System.Security.Cryptography;

namespace Win7POS.Core.Security
{
    /// <summary>Hash e verifica PIN con PBKDF2 (compatibile netstandard2.0).</summary>
    public static class PinHelper
    {
        private const int SaltLength = 16;
        private const int HashLength = 32;
        private const int Iterations = 10000;

        /// <summary>Genera un salt casuale (Base64).</summary>
        public static string GenerateSalt()
        {
            var bytes = new byte[SaltLength];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>Calcola hash del PIN con il salt dato (output Base64).</summary>
        public static string HashPin(string pin, string saltBase64)
        {
            if (string.IsNullOrEmpty(pin)) throw new ArgumentException("PIN is empty");
            if (string.IsNullOrEmpty(saltBase64)) throw new ArgumentException("Salt is empty");

            var salt = Convert.FromBase64String(saltBase64);
            var pinBytes = System.Text.Encoding.UTF8.GetBytes(pin);

            using (var pbkdf2 = new Rfc2898DeriveBytes(pinBytes, salt, Iterations))
            {
                var hash = pbkdf2.GetBytes(HashLength);
                return Convert.ToBase64String(hash);
            }
        }

        /// <summary>Verifica che il PIN corrisponda a hash e salt.</summary>
        public static bool VerifyPin(string pin, string saltBase64, string storedHashBase64)
        {
            if (string.IsNullOrEmpty(storedHashBase64)) return false;
            var computed = HashPin(pin, saltBase64);
            return string.Equals(computed, storedHashBase64, StringComparison.Ordinal);
        }
    }
}
