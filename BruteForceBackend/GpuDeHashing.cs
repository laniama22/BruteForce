namespace BruteForceBackend
{
    using System;
    using System.Text;
    using System.Linq;
    using System.Threading;
    using ILGPU;
    using ILGPU.Runtime;
    using ILGPU.Runtime.Cuda;
    using ILGPU.Runtime.OpenCL;
    using ILGPU.Runtime.CPU;

    // This script was made by Gemini AI - https://www.gemini.com/

    public class GpuDeHashing : IDisposable
    {
        private Context context;
        private Accelerator accelerator;

        // --- HOST SIDE (CPU) ---
        public GpuDeHashing()
        {
            // Initialize ILGPU
            context = Context.Create(builder => builder.AllAccelerators());
            
            // --- FIX 1: Device Selection Logic ---
            // Replaced FirstOrDefault() with explicit Length checks to avoid ambiguity
            Device? device = null;
            var cudaDevices = context.GetCudaDevices();
            var clDevices = context.GetCLDevices();
            var cpuDevices = context.GetCPUDevices();

            if (cudaDevices.Count > 0)
            {
                device = cudaDevices[0];
            }
            else if (clDevices.Count > 0)
            {
                device = clDevices[0];
            }
            else
            {
                device = cpuDevices[0];
            }

            accelerator = device!.CreateAccelerator(context);
            Console.WriteLine($"[GPU] Accelerated by: {device.Name}");
        }

        public void Dispose()
        {
            accelerator.Dispose();
            context.Dispose();
        }

        public string DeHash(string targetHashStr, string charsetStr, string pepperStr, string pepperMode, int minLength, int skipFirstCharsCount, CancellationToken ct)
        {
            // 1. Prepare Data
            uint[] targetHash = HexToUIntArray(targetHashStr);
            byte[] charset = Encoding.UTF8.GetBytes(charsetStr);
            byte[] pepper = Encoding.UTF8.GetBytes(pepperStr);
            int pepperModeInt = pepperMode == "before" ? 1 : 2; // 1=Before, 2=After, 0=None

            // 2. Load Data to GPU Memory
            using var d_target = accelerator.Allocate1D(targetHash);
            using var d_charset = accelerator.Allocate1D(charset);
            using var d_pepper = accelerator.Allocate1D(pepper);
            using var d_result = accelerator.Allocate1D<int>(1); // Holds the "Found Index"
            d_result.MemSetToZero();

            // 3. Compile Kernel
            var kernel = accelerator.LoadAutoGroupedStreamKernel<
                Index1D, ArrayView<uint>, ArrayView<byte>, ArrayView<byte>, int, int, long, ArrayView<int>
                >(BruteForceKernel);

            // 4. Execution Loop
            // We loop from minLength upwards forever (until we hit the 64-bit integer limit)
            for (int length = minLength; length < 100; length++)
            {
                if (ct.IsCancellationRequested) break;

                // 1. CALCULATE OFFSET TO SKIP CPU WORK
                // If CPU does the first 1 char, GPU must skip 1 * (Charset^Length-1) combinations.
                long combinationsPerFirstChar = (long)Math.Pow(charsetStr.Length, length - 1);
                long startOffset = skipFirstCharsCount * combinationsPerFirstChar;

                // CHECK FOR OVERFLOW
                // If the number of combinations is too big for a 64-bit integer, we must stop.
                // (This usually happens around length 11 or 12 depending on charset size)
                double realCombos = Math.Pow(charset.Length, length);
                if (realCombos > long.MaxValue)
                {
                    Console.WriteLine($"[GPU] Length {length} is too huge ({realCombos:N0}) for 64-bit math. Stopping.");
                    break;
                }

                long totalCombos = (long)realCombos;
                int batchSize = 10_000_000; 

                // GPU only checks from 'startOffset' to the end
                long combosToCheck = totalCombos - startOffset;
                
                Console.WriteLine($"[GPU] Length {length}: Checking {totalCombos:N0} passwords...");

                for (long offset = startOffset; offset < totalCombos; offset += batchSize)
                {
                    if (ct.IsCancellationRequested) break;

                    // Calculate how many to do in this specific batch (prevent overshoot)
                    // We use (long) here to ensure math is done in 64-bit, then cast to int for the batch count
                    int currentBatch = (int)Math.Min((long)batchSize, totalCombos - offset);

                    // Launch Kernel
                    // IMPORTANT: Removed '(int)' from offset. We pass the full 'long'.
                    kernel(currentBatch, d_target.View, d_charset.View, d_pepper.View, pepperModeInt, length, offset, d_result.View);
                    
                    accelerator.Synchronize(); // Wait for GPU

                    // Check result
                    int[] res = d_result.GetAsArray1D();
                    if (res[0] != 0)
                    {
                        // Reconstruct password on CPU
                        long foundIndex = offset + (res[0] - 1);
                        return DecodePassword(foundIndex, charsetStr, length);
                    }
                }
            }

            return "Not found";
        }

        // --- DEVICE SIDE (The GPU Kernel) ---
        static void BruteForceKernel(
            Index1D index,              // Thread ID (0 to BatchSize)
            ArrayView<uint> target,     // Target Hash (8 uints)
            ArrayView<byte> charset,    // Charset
            ArrayView<byte> pepper,     // Pepper bytes
            int pepperMode,             // 1=Before, 2=After
            int passLength,             // Length of password to generate
            long globalOffset,           // Where we are in the total count
            ArrayView<int> result)      // Result buffer
        {
            // Stop if found
            if (result[0] != 0) return;
            
            uint w0 = 0, w1 = 0, w2 = 0, w3 = 0, w4 = 0, w5 = 0, w6 = 0, w7 = 0;
            uint w8 = 0, w9 = 0, w10 = 0, w11 = 0, w12 = 0, w13 = 0, w14 = 0, w15 = 0;

            // --- A. CONSTRUCT MESSAGE (Pepper + Password + Padding) ---
            long myGlobalIndex = globalOffset + index;
            int currentPos = 0;

            // Apply Pepper Before
            if (pepperMode == 1) 
            {
                for (int i = 0; i < pepper.IntLength; i++) 
                    SetByte(ref w0, ref w1, ref w2, ref w3, ref w4, ref w5, ref w6, ref w7, ref w8, ref w9, ref w10, ref w11, ref w12, ref w13, ref w14, ref w15, currentPos++, pepper[i]);
            }

            // Generate Password (Base-N decoding)
            int charsetLen = charset.IntLength;
            
            for (int i = 0; i < passLength; i++)
            {
                long powerOf = 1;
                for(int p=0; p < (passLength - 1 - i); p++) powerOf *= charsetLen;
                
                int charIndex = (int)((myGlobalIndex / powerOf) % charsetLen);
                
                // --- FIX 2: Renamed 'c' to 'charByte' ---
                byte charByte = charset[charIndex];
                
                SetByte(ref w0, ref w1, ref w2, ref w3, ref w4, ref w5, ref w6, ref w7, ref w8, ref w9, ref w10, ref w11, ref w12, ref w13, ref w14, ref w15, currentPos++, charByte);
            }

            // Apply Pepper After
            if (pepperMode == 2)
            {
                for (int i = 0; i < pepper.IntLength; i++) 
                    SetByte(ref w0, ref w1, ref w2, ref w3, ref w4, ref w5, ref w6, ref w7, ref w8, ref w9, ref w10, ref w11, ref w12, ref w13, ref w14, ref w15, currentPos++, pepper[i]);
            }

            // --- B. PADDING ---
            SetByte(ref w0, ref w1, ref w2, ref w3, ref w4, ref w5, ref w6, ref w7, ref w8, ref w9, ref w10, ref w11, ref w12, ref w13, ref w14, ref w15, currentPos++, 0x80);

            // Length in Bits
            long bitLength = (long)(currentPos - 1) * 8; 
            w15 = (uint)bitLength; 

            // --- C. SHA256 COMPRESSION ---
            uint h0 = 0x6a09e667; uint h1 = 0xbb67ae85; uint h2 = 0x3c6ef372; uint h3 = 0xa54ff53a;
            uint h4 = 0x510e527f; uint h5 = 0x9b05688c; uint h6 = 0x1f83d9ab; uint h7 = 0x5be0cd19;

            uint a = h0, b = h1, c = h2, d = h3, e = h4, f = h5, g = h6, h = h7;

            uint tr_w0 = w0, tr_w1 = w1, tr_w2 = w2, tr_w3 = w3, tr_w4 = w4, tr_w5 = w5, tr_w6 = w6, tr_w7 = w7;
            uint tr_w8 = w8, tr_w9 = w9, tr_w10 = w10, tr_w11 = w11, tr_w12 = w12, tr_w13 = w13, tr_w14 = w14, tr_w15 = w15;
            
            for (int i = 0; i < 64; i++)
            {
                uint wVal = 0;
                if (i < 16) 
                {
                     wVal = GetW(i, tr_w0, tr_w1, tr_w2, tr_w3, tr_w4, tr_w5, tr_w6, tr_w7, tr_w8, tr_w9, tr_w10, tr_w11, tr_w12, tr_w13, tr_w14, tr_w15);
                }
                else
                {
                    uint wMinus2 = GetW((i - 2) & 15, tr_w0, tr_w1, tr_w2, tr_w3, tr_w4, tr_w5, tr_w6, tr_w7, tr_w8, tr_w9, tr_w10, tr_w11, tr_w12, tr_w13, tr_w14, tr_w15);
                    uint wMinus7 = GetW((i - 7) & 15, tr_w0, tr_w1, tr_w2, tr_w3, tr_w4, tr_w5, tr_w6, tr_w7, tr_w8, tr_w9, tr_w10, tr_w11, tr_w12, tr_w13, tr_w14, tr_w15);
                    uint wMinus15 = GetW((i - 15) & 15, tr_w0, tr_w1, tr_w2, tr_w3, tr_w4, tr_w5, tr_w6, tr_w7, tr_w8, tr_w9, tr_w10, tr_w11, tr_w12, tr_w13, tr_w14, tr_w15);
                    uint wMinus16 = GetW((i - 16) & 15, tr_w0, tr_w1, tr_w2, tr_w3, tr_w4, tr_w5, tr_w6, tr_w7, tr_w8, tr_w9, tr_w10, tr_w11, tr_w12, tr_w13, tr_w14, tr_w15);
                    
                    uint s1 = Ror(wMinus2, 17) ^ Ror(wMinus2, 19) ^ (wMinus2 >> 10);
                    uint s0 = Ror(wMinus15, 7) ^ Ror(wMinus15, 18) ^ (wMinus15 >> 3);
                    
                    wVal = s1 + wMinus7 + s0 + wMinus16;
                    
                    SetW(i & 15, wVal, ref tr_w0, ref tr_w1, ref tr_w2, ref tr_w3, ref tr_w4, ref tr_w5, ref tr_w6, ref tr_w7, ref tr_w8, ref tr_w9, ref tr_w10, ref tr_w11, ref tr_w12, ref tr_w13, ref tr_w14, ref tr_w15);
                }

                uint temp1 = h + (Ror(e, 6) ^ Ror(e, 11) ^ Ror(e, 25)) + ((e & f) ^ (~e & g)) + K(i) + wVal;
                uint temp2 = (Ror(a, 2) ^ Ror(a, 13) ^ Ror(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));

                h = g; g = f; f = e; e = d + temp1;
                d = c; c = b; b = a; a = temp1 + temp2;
            }

            h0 += a; h1 += b; h2 += c; h3 += d; h4 += e; h5 += f; h6 += g; h7 += h;

            // --- D. COMPARE ---
            if (h0 == target[0] && h1 == target[1] && h2 == target[2] && h3 == target[3] &&
                h4 == target[4] && h5 == target[5] && h6 == target[6] && h7 == target[7])
            {
                Atomic.Exchange(ref result[0], (int)index + 1);
            }
        }

        // --- GPU HELPERS (Must be static) ---
        
        static void SetByte(ref uint w0, ref uint w1, ref uint w2, ref uint w3, ref uint w4, ref uint w5, ref uint w6, ref uint w7, ref uint w8, ref uint w9, ref uint w10, ref uint w11, ref uint w12, ref uint w13, ref uint w14, ref uint w15, int pos, byte val)
        {
            int uintIdx = pos / 4;
            int shift = (3 - (pos % 4)) * 8;
            uint mask = (uint)val << shift;
            
            switch(uintIdx) {
                case 0: w0 |= mask; break; case 1: w1 |= mask; break; case 2: w2 |= mask; break; case 3: w3 |= mask; break;
                case 4: w4 |= mask; break; case 5: w5 |= mask; break; case 6: w6 |= mask; break; case 7: w7 |= mask; break;
                case 8: w8 |= mask; break; case 9: w9 |= mask; break; case 10: w10 |= mask; break; case 11: w11 |= mask; break;
                case 12: w12 |= mask; break; case 13: w13 |= mask; break; case 14: w14 |= mask; break; case 15: w15 |= mask; break;
            }
        }
        
        static uint GetW(int idx, uint w0, uint w1, uint w2, uint w3, uint w4, uint w5, uint w6, uint w7, uint w8, uint w9, uint w10, uint w11, uint w12, uint w13, uint w14, uint w15)
        {
             switch(idx) {
                case 0: return w0; case 1: return w1; case 2: return w2; case 3: return w3; 
                case 4: return w4; case 5: return w5; case 6: return w6; case 7: return w7;
                case 8: return w8; case 9: return w9; case 10: return w10; case 11: return w11;
                case 12: return w12; case 13: return w13; case 14: return w14; case 15: return w15;
            }
            return 0;
        }

        static void SetW(int idx, uint val, ref uint w0, ref uint w1, ref uint w2, ref uint w3, ref uint w4, ref uint w5, ref uint w6, ref uint w7, ref uint w8, ref uint w9, ref uint w10, ref uint w11, ref uint w12, ref uint w13, ref uint w14, ref uint w15)
        {
             switch(idx) {
                case 0: w0 = val; break; case 1: w1 = val; break; case 2: w2 = val; break; case 3: w3 = val; break; 
                case 4: w4 = val; break; case 5: w5 = val; break; case 6: w6 = val; break; case 7: w7 = val; break;
                case 8: w8 = val; break; case 9: w9 = val; break; case 10: w10 = val; break; case 11: w11 = val; break;
                case 12: w12 = val; break; case 13: w13 = val; break; case 14: w14 = val; break; case 15: w15 = val; break;
            }
        }

        static uint Ror(uint x, int n) => (x >> n) | (x << (32 - n));

        static uint K(int i) 
        {
            switch(i) {
                case 0: return 0x428a2f98; case 1: return 0x71374491; case 2: return 0xb5c0fbcf; case 3: return 0xe9b5dba5;
                case 4: return 0x3956c25b; case 5: return 0x59f111f1; case 6: return 0x923f82a4; case 7: return 0xab1c5ed5;
                case 8: return 0xd807aa98; case 9: return 0x12835b01; case 10: return 0x243185be; case 11: return 0x550c7dc3;
                case 12: return 0x72be5d74; case 13: return 0x80deb1fe; case 14: return 0x9bdc06a7; case 15: return 0xc19bf174;
                case 16: return 0xe49b69c1; case 17: return 0xefbe4786; case 18: return 0x0fc19dc6; case 19: return 0x240ca1cc;
                case 20: return 0x2de92c6f; case 21: return 0x4a7484aa; case 22: return 0x5cb0a9dc; case 23: return 0x76f988da;
                case 24: return 0x983e5152; case 25: return 0xa831c66d; case 26: return 0xb00327c8; case 27: return 0xbf597fc7;
                case 28: return 0xc6e00bf3; case 29: return 0xd5a79147; case 30: return 0x06ca6351; case 31: return 0x14292967;
                case 32: return 0x27b70a85; case 33: return 0x2e1b2138; case 34: return 0x4d2c6dfc; case 35: return 0x53380d13;
                case 36: return 0x650a7354; case 37: return 0x766a0abb; case 38: return 0x81c2c92e; case 39: return 0x92722c85;
                case 40: return 0xa2bfe8a1; case 41: return 0xa81a664b; case 42: return 0xc24b8b70; case 43: return 0xc76c51a3;
                case 44: return 0xd192e819; case 45: return 0xd6990624; case 46: return 0xf40e3585; case 47: return 0x106aa070;
                case 48: return 0x19a4c116; case 49: return 0x1e376c08; case 50: return 0x2748774c; case 51: return 0x34b0bcb5;
                case 52: return 0x391c0cb3; case 53: return 0x4ed8aa4a; case 54: return 0x5b9cca4f; case 55: return 0x682e6ff3;
                case 56: return 0x748f82ee; case 57: return 0x78a5636f; case 58: return 0x84c87814; case 59: return 0x8cc70208;
                case 60: return 0x90befffa; case 61: return 0xa4506ceb; case 62: return 0xbef9a3f7; case 63: return 0xc67178f2;
            }
            return 0;
        }

        private uint[] HexToUIntArray(string hex)
        {
            uint[] parts = new uint[8];
            for (int i = 0; i < 8; i++)
            {
                string chunk = hex.Substring(i * 8, 8);
                parts[i] = Convert.ToUInt32(chunk, 16);
            }
            return parts;
        }

        private string DecodePassword(long index, string charset, int length)
        {
            char[] result = new char[length];
            int charsetLen = charset.Length;
            for (int i = 0; i < length; i++)
            {
                long powerOf = 1;
                for(int p=0; p < (length - 1 - i); p++) powerOf *= charsetLen;
                int charIndex = (int)((index / powerOf) % charsetLen);
                result[i] = charset[charIndex];
            }
            return new string(result);
        }
    }
}