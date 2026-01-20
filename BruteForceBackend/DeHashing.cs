namespace BruteForceBackend
{
    using System;
    using System.Diagnostics;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    public class DeHashing
    {
        public required string Hash { get; set; }
        public required string PepperLocation { get; set; }
        public required bool UseNumbers { get; set; }
        public required bool UseSmallLetters { get; set; }
        public required bool UseBigLetters { get; set; }
        public required bool UseSpecialChars { get; set; }

        public string? LimitStartingChars { get; set; }
        
        public string charset = "";
        public string pepper = "cajovna-2025-";

        private volatile string? foundPassword = null;
        private long totalChecks = 0;
        private int currentLenght = 0;

        private byte[] targetHashBytes = Array.Empty<byte>();

        public void Setup()
        {
            StringBuilder sb = new StringBuilder();
            if (UseNumbers) sb.Append("0123456789");
            if (UseSmallLetters) sb.Append("abcdefghijklmnopqrstuvwxyz");
            if (UseBigLetters) sb.Append("ABCDEFGHIJKLMNOPQRSTUVWXYZ");
            if (UseSpecialChars) sb.Append("!@#$%^&*()-_=+[]{}|;:',.<>?/`~");
            charset = sb.ToString();

            targetHashBytes = Convert.FromHexString(Hash);
        }

        public string DeHash(int minLength, CancellationToken ct)
        {
            Setup();
            foundPassword = null;
            totalChecks = 0;

            var progressTask = Task.Run(async () => {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                var stopwatch = Stopwatch.StartNew();
                
                while (await timer.WaitForNextTickAsync() && foundPassword == null && !ct.IsCancellationRequested)
                {
                    double seconds = stopwatch.Elapsed.TotalSeconds;
                    double rate = totalChecks / seconds; 

                    BruteForceStats.TotalChecks = totalChecks;
                    BruteForceStats.Speed = rate;
                    BruteForceStats.CurrentLength = currentLenght;

                    Console.WriteLine($"[Stats] Checked: {totalChecks:N0} | Speed: {rate:N0}/s | Length: {currentLenght}");
                }
            }, ct);

            for (int length = minLength; length < 100; length++)
            {
                currentLenght = length;

                string currentOuterCharset = LimitStartingChars ?? charset;

                Parallel.ForEach(currentOuterCharset, new ParallelOptions { CancellationToken = ct }, (firstChar, state) =>
                {
                    char[] buffer = new char[length];
                    buffer[0] = firstChar;

                    using (SHA256 sha = SHA256.Create())
                    {
                        DeHashRecursive(buffer, 1, length, ct, state, sha);
                    }
                });

                if (foundPassword != null) return foundPassword;
            }

            return "Not found";
        }

        private void DeHashRecursive(char[] buffer, int currentIndex, int targetLength, CancellationToken ct, ParallelLoopState state, SHA256 sha)
        {
            if (foundPassword != null || ct.IsCancellationRequested) 
            {
                state.Stop();
                return;
            }

            if (currentIndex == targetLength)
            {
                Interlocked.Increment(ref totalChecks);

                if (CheckHashFast(buffer, sha))
                {
                    foundPassword = new string(buffer);
                    state.Stop();
                }
                return;
            }

            foreach (char c in charset)
            {
                buffer[currentIndex] = c;
                DeHashRecursive(buffer, currentIndex + 1, targetLength, ct, state, sha);
                
                if (state.IsStopped) return;
            }
        }

        private bool CheckHashFast(char[] buffer, SHA256 sha)
        {
            int totalLen = buffer.Length + pepper.Length;
            
            Span<byte> inputBytes = stackalloc byte[totalLen * 4];
            
            int bytesWritten = 0;
            
            if (PepperLocation == "before")
            {
                bytesWritten += Encoding.UTF8.GetBytes(pepper, inputBytes.Slice(bytesWritten));
                bytesWritten += Encoding.UTF8.GetBytes(buffer, inputBytes.Slice(bytesWritten));
            }
            else
            {
                bytesWritten += Encoding.UTF8.GetBytes(buffer, inputBytes.Slice(bytesWritten));
                bytesWritten += Encoding.UTF8.GetBytes(pepper, inputBytes.Slice(bytesWritten));
            }

            Span<byte> hashResult = stackalloc byte[32];
            sha.TryComputeHash(inputBytes.Slice(0, bytesWritten), hashResult, out int _);

            return hashResult.SequenceEqual(targetHashBytes);
        }
    }
}