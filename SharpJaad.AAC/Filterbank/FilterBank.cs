using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpJaad.AAC.Syntax;
using static SharpJaad.AAC.Syntax.ICSInfo;

namespace SharpJaad.AAC.Filterbank
{
    public class FilterBank
    {
        private float[][] _LONG_WINDOWS;// = {SINE_LONG, KBD_LONG};
        private float[][] _SHORT_WINDOWS;// = {SINE_SHORT, KBD_SHORT};
        private int _length;
        private int _shortLen;
        private int _mid;
        private int _trans;
        private MDCT _mdctShort, _mdctLong;
        private float[] _buf;
        private float[][] _overlaps;

        public FilterBank(bool smallFrames, int channels)
        {
            if (smallFrames)
            {
                _length = Constants.WINDOW_SMALL_LEN_LONG;
                _shortLen = Constants.WINDOW_SMALL_LEN_SHORT;
                _LONG_WINDOWS = new float[][] { SineWindows.SINE_960, KBDWindows.KBD_960 };
                _SHORT_WINDOWS = new float[][] { SineWindows.SINE_120, KBDWindows.KBD_120 };
            }
            else
            {
                _length = Constants.WINDOW_LEN_LONG;
                _shortLen = Constants.WINDOW_LEN_SHORT;
                _LONG_WINDOWS = new float[][] { SineWindows.SINE_1024, KBDWindows.KBD_1024 };
                _SHORT_WINDOWS = new float[][] { SineWindows.SINE_128, KBDWindows.KBD_128 };
            }
            _mid = (_length - _shortLen) / 2;
            _trans = _shortLen / 2;

            _mdctShort = new MDCT(_shortLen * 2);
            _mdctLong = new MDCT(_length * 2);

            int allocatedChannels = Math.Max(channels, 2);
            _overlaps = new float[allocatedChannels][];
            for (int i = 0; i < allocatedChannels; i++)
            {
                _overlaps[i] = new float[_length];
            }

            _buf = new float[2 * _length];
        }

        public void Process(WindowSequence windowSequence, int windowShape, int windowShapePrev, float[] input, float[] output, int channel)
        {
            int i;
            float[] overlap = _overlaps[channel];
            float[] buf = _buf;
            int len = _length;
            int shortLen = _shortLen;
            int mid = _mid;
            int trans = _trans;

            ref float outRef = ref MemoryMarshal.GetArrayDataReference(output);
            ref float ovlRef = ref MemoryMarshal.GetArrayDataReference(overlap);
            ref float bufRef = ref MemoryMarshal.GetArrayDataReference(buf);

            switch (windowSequence)
            {
                case WindowSequence.ONLY_LONG_SEQUENCE:
                    {
                        _mdctLong.Process(input, 0, buf, 0);
                        ref float wpRef = ref MemoryMarshal.GetArrayDataReference(_LONG_WINDOWS[windowShapePrev]);
                        ref float wcRef = ref MemoryMarshal.GetArrayDataReference(_LONG_WINDOWS[windowShape]);

                        MultiplyAddWindow(ref outRef, ref ovlRef, ref bufRef, ref wpRef, len);

                        int lastIdx = len - 1;
                        for (i = 0; i < len; i++)
                        {
                            Unsafe.Add(ref ovlRef, i) = Unsafe.Add(ref bufRef, len + i) * Unsafe.Add(ref wcRef, lastIdx - i);
                        }
                        break;
                    }

                case WindowSequence.LONG_START_SEQUENCE:
                    {
                        _mdctLong.Process(input, 0, buf, 0);
                        ref float wpRef = ref MemoryMarshal.GetArrayDataReference(_LONG_WINDOWS[windowShapePrev]);
                        ref float wsCurrRef = ref MemoryMarshal.GetArrayDataReference(_SHORT_WINDOWS[windowShape]);

                        MultiplyAddWindow(ref outRef, ref ovlRef, ref bufRef, ref wpRef, len);

                        buf.AsSpan(len, mid).CopyTo(overlap.AsSpan(0, mid));

                        int lastShortIdx = shortLen - 1;
                        int bufMidOffset = len + mid;
                        for (i = 0; i < shortLen; i++)
                        {
                            Unsafe.Add(ref ovlRef, mid + i) = Unsafe.Add(ref bufRef, bufMidOffset + i) * Unsafe.Add(ref wsCurrRef, lastShortIdx - i);
                        }

                        overlap.AsSpan(mid + shortLen, mid).Clear();
                        break;
                    }

                case WindowSequence.EIGHT_SHORT_SEQUENCE:
                    {
                        for (i = 0; i < 8; i++)
                        {
                            _mdctShort.Process(input, i * shortLen, buf, 2 * i * shortLen);
                        }

                        overlap.AsSpan(0, mid).CopyTo(output.AsSpan(0, mid));

                        ref float wsPrevRef = ref MemoryMarshal.GetArrayDataReference(_SHORT_WINDOWS[windowShapePrev]);
                        ref float wsCurrRef = ref MemoryMarshal.GetArrayDataReference(_SHORT_WINDOWS[windowShape]);
                        int lastShortIdx = shortLen - 1;

                        for (i = 0; i < shortLen; i++)
                        {
                            float wCurr = Unsafe.Add(ref wsCurrRef, i);
                            float wCurrRev = Unsafe.Add(ref wsCurrRef, lastShortIdx - i);

                            output[mid + i] = overlap[mid + i] + buf[i] * Unsafe.Add(ref wsPrevRef, i);
                            output[mid + shortLen + i] = overlap[mid + shortLen + i] + buf[shortLen + i] * wCurrRev + buf[shortLen * 2 + i] * wCurr;
                            output[mid + 2 * shortLen + i] = overlap[mid + shortLen * 2 + i] + buf[shortLen * 3 + i] * wCurrRev + buf[shortLen * 4 + i] * wCurr;
                            output[mid + 3 * shortLen + i] = overlap[mid + shortLen * 3 + i] + buf[shortLen * 5 + i] * wCurrRev + buf[shortLen * 6 + i] * wCurr;
                            if (i < trans)
                                output[mid + 4 * shortLen + i] = overlap[mid + shortLen * 4 + i] + buf[shortLen * 7 + i] * wCurrRev + buf[shortLen * 8 + i] * wCurr;
                        }

                        for (i = 0; i < shortLen; i++)
                        {
                            float wCurr = Unsafe.Add(ref wsCurrRef, i);
                            float wCurrRev = Unsafe.Add(ref wsCurrRef, lastShortIdx - i);

                            if (i >= trans)
                                overlap[mid + 4 * shortLen + i - len] = buf[shortLen * 7 + i] * wCurrRev + buf[shortLen * 8 + i] * wCurr;
                            overlap[mid + 5 * shortLen + i - len] = buf[shortLen * 9 + i] * wCurrRev + buf[shortLen * 10 + i] * wCurr;
                            overlap[mid + 6 * shortLen + i - len] = buf[shortLen * 11 + i] * wCurrRev + buf[shortLen * 12 + i] * wCurr;
                            overlap[mid + 7 * shortLen + i - len] = buf[shortLen * 13 + i] * wCurrRev + buf[shortLen * 14 + i] * wCurr;
                            overlap[mid + 8 * shortLen + i - len] = buf[shortLen * 15 + i] * wCurrRev;
                        }

                        overlap.AsSpan(mid + shortLen, mid).Clear();
                        break;
                    }

                case WindowSequence.LONG_STOP_SEQUENCE:
                    {
                        _mdctLong.Process(input, 0, buf, 0);
                        ref float wsPrevRef = ref MemoryMarshal.GetArrayDataReference(_SHORT_WINDOWS[windowShapePrev]);
                        ref float wcRef = ref MemoryMarshal.GetArrayDataReference(_LONG_WINDOWS[windowShape]);

                        overlap.AsSpan(0, mid).CopyTo(output.AsSpan(0, mid));

                        MultiplyAddWindow(
                            ref Unsafe.Add(ref outRef, mid),
                            ref Unsafe.Add(ref ovlRef, mid),
                            ref Unsafe.Add(ref bufRef, mid),
                            ref wsPrevRef,
                            shortLen);

                        int tailOffset = mid + shortLen;
                        for (i = 0; i < mid; i++)
                        {
                            Unsafe.Add(ref outRef, tailOffset + i) = Unsafe.Add(ref ovlRef, tailOffset + i) + Unsafe.Add(ref bufRef, tailOffset + i);
                        }

                        int lastIdx = len - 1;
                        for (i = 0; i < len; i++)
                        {
                            Unsafe.Add(ref ovlRef, i) = Unsafe.Add(ref bufRef, len + i) * Unsafe.Add(ref wcRef, lastIdx - i);
                        }
                        break;
                    }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MultiplyAddWindow(
            ref float outRef,
            ref float ovlRef,
            ref float bufRef,
            ref float winRef,
            int count)
        {
            int i = 0;
            if (Vector.IsHardwareAccelerated && count >= Vector<float>.Count)
            {
                int vecSize = Vector<float>.Count;
                for (; i <= count - vecSize; i += vecSize)
                {
                    var vOvl = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref ovlRef, i)));
                    var vBuf = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref bufRef, i)));
                    var vWin = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref winRef, i)));
                    Unsafe.WriteUnaligned(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref outRef, i)), vOvl + vBuf * vWin);
                }
            }
            for (; i < count; i++)
            {
                Unsafe.Add(ref outRef, i) = Unsafe.Add(ref ovlRef, i) + Unsafe.Add(ref bufRef, i) * Unsafe.Add(ref winRef, i);
            }
        }

        //only for LTP: no overlapping, no short blocks
        public void ProcessLTP(WindowSequence windowSequence, int windowShape, int windowShapePrev, float[] input, float[] output)
        {
            int i;

            switch (windowSequence)
            {
                case WindowSequence.ONLY_LONG_SEQUENCE:
                    for (i = _length - 1; i >= 0; i--)
                    {
                        _buf[i] = input[i] * _LONG_WINDOWS[windowShapePrev][i];
                        _buf[i + _length] = input[i + _length] * _LONG_WINDOWS[windowShape][_length - 1 - i];
                    }
                    break;

                case WindowSequence.LONG_START_SEQUENCE:
                    for (i = 0; i < _length; i++)
                    {
                        _buf[i] = input[i] * _LONG_WINDOWS[windowShapePrev][i];
                    }
                    for (i = 0; i < _mid; i++)
                    {
                        _buf[i + _length] = input[i + _length];
                    }
                    for (i = 0; i < _shortLen; i++)
                    {
                        _buf[i + _length + _mid] = input[i + _length + _mid] * _SHORT_WINDOWS[windowShape][_shortLen - 1 - i];
                    }
                    for (i = 0; i < _mid; i++)
                    {
                        _buf[i + _length + _mid + _shortLen] = 0;
                    }
                    break;

                case WindowSequence.LONG_STOP_SEQUENCE:
                    for (i = 0; i < _mid; i++)
                    {
                        _buf[i] = 0;
                    }
                    for (i = 0; i < _shortLen; i++)
                    {
                        _buf[i + _mid] = input[i + _mid] * _SHORT_WINDOWS[windowShapePrev][i];
                    }
                    for (i = 0; i < _mid; i++)
                    {
                        _buf[i + _mid + _shortLen] = input[i + _mid + _shortLen];
                    }
                    for (i = 0; i < _length; i++)
                    {
                        _buf[i + _length] = input[i + _length] * _LONG_WINDOWS[windowShape][_length - 1 - i];
                    }
                    break;
            }
            _mdctLong.ProcessForward(_buf, output);
        }

        public float[] GetOverlap(int channel)
        {
            return _overlaps[channel];
        }
    }
}
