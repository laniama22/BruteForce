namespace BruteForceBackend
{
    using System.Threading;
    using System.Threading.Tasks;

    // This script was made by Gemini AI - https://www.gemini.com/

    public class HybridDeHashing
    {
        internal async Task<string> SolveAsync(HashRequest request, CancellationToken ct)
        {
            // 1. Build Charset (We need to know it to split it)
            // (Copying logic from DeHashing to ensure consistency)
            var sb = new System.Text.StringBuilder();
            if (request.UseNumbers) sb.Append("0123456789");
            if (request.UseSmallLetters) sb.Append("abcdefghijklmnopqrstuvwxyz");
            if (request.UseBigLetters) sb.Append("ABCDEFGHIJKLMNOPQRSTUVWXYZ");
            if (request.UseSpecialChars) sb.Append("!@#$%^&*()-_=+[]{}|;:',.<>?/`~");
            string fullCharset = sb.ToString();

            if (string.IsNullOrEmpty(fullCharset)) return "Error: No Charset";

            // 2. THE SPLIT STRATEGY
            int availableCores = Environment.ProcessorCount; // e.g., 8, 12, 16

            // We want the CPU to have at least 1 char per core so all cores work.
            // But we also don't want to steal the WHOLE job from the GPU.
            // Let's cap it at max 20% of the total charset or the core count.
            int cpuCharCount = Math.Min(availableCores, fullCharset.Length / 5);

            // Safety: Ensure we take at least 1, but not more than total length
            cpuCharCount = Math.Clamp(cpuCharCount, 1, fullCharset.Length - 1);
            
            // CPU takes the first character (e.g., '0' or 'a')
            // GPU takes everything else.
            string cpuChars = fullCharset.Substring(0, cpuCharCount);
            
            Console.WriteLine($"[HYBRID] CPU Cores: {availableCores}");
            Console.WriteLine($"[HYBRID] CPU takes first {cpuCharCount} chars: '{cpuChars}'");
            Console.WriteLine($"[HYBRID] GPU handles the rest ({fullCharset.Length - cpuCharCount} chars)");

            // 3. Setup CPU Solver
            var cpuSolver = new DeHashing
            {
                Hash = request.HashToCrack,
                PepperLocation = request.PepperLocation,
                Pepper = request.Pepper,
                UseNumbers = request.UseNumbers,
                UseSmallLetters = request.UseSmallLetters,
                UseBigLetters = request.UseBigLetters,
                UseSpecialChars = request.UseSpecialChars,
                LimitStartingChars = cpuChars // <--- Restrict CPU
            };

            // 4. Setup GPU Solver
            var gpuSolver = new GpuDeHashing();

            // 5. Create Cancellation (First one to finish stops the other)
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // 6. Run Both Tasks
            var cpuTask = Task.Run(() => cpuSolver.DeHash(1, linkedCts.Token));
            
            var gpuTask = Task.Run(() => gpuSolver.DeHash(
                request.HashToCrack, 
                fullCharset, 
                "cajovna-2025-", 
                request.PepperLocation, 
                request.MinLength, // Min Length
                cpuCharCount, // <--- Tell GPU to skip the CPU's work
                linkedCts.Token
            ));

            // 7. Wait for the winner
            Task<string> winner = await Task.WhenAny(cpuTask, gpuTask);
            
            // Cancel the loser
            linkedCts.Cancel();

            string result = await winner;
            return result;
        }
    }
}