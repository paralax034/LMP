using SharpJaad.AAC.Filterbank;
using SharpJaad.AAC.Syntax;
using SharpJaad.AAC.Transport;
using System;
using System.ComponentModel;

namespace SharpJaad.AAC
{
    /// <summary>
    /// AAC Decoder ported from JAAD: https://sourceforge.net/projects/jaadec/
    /// </summary>
    public class Decoder
    {
        /*
        static Decoder()
		{
			foreach (Handler h in LOGGER.getHandlers())
			{
				LOGGER.removeHandler(h);
			}
			LOGGER.setLevel(Level.WARNING);

			ConsoleHandler h = new ConsoleHandler();
			h.setLevel(Level.ALL);
			LOGGER.addHandler(h);
		}
		*/

        private DecoderConfig _config;
        private SyntacticElements _syntacticElements;
        private FilterBank _filterBank;
        private BitStream _input;
        private ADIFHeader _adifHeader;

        /// <summary>
        /// The methods returns true, if a profile is supported by the decoder.
        /// </summary>
        /// <param name="profile">An AAC profile.</param>
        /// <returns>true if the specified profile can be decoded</returns>
        public static bool CanDecode(Profile profile)
        {
            return profile.IsDecodingSupported();
        }

        /// <summary>
        /// Initializes the decoder with a MP4 decoder specific info. After this the MP4 frames can be passed to the decodeFrame(byte[], SampleBuffer) method to decode them.
        /// </summary>
        /// <param name="decoderSpecificInfo">A byte array containing the decoder specific info from an MP4 container.</param>
        /// <exception cref="InvalidEnumArgumentException"></exception>
        /// <exception cref="AACException">If the specified profile is not supported.</exception>
        public Decoder(byte[] decoderSpecificInfo)
        {
            _config = DecoderConfig.ParseMP4DecoderSpecificInfo(decoderSpecificInfo);
            if (_config == null) throw new InvalidEnumArgumentException("illegal MP4 decoder specific info");

            if (!CanDecode(_config.GetProfile())) throw new AACException("unsupported profile: " + _config.GetProfile());

            _syntacticElements = new SyntacticElements(_config);
            _filterBank = new FilterBank(_config.IsSmallFrameUsed(), (int)_config.GetChannelConfiguration());

            _input = new BitStream();

            //LOGGER.log(Level.FINE, "profile: {0}", config.getProfile());
            //LOGGER.log(Level.FINE, "sf: {0}", config.getSampleFrequency().getFrequency());
            //LOGGER.log(Level.FINE, "channels: {0}", config.getChannelConfiguration().getDescription());
        }

        public Decoder(DecoderConfig cfg)
        {
            _config = cfg ?? throw new InvalidEnumArgumentException("illegal MP4 decoder specific info");

            if (!CanDecode(_config.GetProfile())) throw new AACException("unsupported profile: " + _config.GetProfile());

            _syntacticElements = new SyntacticElements(_config);
            _filterBank = new FilterBank(_config.IsSmallFrameUsed(), (int)_config.GetChannelConfiguration());

            _input = new BitStream();

            //LOGGER.log(Level.FINE, "profile: {0}", config.getProfile());
            //LOGGER.log(Level.FINE, "sf: {0}", config.getSampleFrequency().getFrequency());
            //LOGGER.log(Level.FINE, "channels: {0}", config.getChannelConfiguration().getDescription());
        }

        public DecoderConfig GetConfig()
        {
            return _config;
        }

        /// <summary>
        /// Decodes one frame of AAC data in frame mode and returns the raw PCM.
        /// </summary>
        /// <param name="frame">The AAC frame.</param>
        /// <param name="buffer">A buffer to hold the decoded PCM data.</param>
		/// <exception cref="AACException">if decoding fails</exception>
        public void DecodeFrame(byte[] frame, SampleBuffer buffer)
        {
            if (frame != null) _input.SetData(frame);
            try
            {
                Decode(buffer);
            }
            catch (AACException e)
            {
                if (!e.IsEndOfStream)
                    throw;
                //else LOGGER.warning("unexpected end of frame");
            }
        }

        private void Decode(SampleBuffer buffer)
        {
            if (ADIFHeader.IsPresent(_input))
            {
                _adifHeader = ADIFHeader.ReadHeader(_input);
                PCE pce = _adifHeader.GetFirstPCE();
                _config.SetProfile(pce.GetProfile());
                _config.SetSampleFrequency(pce.GetSampleFrequency());
                _config.SetChannelConfiguration((ChannelConfiguration)pce.GetChannelCount());
            }

            if (!CanDecode(_config.GetProfile())) throw new AACException("unsupported profile: " + _config.GetProfile());

            _syntacticElements.StartNewFrame();

            try
            {
                //1: bitstream parsing and noiseless coding
                _syntacticElements.Decode(_input);
                //2: spectral processing
                _syntacticElements.Process(_filterBank);
                //3: send to output buffer
                _syntacticElements.SendToOutput(buffer);
            }
            catch (AACException)
            {
                buffer.SetData(new byte[0], 0, 0, 0, 0);
                throw;
            }
            catch (Exception e)
            {
                buffer.SetData(new byte[0], 0, 0, 0, 0);
                throw new AACException(e.Message);
            }
        }

        /// <summary>
        /// Resets the internal state of the decoder for seeking without recreation.
        /// </summary>
        /// <remarks>
        /// <para><b>FFmpeg equivalent of <c>avcodec_flush_buffers</c>.</b></para>
        /// <para>Resets <see cref="SyntacticElements"/> (prediction/LTP/coupling state)
        /// and <see cref="BitStream"/>. <see cref="FilterBank"/> is left untouched —
        /// the overlap-add state is cleared naturally via skip-frames.</para>
        ///
        /// <para><b>What is preserved:</b></para>
        /// <list type="bullet">
        ///   <item><see cref="DecoderConfig"/> — profile, sample rate, channels remain unchanged.</item>
        ///   <item><see cref="FilterBank"/> — MDCT tables, FFT tables.
        ///     Overlap buffers are flushed by skip-frames in ~2ms.</item>
        /// </list>
        ///
        /// <para><b>Cost vs recreate:</b></para>
        /// <list type="bullet">
        ///   <item>Flush: ~4 <c>Array.Clear</c> + lazy recreate 1 Element = ~0.1ms, 1 allocation</item>
        ///   <item>Recreate: <c>new Decoder(config)</c> = ~0.3ms, 5+ allocations
        ///     (SyntacticElements, FilterBank, BitStream, Element arrays, MDCT tables)</item>
        ///   <item>Full recreate: <c>ParseMP4DecoderSpecificInfo</c> + <c>new Decoder</c>
        ///     + <c>new SampleBuffer</c> = ~2ms, 10+ allocations</item>
        /// </list>
        /// </remarks>
        public void Flush()
        {
            _syntacticElements.Flush();
            _input.SetData(Array.Empty<byte>());
        }
    }
}