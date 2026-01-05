namespace BruteForceBackend
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    public class DeHashing
    {
        public required string Hash {get; set;}
        public string charset = "abcdefghijklmnopqrstuvwxyz0123456789";
        public string pepper = "cajovna-2025-";

        public string HashCompare(string generatedHash, string input)
        {
            if (generatedHash == Hash)
            {
                return input;
            }
            else
            {
                return "not the answer";
            }
        }

        public string HashGenerator(string input)
        {
            using (SHA256 sha256Hash = SHA256.Create())
            {
                byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(pepper + input));

                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }

        public string DeHash(int maxLength, CancellationToken ct)
        {
            for (int length = 1; length <= maxLength; length++)
            {
                string result = DeHashRecursive("", length, ct);
                if (result != "not the answer")
                {
                    return result;
                }
            }
            return "Not found";
        }

        private string DeHashRecursive(string current, int maxLength, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
        
            if (current.Length == maxLength)
            {
                return HashCompare(HashGenerator(current), current);
            }

            foreach (char c in charset)
            {
                string result = DeHashRecursive(current + c, maxLength, ct);
                if (result != "not the answer")
                {
                    return result;
                }
            }

            return "not the answer";
        }

    }
}