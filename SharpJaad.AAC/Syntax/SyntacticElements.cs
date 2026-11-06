using SharpJaad.AAC.Filterbank;
using SharpJaad.AAC.Sbr;
using SharpJaad.AAC.Tools;
using System;

namespace SharpJaad.AAC.Syntax
{
    public class SyntacticElements
    {
        //global properties
        private DecoderConfig config;
        private bool sbrPresent, psPresent;
        private int bitsRead;
        private int frame = 0;
        //elements
        private PCE pce;
        private Element[] elements; //SCE, LFE and CPE
        private CCE[] cces;
        private DSE[] dses;
        private FIL[] fils;
        private int curElem, curCCE, curDSE, curFIL;
        private float[][] data;

        public SyntacticElements(DecoderConfig config)
        {
            this.config = config;

            pce = new PCE();
            elements = new Element[4 * Constants.MAX_ELEMENTS];
            cces = new CCE[Constants.MAX_ELEMENTS];
            dses = new DSE[Constants.MAX_ELEMENTS];
            fils = new FIL[Constants.MAX_ELEMENTS];

            StartNewFrame();
        }

        public void StartNewFrame()
        {
            curElem = 0;
            curCCE = 0;
            curDSE = 0;
            curFIL = 0;
            sbrPresent = false;
            psPresent = false;
            bitsRead = 0;
        }

        public void Decode(BitStream input)
        {
            ++frame;
            int start = input.GetPosition(); //should be 0

            int type;
            Element prev = null;
            bool content = true;
            if (!config.GetProfile().IsErrorResilientProfile())
            {
                while (content && (type = input.ReadBits(3)) != Constants.ELEMENT_END)
                {
                    switch (type)
                    {
                        case Constants.ELEMENT_SCE:
                        case Constants.ELEMENT_LFE:
                            //LOGGER.finest("SCE");
                            prev = DecodeSCE_LFE(input);
                            break;
                        case Constants.ELEMENT_CPE:
                            //LOGGER.finest("CPE");
                            prev = DecodeCPE(input);
                            break;
                        case Constants.ELEMENT_CCE:
                            //LOGGER.finest("CCE");
                            DecodeCCE(input);
                            prev = null;
                            break;
                        case Constants.ELEMENT_DSE:
                            //LOGGER.finest("DSE");
                            DecodeDSE(input);
                            prev = null;
                            break;
                        case Constants.ELEMENT_PCE:
                            //LOGGER.finest("PCE");
                            DecodePCE(input);
                            prev = null;
                            break;
                        case Constants.ELEMENT_FIL:
                            //LOGGER.finest("FIL");
                            DecodeFIL(input, prev);
                            prev = null;
                            break;
                    }
                }
                //LOGGER.finest("END");
                content = false;
                prev = null;
            }
            else
            {
                //error resilient raw data block
                switch (config.GetChannelConfiguration())
                {
                    case ChannelConfiguration.CHANNEL_CONFIG_MONO:
                        DecodeSCE_LFE(input);
                        break;
                    case ChannelConfiguration.CHANNEL_CONFIG_STEREO:
                        DecodeCPE(input);
                        break;
                    case ChannelConfiguration.CHANNEL_CONFIG_STEREO_PLUS_CENTER:
                        DecodeSCE_LFE(input);
                        DecodeCPE(input);
                        break;
                    case ChannelConfiguration.CHANNEL_CONFIG_STEREO_PLUS_CENTER_PLUS_REAR_MONO:
                        DecodeSCE_LFE(input);
                        DecodeCPE(input);
                        DecodeSCE_LFE(input);
                        break;
                    case ChannelConfiguration.CHANNEL_CONFIG_FIVE:
                        DecodeSCE_LFE(input);
                        DecodeCPE(input);
                        DecodeCPE(input);
                        break;
                    case ChannelConfiguration.CHANNEL_CONFIG_FIVE_PLUS_ONE:
                        DecodeSCE_LFE(input);
                        DecodeCPE(input);
                        DecodeCPE(input);
                        DecodeSCE_LFE(input);
                        break;
                    case ChannelConfiguration.CHANNEL_CONFIG_SEVEN_PLUS_ONE:
                        DecodeSCE_LFE(input);
                        DecodeCPE(input);
                        DecodeCPE(input);
                        DecodeCPE(input);
                        DecodeSCE_LFE(input);
                        break;
                    default:
                        throw new AACException("unsupported channel configuration for error resilience: " + config.GetChannelConfiguration());
                }
            }
            input.ByteAlign();

            bitsRead = input.GetPosition() - start;
        }

        private Element DecodeSCE_LFE(BitStream input)
        {
            if (elements[curElem] == null) elements[curElem] = new SCE_LFE(config);
            ((SCE_LFE)elements[curElem]).Decode(input, config);
            curElem++;
            return elements[curElem - 1];
        }

        private Element DecodeCPE(BitStream input)
        {
            if (elements[curElem] == null) elements[curElem] = new CPE(config);
            ((CPE)elements[curElem]).Decode(input, config);
            curElem++;
            return elements[curElem - 1];
        }

        private void DecodeCCE(BitStream input)
        {
            if (curCCE == Constants.MAX_ELEMENTS) throw new AACException("too much CCE elements");
            if (cces[curCCE] == null) cces[curCCE] = new CCE(config);
            cces[curCCE].Decode(input, config);
            curCCE++;
        }

        private void DecodeDSE(BitStream input)
        {
            if (curDSE == Constants.MAX_ELEMENTS) throw new AACException("too much CCE elements");
            if (dses[curDSE] == null) dses[curDSE] = new DSE();
            dses[curDSE].Decode(input);
            curDSE++;
        }

        private void DecodePCE(BitStream input)
        {
            pce.Decode(input);
            config.SetProfile(pce.GetProfile());
            config.SetSampleFrequency(pce.GetSampleFrequency());
            config.SetChannelConfiguration((ChannelConfiguration)pce.GetChannelCount());
        }

        private void DecodeFIL(BitStream input, Element prev)
        {
            if (curFIL == Constants.MAX_ELEMENTS) throw new AACException("too much FIL elements");
            if (fils[curFIL] == null) fils[curFIL] = new FIL(config.IsSBRDownSampled());
            fils[curFIL].Decode(input, prev, config.GetSampleFrequency(), config.IsSBREnabled(), config.IsSmallFrameUsed());
            curFIL++;

            if (prev != null && prev.IsSBRPresent())
            {
                sbrPresent = true;
                if (!psPresent && prev.GetSBR().IsPSUsed()) psPresent = true;
            }
        }

        public void Process(FilterBank filterBank)
        {
            Profile profile = config.GetProfile();
            SampleFrequency sf = config.GetSampleFrequency();
            //final ChannelConfiguration channels = config.getChannelConfiguration();

            int chs = (int)config.GetChannelConfiguration();
            if (chs == 1 && psPresent) chs++;
            int mult = sbrPresent ? 2 : 1;
            //only reallocate if needed
            if (data == null || chs != data.Length || mult * config.GetFrameLength() != data[0].Length)
            {
                data = new float[chs][];

                for (int i = 0; i < chs; i++)
                {
                    data[i] = new float[mult * config.GetFrameLength()];
                }
            }

            int channel = 0;
            Element e;
            SCE_LFE scelfe;
            CPE cpe;
            for (int i = 0; i < elements.Length && channel < chs; i++)
            {
                e = elements[i];
                if (e == null) continue;
                if (e is SCE_LFE)
                {
                    scelfe = (SCE_LFE)e;
                    channel += ProcessSingle(scelfe, filterBank, channel, profile, sf);
                }
                else if (e is CPE)
                {
                    cpe = (CPE)e;
                    ProcessPair(cpe, filterBank, channel, profile, sf);
                    channel += 2;
                }
                else if (e is CCE)
                {
                    //applies invquant and save the result in the CCE
                    ((CCE)e).Process();
                    channel++;
                }
            }
        }

        private int ProcessSingle(SCE_LFE scelfe, FilterBank filterBank, int channel, Profile profile, SampleFrequency sf)
        {
            ICStream ics = scelfe.GetICStream();
            ICSInfo info = ics.GetInfo();
            LTPrediction ltp = info.GetLTPrediction();
            int elementID = scelfe.GetElementInstanceTag();

            //inverse quantization
            float[] iqData = ics.GetInvQuantData();

            //prediction
            if (profile.Equals(Profile.AAC_MAIN) && info.IsICPredictionPresent()) info.GetICPrediction().Process(ics, iqData, sf);
            if (ltp != null) ltp.Process(ics, iqData, filterBank, sf);

            //dependent coupling
            processDependentCoupling(false, elementID, CCE.BEFORE_TNS, iqData, null);

            //TNS
            if (ics.IsTNSDataPresent()) ics.GetTNS().Process(ics, iqData, sf, false);

            //dependent coupling
            processDependentCoupling(false, elementID, CCE.AFTER_TNS, iqData, null);

            //filterbank
            filterBank.Process(info.GetWindowSequence(), info.GetWindowShape(ICSInfo.CURRENT), info.GetWindowShape(ICSInfo.PREVIOUS), iqData, data[channel], channel);

            if (ltp != null) ltp.UpdateState(data[channel], filterBank.GetOverlap(channel), profile);

            //dependent coupling
            ProcessIndependentCoupling(false, elementID, data[channel], null);

            //gain control
            if (ics.IsGainControlPresent()) ics.GetGainControl().Process(iqData, info.GetWindowShape(ICSInfo.CURRENT), info.GetWindowShape(ICSInfo.PREVIOUS), info.GetWindowSequence());

            //SBR
            int chs = 1;
            if (sbrPresent && config.IsSBREnabled())
            {
                //if(data[channel].Length==config.getFrameLength()) LOGGER.log(Level.WARNING, "SBR data present, but buffer has normal size!");
                SBR sbr = scelfe.GetSBR();
                if (sbr.IsPSUsed())
                {
                    chs = 2;
                    scelfe.GetSBR().ProcessPS(data[channel], data[channel + 1], false);
                }
                else
                    scelfe.GetSBR().Process(data[channel], false);
            }
            return chs;
        }

        private void ProcessPair(CPE cpe, FilterBank filterBank, int channel, Profile profile, SampleFrequency sf)
        {
            ICStream ics1 = cpe.GetLeftChannel();
            ICStream ics2 = cpe.GetRightChannel();
            ICSInfo info1 = ics1.GetInfo();
            ICSInfo info2 = ics2.GetInfo();
            LTPrediction ltp1 = info1.GetLTPrediction();
            LTPrediction ltp2 = info2.GetLTPrediction();
            int elementID = cpe.GetElementInstanceTag();

            //inverse quantization
            float[] iqData1 = ics1.GetInvQuantData();
            float[] iqData2 = ics2.GetInvQuantData();

            //MS
            if (cpe.IsCommonWindow() && cpe.IsMSMaskPresent()) SharpJaad.AAC.Tools.MS.Process(cpe, iqData1, iqData2);
            //main prediction
            if (profile.Equals(Profile.AAC_MAIN))
            {
                if (info1.IsICPredictionPresent()) info1.GetICPrediction().Process(ics1, iqData1, sf);
                if (info2.IsICPredictionPresent()) info2.GetICPrediction().Process(ics2, iqData2, sf);
            }
            //IS
            IS.Process(cpe, iqData1, iqData2);

            //LTP
            if (ltp1 != null) ltp1.Process(ics1, iqData1, filterBank, sf);
            if (ltp2 != null) ltp2.Process(ics2, iqData2, filterBank, sf);

            //dependent coupling
            processDependentCoupling(true, elementID, CCE.BEFORE_TNS, iqData1, iqData2);

            //TNS
            if (ics1.IsTNSDataPresent()) ics1.GetTNS().Process(ics1, iqData1, sf, false);
            if (ics2.IsTNSDataPresent()) ics2.GetTNS().Process(ics2, iqData2, sf, false);

            //dependent coupling
            processDependentCoupling(true, elementID, CCE.AFTER_TNS, iqData1, iqData2);

            //filterbank
            filterBank.Process(info1.GetWindowSequence(), info1.GetWindowShape(ICSInfo.CURRENT), info1.GetWindowShape(ICSInfo.PREVIOUS), iqData1, data[channel], channel);
            filterBank.Process(info2.GetWindowSequence(), info2.GetWindowShape(ICSInfo.CURRENT), info2.GetWindowShape(ICSInfo.PREVIOUS), iqData2, data[channel + 1], channel + 1);

            if (ltp1 != null) ltp1.UpdateState(data[channel], filterBank.GetOverlap(channel), profile);
            if (ltp2 != null) ltp2.UpdateState(data[channel + 1], filterBank.GetOverlap(channel + 1), profile);

            //independent coupling
            ProcessIndependentCoupling(true, elementID, data[channel], data[channel + 1]);

            //gain control
            if (ics1.IsGainControlPresent()) ics1.GetGainControl().Process(iqData1, info1.GetWindowShape(ICSInfo.CURRENT), info1.GetWindowShape(ICSInfo.PREVIOUS), info1.GetWindowSequence());
            if (ics2.IsGainControlPresent()) ics2.GetGainControl().Process(iqData2, info2.GetWindowShape(ICSInfo.CURRENT), info2.GetWindowShape(ICSInfo.PREVIOUS), info2.GetWindowSequence());

            //SBR
            if (sbrPresent && config.IsSBREnabled())
            {
                //if(data[channel].Length==config.getFrameLength()) LOGGER.log(Level.WARNING, "SBR data present, but buffer has normal size!");
                cpe.GetSBR().Process(data[channel], data[channel + 1], false);
            }
        }

        private void ProcessIndependentCoupling(bool channelPair, int elementID, float[] data1, float[] data2)
        {
            int index, c, chSelect;
            CCE cce;
            for (int i = 0; i < cces.Length; i++)
            {
                cce = cces[i];
                index = 0;
                if (cce != null && cce.GetCouplingPoint() == CCE.AFTER_IMDCT)
                {
                    for (c = 0; c <= cce.GetCoupledCount(); c++)
                    {
                        chSelect = cce.GetCHSelect(c);
                        if (cce.IsChannelPair(c) == channelPair && cce.GetIDSelect(c) == elementID)
                        {
                            if (chSelect != 1)
                            {
                                cce.ApplyIndependentCoupling(index, data1);
                                if (chSelect != 0) index++;
                            }
                            if (chSelect != 2)
                            {
                                cce.ApplyIndependentCoupling(index, data2);
                                index++;
                            }
                        }
                        else index += 1 + (chSelect == 3 ? 1 : 0);
                    }
                }
            }
        }

        private void processDependentCoupling(bool channelPair, int elementID, int couplingPoint, float[] data1, float[] data2)
        {
            int index, c, chSelect;
            CCE cce;
            for (int i = 0; i < cces.Length; i++)
            {
                cce = cces[i];
                index = 0;
                if (cce != null && cce.GetCouplingPoint() == couplingPoint)
                {
                    for (c = 0; c <= cce.GetCoupledCount(); c++)
                    {
                        chSelect = cce.GetCHSelect(c);
                        if (cce.IsChannelPair(c) == channelPair && cce.GetIDSelect(c) == elementID)
                        {
                            if (chSelect != 1)
                            {
                                cce.ApplyDependentCoupling(index, data1);
                                if (chSelect != 0) index++;
                            }
                            if (chSelect != 2)
                            {
                                cce.ApplyDependentCoupling(index, data2);
                                index++;
                            }
                        }
                        else
                            index += 1 + (chSelect == 3 ? 1 : 0);
                    }
                }
            }
        }

        public void SendToOutput(SampleBuffer buffer)
        {
            bool be = buffer.BigEndian;

            // always allocate at least two channels
            // mono can't be upgraded after implicit PS occures
            int chs = Math.Max(data.Length, 2);

            int mult = sbrPresent && config.IsSBREnabled() ? 2 : 1;
            int length = mult * config.GetFrameLength();
            int freq = mult * config.GetSampleFrequency().GetFrequency();

            byte[] b = buffer.Data;
            if (b.Length != chs * length * 2) b = new byte[chs * length * 2];

            float[] cur;
            int i, j, off;
            short s;
            for (i = 0; i < chs; i++)
            {
                // duplicate possible mono channel
                cur = data[i < data.Length ? i : 0];
                for (j = 0; j < length; j++)
                {
                    s = (short)Math.Max(Math.Min(Math.Round(cur[j]), short.MaxValue), short.MinValue);
                    off = (j * chs + i) * 2;
                    if (be)
                    {
                        b[off] = (byte)(s >> 8 & Constants.BYTE_MASK);
                        b[off + 1] = (byte)(s & Constants.BYTE_MASK);
                    }
                    else
                    {
                        b[off + 1] = (byte)(s >> 8 & Constants.BYTE_MASK);
                        b[off] = (byte)(s & Constants.BYTE_MASK);
                    }
                }
            }

            buffer.SetData(b, freq, chs, 16, bitsRead);
        }

        /// <summary>
        /// Resets the internal state for seeking without recreating the object.
        /// </summary>
        /// <remarks>
        /// <para><b>Industry Practice (FFmpeg):</b> <c>avcodec_flush_buffers</c>
        /// zeros internal buffers via <c>memset</c> without recreating
        /// <c>AVCodecContext</c>. A similar approach is used here.</para>
        ///
        /// <para><b>What is cleared:</b></para>
        /// <list type="bullet">
        ///   <item><c>elements[]</c> — accumulated state: <c>ICSInfo.windowShape[PREVIOUS]</c>,
        ///     <c>LTPrediction</c> state buffer, <c>ICPrediction</c> coefficients.
        ///     Setting to null allows lazy-recreation on the next <see cref="Decode"/> —
        ///     cost: 1 CPE (stereo) or 1 SCE (mono) allocation, ~0.1ms.</item>
        ///   <item><c>cces[], dses[], fils[]</c> — coupling/data/fill elements
        ///     (rarely used, but may contain stale references).</item>
        ///   <item><c>data[][]</c> — output PCM buffers are zeroed out rather than set to null
        ///     to avoid reallocation in <see cref="Process"/>.</item>
        ///   <item>Counters and frame — full reset of the position in the stream.</item>
        /// </list>
        ///
        /// <para><b>What is NOT cleared (intentionally):</b></para>
        /// <list type="bullet">
        ///   <item><c>FilterBank</c> — overlap-add buffers are cleared naturally
        ///     via frame skipping after seek (<c>SkipFramesAfterSeekAac</c>).
        ///     The first N frames are decoded but discarded, flushing out stale overlap.</item>
        ///   <item><c>config</c> — decoder configuration remains unchanged within the track.</item>
        /// </list>
        /// </remarks>
        public void Flush()
        {
            // Reset frame counters (same as StartNewFrame, but additionally frame=0)
            curElem = 0;
            curCCE = 0;
            curDSE = 0;
            curFIL = 0;
            sbrPresent = false;
            psPresent = false;
            bitsRead = 0;
            frame = 0;

            // Nullifying elements: clears accumulated prediction/LTP/coupling state.
            // Elements will be lazy-recreated on the next Decode() via
            // `if (elements[curElem] == null) elements[curElem] = new SCE_LFE(config);`
            // Cost: 1 object for stereo (CPE), 1 for mono (SCE_LFE). ~0.1ms.
            Array.Clear(elements, 0, elements.Length);
            Array.Clear(cces, 0, cces.Length);
            Array.Clear(dses, 0, dses.Length);
            Array.Clear(fils, 0, fils.Length);

            // Zero out PCM buffers without reallocation.
            // data[][] dimensions are checked in Process() and do not change
            // within a single track — no reallocation will occur.
            if (data != null)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    if (data[i] != null)
                        Array.Clear(data[i], 0, data[i].Length);
                }
            }
        }
    }
}
