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

        // 1. NEW PROPERTY
        public string? LimitStartingChars { get; set; }
        
        public string charset = "";
        public string pepper = "cajovna-2025-";

        private volatile string? foundPassword = null;
        private long totalChecks = 0;
        private int currentLenght = 0;

        // We need the Target Hash as bytes for fast comparison
        private byte[] targetHashBytes = Array.Empty<byte>();

        public void Setup()
        {
            // 1. Build Charset
            StringBuilder sb = new StringBuilder();
            if (UseNumbers) sb.Append("0123456789");
            if (UseSmallLetters) sb.Append("abcdefghijklmnopqrstuvwxyz");
            if (UseBigLetters) sb.Append("ABCDEFGHIJKLMNOPQRSTUVWXYZ");
            if (UseSpecialChars) sb.Append("!@#$%^&*()-_=+[]{}|;:',.<>?/`~");
            charset = sb.ToString();

            // 2. Convert Target Hash to Bytes ONCE (so we don't do it every loop)
            targetHashBytes = Convert.FromHexString(Hash);
        }

        public string DeHash(int minLength, CancellationToken ct)
        {
            Setup(); // Prepare data
            foundPassword = null;
            totalChecks = 0;

            // Progress Logger
            var progressTask = Task.Run(async () => {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(1)); // Update every 1s
                var stopwatch = Stopwatch.StartNew();
                
                while (await timer.WaitForNextTickAsync() && foundPassword == null && !ct.IsCancellationRequested)
                {
                    double seconds = stopwatch.Elapsed.TotalSeconds;
                    double rate = totalChecks / seconds; 

                    // --- UPDATE THE GLOBAL SCOREBOARD ---
                    // Now the API can see these numbers!
                    BruteForceStats.TotalChecks = totalChecks;
                    BruteForceStats.Speed = rate;
                    BruteForceStats.CurrentLength = currentLenght;

                    // You can still keep this for debugging if you want
                    Console.WriteLine($"[Stats] Checked: {totalChecks:N0} | Speed: {rate:N0}/s | Length: {currentLenght}");
                }
            }, ct);

            for (int length = minLength; length < 100; length++)
            {
                currentLenght = length;

                // 2. USE THE SPLIT HERE
                // If LimitStartingChars is set (Hybrid mode), we only iterate those for the first letter.
                // Otherwise we use the full charset.
                string currentOuterCharset = LimitStartingChars ?? charset;

                // Parallel Loop
                Parallel.ForEach(currentOuterCharset, new ParallelOptions { CancellationToken = ct }, (firstChar, state) =>
                {
                    // OPTIMIZATION: Create one buffer per thread
                    // This replaces millions of 'string' creations
                    char[] buffer = new char[length];
                    buffer[0] = firstChar; // Set the first letter (assigned by Parallel)

                    using (SHA256 sha = SHA256.Create())
                    {
                        // Start recursion at index 1 (since index 0 is already set)
                        DeHashRecursive(buffer, 1, length, ct, state, sha);
                    }
                });

                if (foundPassword != null) return foundPassword;
            }

            return "Not found";
        }

        // --- ZERO ALLOCATION WORKER ---
        private void DeHashRecursive(char[] buffer, int currentIndex, int targetLength, CancellationToken ct, ParallelLoopState state, SHA256 sha)
        {
            if (foundPassword != null || ct.IsCancellationRequested) 
            {
                state.Stop();
                return;
            }

            // BASE CASE: Buffer is full
            if (currentIndex == targetLength)
            {
                Interlocked.Increment(ref totalChecks);

                // OPTIMIZATION: Check directly without creating ANY strings or arrays
                if (CheckHashFast(buffer, sha))
                {
                    foundPassword = new string(buffer); // Create string only if found!
                    state.Stop();
                }
                return;
            }

            // RECURSIVE STEP
            // We loop through the charset and MUTATE the buffer in place
            foreach (char c in charset)
            {
                buffer[currentIndex] = c; // <--- No new string created here! Just editing.
                DeHashRecursive(buffer, currentIndex + 1, targetLength, ct, state, sha);
                
                if (state.IsStopped) return;
            }
        }

        // --- ZERO ALLOCATION CHECKER ---
        // This function uses "Span" to operate on memory directly
        private bool CheckHashFast(char[] buffer, SHA256 sha)
        {
            // 1. Calculate buffer size needed (Pepper + Password)
            int totalLen = buffer.Length + pepper.Length;
            
            // StackAlloc creates a temporary array on the CPU Stack (Super fast, no RAM cleanup needed)
            Span<byte> inputBytes = stackalloc byte[totalLen * 4]; // *4 for max UTF8 size safety
            
            // 2. Combine Pepper + Password into inputBytes
            // (We are doing this manually to avoid string creation)
            int bytesWritten = 0;
            
            if (PepperLocation == "before")
            {
                bytesWritten += Encoding.UTF8.GetBytes(pepper, inputBytes.Slice(bytesWritten));
                bytesWritten += Encoding.UTF8.GetBytes(buffer, inputBytes.Slice(bytesWritten));
            }
            else // after
            {
                bytesWritten += Encoding.UTF8.GetBytes(buffer, inputBytes.Slice(bytesWritten));
                bytesWritten += Encoding.UTF8.GetBytes(pepper, inputBytes.Slice(bytesWritten));
            }

            // 3. Hash directly into stack memory
            Span<byte> hashResult = stackalloc byte[32]; // SHA256 is always 32 bytes
            sha.TryComputeHash(inputBytes.Slice(0, bytesWritten), hashResult, out int _);

            // 4. Compare bytes directly (No "Convert.ToHexString")
            return hashResult.SequenceEqual(targetHashBytes);
        }
    }
}