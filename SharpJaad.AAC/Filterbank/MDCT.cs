using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpJaad.AAC;

namespace SharpJaad.AAC.Filterbank
{
    public class MDCT
    {
        private readonly int _N, _N2, _N4, _N8;
        private readonly float[] _sincosFlat;
        private readonly FFT _fft;
        private readonly float[] _buf;

        public MDCT(int length)
        {
            _N = length;
            _N2 = length >> 1;
            _N4 = length >> 2;
            _N8 = length >> 3;
            float[][] sincos = length switch
            {
                2048 => MDCTTables.MDCT_TABLE_2048,
                256 => MDCTTables.MDCT_TABLE_128,
                1920 => MDCTTables.MDCT_TABLE_1920,
                240 => MDCTTables.MDCT_TABLE_240,
                _ => throw new AACException("unsupported MDCT length: " + length)
            };

            _sincosFlat = new float[_N4 * 2];
            for (int i = 0; i < _N4; i++)
            {
                _sincosFlat[i * 2] = sincos[i][0];
                _sincosFlat[i * 2 + 1] = sincos[i][1];
            }

            _fft = new FFT(_N4);
            _buf = new float[_N4 * 2];
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] input, int inOff, float[] output, int outOff)
        {
            int n2 = _N2;
            int n4 = _N4;
            int n8 = _N8;

            ref float inRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(input), inOff);
            ref float outRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(output), outOff);
            ref float bufRef = ref MemoryMarshal.GetArrayDataReference(_buf);
            ref float scRef = ref MemoryMarshal.GetArrayDataReference(_sincosFlat);

            // Pre-IFFT complex multiplication
            for (int k = 0; k < n4; k++)
            {
                int k2 = k * 2;
                float c = Unsafe.Add(ref scRef, k2);
                float s = Unsafe.Add(ref scRef, k2 + 1);
                float a = Unsafe.Add(ref inRef, k2);
                float b = Unsafe.Add(ref inRef, n2 - 1 - k2);
                Unsafe.Add(ref bufRef, k2 + 1) = a * c + b * s;
                Unsafe.Add(ref bufRef, k2) = b * c - a * s;
            }

            // Complex IFFT, non-scaling
            _fft.ProcessFlat(_buf, false);

            // Post-IFFT complex multiplication
            for (int k = 0; k < n4; k++)
            {
                int k2 = k * 2;
                float c = Unsafe.Add(ref scRef, k2);
                float s = Unsafe.Add(ref scRef, k2 + 1);
                float t0 = Unsafe.Add(ref bufRef, k2);
                float t1 = Unsafe.Add(ref bufRef, k2 + 1);
                Unsafe.Add(ref bufRef, k2 + 1) = t1 * c + t0 * s;
                Unsafe.Add(ref bufRef, k2) = t0 * c - t1 * s;
            }

            // Reordering
            for (int k = 0; k < n8; k += 2)
            {
                int k2 = k * 2;
                int pN8pK = (n8 + k) * 2;
                int pN8m1mK = (n8 - 1 - k) * 2;
                int pN4m1mK = (n4 - 1 - k) * 2;

                Unsafe.Add(ref outRef, k2) = Unsafe.Add(ref bufRef, pN8pK + 1);
                Unsafe.Add(ref outRef, 2 + k2) = Unsafe.Add(ref bufRef, pN8pK + 3);

                Unsafe.Add(ref outRef, 1 + k2) = -Unsafe.Add(ref bufRef, pN8m1mK);
                Unsafe.Add(ref outRef, 3 + k2) = -Unsafe.Add(ref bufRef, pN8m1mK - 2);

                Unsafe.Add(ref outRef, n4 + k2) = Unsafe.Add(ref bufRef, k2);
                Unsafe.Add(ref outRef, n4 + 2 + k2) = Unsafe.Add(ref bufRef, k2 + 2);

                Unsafe.Add(ref outRef, n4 + 1 + k2) = -Unsafe.Add(ref bufRef, pN4m1mK + 1);
                Unsafe.Add(ref outRef, n4 + 3 + k2) = -Unsafe.Add(ref bufRef, pN4m1mK - 1);

                Unsafe.Add(ref outRef, n2 + k2) = Unsafe.Add(ref bufRef, pN8pK);
                Unsafe.Add(ref outRef, n2 + 2 + k2) = Unsafe.Add(ref bufRef, pN8pK + 2);

                Unsafe.Add(ref outRef, n2 + 1 + k2) = -Unsafe.Add(ref bufRef, pN8m1mK + 1);
                Unsafe.Add(ref outRef, n2 + 3 + k2) = -Unsafe.Add(ref bufRef, pN8m1mK - 1);

                Unsafe.Add(ref outRef, n2 + n4 + k2) = -Unsafe.Add(ref bufRef, k2 + 1);
                Unsafe.Add(ref outRef, n2 + n4 + 2 + k2) = -Unsafe.Add(ref bufRef, k2 + 3);

                Unsafe.Add(ref outRef, n2 + n4 + 1 + k2) = Unsafe.Add(ref bufRef, pN4m1mK);
                Unsafe.Add(ref outRef, n2 + n4 + 3 + k2) = Unsafe.Add(ref bufRef, pN4m1mK - 2);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void ProcessForward(float[] input, float[] output)
        {
            int n = _N;
            int n2 = _N2;
            int n4 = _N4;
            int n8 = _N8;

            ref float inRef = ref MemoryMarshal.GetArrayDataReference(input);
            ref float outRef = ref MemoryMarshal.GetArrayDataReference(output);
            ref float bufRef = ref MemoryMarshal.GetArrayDataReference(_buf);
            ref float scRef = ref MemoryMarshal.GetArrayDataReference(_sincosFlat);

            // Pre-FFT complex multiplication
            for (int k = 0; k < n8; k++)
            {
                int k2 = k * 2;
                float t0 = Unsafe.Add(ref inRef, n - n4 - 1 - k2) + Unsafe.Add(ref inRef, n - n4 + k2);
                float t1 = Unsafe.Add(ref inRef, n4 + k2) - Unsafe.Add(ref inRef, n4 - 1 - k2);

                float c0 = Unsafe.Add(ref scRef, k2);
                float s0 = Unsafe.Add(ref scRef, k2 + 1);
                Unsafe.Add(ref bufRef, k2) = (t0 * c0 + t1 * s0) * n;
                Unsafe.Add(ref bufRef, k2 + 1) = (t1 * c0 - t0 * s0) * n;

                float t2 = Unsafe.Add(ref inRef, n2 - 1 - k2) - Unsafe.Add(ref inRef, k2);
                float t3 = Unsafe.Add(ref inRef, n2 + k2) + Unsafe.Add(ref inRef, n - 1 - k2);

                int sc2Idx = (k + n8) * 2;
                float c1 = Unsafe.Add(ref scRef, sc2Idx);
                float s1 = Unsafe.Add(ref scRef, sc2Idx + 1);
                int b2Idx = (k + n8) * 2;
                Unsafe.Add(ref bufRef, b2Idx) = (t2 * c1 + t3 * s1) * n;
                Unsafe.Add(ref bufRef, b2Idx + 1) = (t3 * c1 - t2 * s1) * n;
            }

            // Complex FFT, non-scaling
            _fft.ProcessFlat(_buf, true);

            // Post-FFT complex multiplication
            for (int k = 0; k < n4; k++)
            {
                int k2 = k * 2;
                float b0 = Unsafe.Add(ref bufRef, k2);
                float b1 = Unsafe.Add(ref bufRef, k2 + 1);
                float c = Unsafe.Add(ref scRef, k2);
                float s = Unsafe.Add(ref scRef, k2 + 1);

                float tmp0 = b0 * c + b1 * s;
                float tmp1 = b1 * c - b0 * s;

                Unsafe.Add(ref outRef, k2) = -tmp0;
                Unsafe.Add(ref outRef, n2 - 1 - k2) = tmp1;
                Unsafe.Add(ref outRef, n2 + k2) = -tmp1;
                Unsafe.Add(ref outRef, n - 1 - k2) = tmp0;
            }
        }
    }
}
