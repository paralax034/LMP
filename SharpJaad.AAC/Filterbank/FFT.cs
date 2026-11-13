using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpJaad.AAC;

namespace SharpJaad.AAC.Filterbank
{
    public class FFT
    {
        private readonly int _length;
        private readonly float[] _rootsFwd;
        private readonly float[] _rootsInv;
        private readonly float[] _rev;

        public FFT(int length)
        {
            _length = length;

            float[,] roots = length switch
            {
                64 => FFTables.FFT_TABLE_64,
                512 => FFTables.FFT_TABLE_512,
                60 => FFTables.FFT_TABLE_60,
                480 => FFTables.FFT_TABLE_480,
                _ => throw new AACException("unexpected FFT length: " + length)
            };

            int rootCount = roots.GetLength(0);
            int rootCols = roots.GetLength(1);
            _rootsInv = new float[rootCount * 2];
            _rootsFwd = new float[rootCount * 2];
            for (int i = 0; i < rootCount; i++)
            {
                float re = roots[i, 0];
                float imInv = roots[i, 1];
                float imFwd = rootCols > 2 ? roots[i, 2] : -imInv;
                _rootsInv[i * 2] = re;
                _rootsInv[i * 2 + 1] = imInv;
                _rootsFwd[i * 2] = re;
                _rootsFwd[i * 2 + 1] = imFwd;
            }

            _rev = new float[length * 2];
        }

        public void Process(float[,] input, bool forward)
        {
            float[] flat = new float[_length * 2];
            for (int i = 0; i < _length; i++)
            {
                flat[i * 2] = input[i, 0];
                flat[i * 2 + 1] = input[i, 1];
            }

            ProcessFlat(flat, forward);

            for (int i = 0; i < _length; i++)
            {
                input[i, 0] = flat[i * 2];
                input[i, 1] = flat[i * 2 + 1];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal void ProcessFlat(float[] input, bool forward)
        {
            int len = _length;
            ref float inRef = ref MemoryMarshal.GetArrayDataReference(input);
            ref float revRef = ref MemoryMarshal.GetArrayDataReference(_rev);
            ref float rootsRef = ref MemoryMarshal.GetArrayDataReference(forward ? _rootsFwd : _rootsInv);

            // Bit-reversal
            int ii = 0;
            for (int i = 0; i < len; i++)
            {
                int i2 = i * 2;
                int ii2 = ii * 2;
                Unsafe.Add(ref revRef, i2) = Unsafe.Add(ref inRef, ii2);
                Unsafe.Add(ref revRef, i2 + 1) = Unsafe.Add(ref inRef, ii2 + 1);
                int k = len >> 1;
                while (ii >= k && k > 0)
                {
                    ii -= k;
                    k >>= 1;
                }
                ii += k;
            }
            _rev.AsSpan(0, len * 2).CopyTo(input.AsSpan(0, len * 2));

            // Bottom base-4 round
            for (int i = 0; i < len; i += 4)
            {
                int b0 = i * 2;
                float x0r = Unsafe.Add(ref inRef, b0);
                float x0i = Unsafe.Add(ref inRef, b0 + 1);
                float x1r = Unsafe.Add(ref inRef, b0 + 2);
                float x1i = Unsafe.Add(ref inRef, b0 + 3);
                float x2r = Unsafe.Add(ref inRef, b0 + 4);
                float x2i = Unsafe.Add(ref inRef, b0 + 5);
                float x3r = Unsafe.Add(ref inRef, b0 + 6);
                float x3i = Unsafe.Add(ref inRef, b0 + 7);

                float ar = x0r + x1r;
                float ai = x0i + x1i;
                float br = x2r + x3r;
                float bi = x2i + x3i;
                float cr = x0r - x1r;
                float ci = x0i - x1i;
                float dr = x2r - x3r;
                float di = x2i - x3i;

                Unsafe.Add(ref inRef, b0) = ar + br;
                Unsafe.Add(ref inRef, b0 + 1) = ai + bi;
                Unsafe.Add(ref inRef, b0 + 4) = ar - br;
                Unsafe.Add(ref inRef, b0 + 5) = ai - bi;

                float e1r = cr - di;
                float e1i = ci + dr;
                float e2r = cr + di;
                float e2i = ci - dr;

                if (forward)
                {
                    Unsafe.Add(ref inRef, b0 + 2) = e2r;
                    Unsafe.Add(ref inRef, b0 + 3) = e2i;
                    Unsafe.Add(ref inRef, b0 + 6) = e1r;
                    Unsafe.Add(ref inRef, b0 + 7) = e1i;
                }
                else
                {
                    Unsafe.Add(ref inRef, b0 + 2) = e1r;
                    Unsafe.Add(ref inRef, b0 + 3) = e1i;
                    Unsafe.Add(ref inRef, b0 + 6) = e2r;
                    Unsafe.Add(ref inRef, b0 + 7) = e2i;
                }
            }

            // Iterations from bottom to top
            for (int i = 4; i < len; i <<= 1)
            {
                int shift = i << 1;
                int m = len / shift;
                for (int j = 0; j < len; j += shift)
                {
                    for (int k = 0; k < i; k++)
                    {
                        int km2 = (k * m) * 2;
                        float rootRe = Unsafe.Add(ref rootsRef, km2);
                        float rootIm = Unsafe.Add(ref rootsRef, km2 + 1);

                        int idx1 = (i + j + k) * 2;
                        int idx0 = (j + k) * 2;
                        float in1Re = Unsafe.Add(ref inRef, idx1);
                        float in1Im = Unsafe.Add(ref inRef, idx1 + 1);
                        float zRe = in1Re * rootRe - in1Im * rootIm;
                        float zIm = in1Re * rootIm + in1Im * rootRe;

                        float in0Re = Unsafe.Add(ref inRef, idx0);
                        float in0Im = Unsafe.Add(ref inRef, idx0 + 1);
                        Unsafe.Add(ref inRef, idx1) = in0Re - zRe;
                        Unsafe.Add(ref inRef, idx1 + 1) = in0Im - zIm;
                        Unsafe.Add(ref inRef, idx0) = in0Re + zRe;
                        Unsafe.Add(ref inRef, idx0 + 1) = in0Im + zIm;
                    }
                }
            }
        }
    }
}
