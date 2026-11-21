using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Unit tests for WebM container corruption detection: misaligned seams, corrupt CuePoint seeks,
/// sync fast-path invalid EBML VINTs, and excessive zero-byte holes.
/// </summary>
public static class WebMParserTests
{
    [TestMethod(TestCategory.Unit, "WebMParser Rejects Misaligned Seam, Corrupt Seek, Zero Hole & Sync Invalid VINT", Group = TestGroups.Audio, Order = 11)]
    public static async Task TestWebMParserCorruptionDetectionAsync()
    {
        byte[] validWebm = BuildSyntheticWebM(out _, out long cluster2AbsolutePos);

        // 0. Sanity check: unmodified synthetic WebM parses both clusters and seeks cleanly
        using (var ms = new MemoryStream(validWebm))
        using (var containerParser = new Core.Audio.Parsers.WebMContainerParser(ms))
        {
            TestAssert.True(await containerParser.ParseHeadersAsync().ConfigureAwait(false), "Valid WebM headers must parse");
            TestAssert.Equal(1000L, containerParser.DurationMs);
            TestAssert.Equal(48000, containerParser.SampleRate);
            TestAssert.Equal(2, containerParser.Channels);

            var f1 = await containerParser.ReadNextFrameAsync().ConfigureAwait(false);
            var f2 = await containerParser.ReadNextFrameAsync().ConfigureAwait(false);
            var eof = await containerParser.ReadNextFrameAsync().ConfigureAwait(false);
            TestAssert.True(f1.HasValue && f1.Value.TimestampMs == 0, "Cluster 1 frame must decode at 0ms");
            TestAssert.True(f2.HasValue && f2.Value.TimestampMs == 200, "Cluster 2 frame must decode at 200ms");
            TestAssert.False(eof.HasValue, "Clean WebM must reach EOF after cluster 2");

            var seekPos = containerParser.FindSeekPosition(200);
            TestAssert.True(seekPos.HasValue, "CuePoint #2 must resolve");
            ms.Position = seekPos!.Value.BytePosition;
            containerParser.Reset();
            var seekedFrame = await containerParser.ReadNextFrameAsync().ConfigureAwait(false);
            TestAssert.True(seekedFrame.HasValue && seekedFrame.Value.TimestampMs == 200, "Seek to CuePoint #2 must decode cluster 2");
        }

        // 1. Shift cluster 2 by +3 bytes (hybrid file seam after ContentLength mismatch)
        byte[] shiftedWebm = new byte[validWebm.Length + 3];
        Buffer.BlockCopy(validWebm, 0, shiftedWebm, 0, (int)cluster2AbsolutePos);
        shiftedWebm[cluster2AbsolutePos] = 0x92;
        shiftedWebm[cluster2AbsolutePos + 1] = 0x4F;
        shiftedWebm[cluster2AbsolutePos + 2] = 0xFF;
        Buffer.BlockCopy(validWebm, (int)cluster2AbsolutePos, shiftedWebm, (int)cluster2AbsolutePos + 3, validWebm.Length - (int)cluster2AbsolutePos);

        // 1a. Sequential read across the seam must throw ParserCorruptionException
        using (var ms = new MemoryStream(shiftedWebm))
        using (var containerParser = new Core.Audio.Parsers.WebMContainerParser(ms))
        {
            TestAssert.True(await containerParser.ParseHeadersAsync().ConfigureAwait(false));
            var firstFrame = await containerParser.ReadNextFrameAsync().ConfigureAwait(false);
            TestAssert.True(firstFrame.HasValue, "First cluster frame must decode");

            bool threwCorruption = false;
            try
            {
                _ = await containerParser.ReadNextFrameAsync().ConfigureAwait(false);
            }
            catch (Core.Exceptions.ParserCorruptionException)
            {
                threwCorruption = true;
            }
            TestAssert.True(threwCorruption, "Oversized unknown EBML skip past EOF at seam must throw ParserCorruptionException instead of silent EOF");
        }

        // 1b. Seek to CuePoint #2 on shifted file must throw ParserCorruptionException
        using (var ms = new MemoryStream(shiftedWebm))
        using (var containerParser = new Core.Audio.Parsers.WebMContainerParser(ms))
        {
            TestAssert.True(await containerParser.ParseHeadersAsync().ConfigureAwait(false));
            var seekPos = containerParser.FindSeekPosition(200);
            TestAssert.True(seekPos.HasValue, "CuePoint seek position must resolve");

            ms.Position = seekPos!.Value.BytePosition;
            containerParser.Reset();

            bool threwCorruption = false;
            try
            {
                _ = await containerParser.ReadNextFrameAsync().ConfigureAwait(false);
            }
            catch (Core.Exceptions.ParserCorruptionException)
            {
                threwCorruption = true;
            }
            TestAssert.True(threwCorruption, "Seek landing on non-Cluster bytes must throw ParserCorruptionException instead of silent EOF");
        }

        // 2. Sync fast-path invalid EBML ID VINT length must wrap into ParserCorruptionException
        byte[] syncCorruptWebm = (byte[])validWebm.Clone();
        syncCorruptWebm[cluster2AbsolutePos] = 0x08;
        using (var ms = new MemoryStream(syncCorruptWebm))
        using (var parser = new Core.Audio.Helpers.WebMParser(ms))
        {
            TestAssert.True(await parser.ParseHeadersAsync().ConfigureAwait(false));
            var b1 = await parser.ReadNextBlockAsync().ConfigureAwait(false);
            TestAssert.True(b1.HasValue, "First WebM block must decode");
            b1!.Value.Owner.Dispose();

            bool threwCorruption = false;
            try
            {
                _ = await parser.ReadNextBlockAsync().ConfigureAwait(false);
            }
            catch (Core.Exceptions.ParserCorruptionException)
            {
                threwCorruption = true;
            }
            TestAssert.True(threwCorruption, "Sync fast-path Invalid EBML ID VINT length must throw ParserCorruptionException, not raw InvalidDataException");
        }

        // 3. Excessive zero-byte hole (>16 consecutive 0x00) must throw ParserCorruptionException
        byte[] zeroHoleWebm = new byte[validWebm.Length + 24];
        Buffer.BlockCopy(validWebm, 0, zeroHoleWebm, 0, (int)cluster2AbsolutePos);
        Buffer.BlockCopy(validWebm, (int)cluster2AbsolutePos, zeroHoleWebm, (int)cluster2AbsolutePos + 24, validWebm.Length - (int)cluster2AbsolutePos);
        using (var ms = new MemoryStream(zeroHoleWebm))
        using (var parser = new Core.Audio.Helpers.WebMParser(ms))
        {
            TestAssert.True(await parser.ParseHeadersAsync().ConfigureAwait(false));
            var b1 = await parser.ReadNextBlockAsync().ConfigureAwait(false);
            TestAssert.True(b1.HasValue);
            b1!.Value.Owner.Dispose();

            bool threwCorruption = false;
            try
            {
                _ = await parser.ReadNextBlockAsync().ConfigureAwait(false);
            }
            catch (Core.Exceptions.ParserCorruptionException)
            {
                threwCorruption = true;
            }
            TestAssert.True(threwCorruption, "Zero-filled hole (>16 zero bytes) must throw ParserCorruptionException");
        }
    }

    internal static byte[] BuildSyntheticWebM(out long cluster2SegmentOffset, out long cluster2AbsolutePos)
    {
        using var ms = new MemoryStream();

        static void WriteBytes(Stream s, ReadOnlySpan<byte> bytes) => s.Write(bytes);
        static void WriteUInt16BE(Stream s, ushort v)
        {
            Span<byte> b = stackalloc byte[2];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(b, v);
            s.Write(b);
        }
        static void WriteSingleBE(Stream s, float v)
        {
            Span<byte> b = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b, v);
            s.Write(b);
        }

        // EBML (0x1A45DFA3), size 0 (0x80)
        WriteBytes(ms, [0x1A, 0x45, 0xDF, 0xA3, 0x80]);

        // Segment (0x18538067), unknown size (0xFF)
        WriteBytes(ms, [0x18, 0x53, 0x80, 0x67, 0xFF]);
        long segmentStart = ms.Position;

        // Info (0x1549A966): TimecodeScale (7B) + Duration (7B) => 14 bytes (0x8E)
        WriteBytes(ms, [0x15, 0x49, 0xA9, 0x66, 0x8E]);
        WriteBytes(ms, [0x2A, 0xD7, 0xB1, 0x83, 0x0F, 0x42, 0x40]);
        WriteBytes(ms, [0x44, 0x89, 0x84]);
        WriteSingleBE(ms, 1000.0f);

        // Tracks (0x1654AE6B) -> TrackEntry (0xAE)
        WriteBytes(ms, [0x16, 0x54, 0xAE, 0x6B, 0x93]);
        WriteBytes(ms, [0xAE, 0x91]);
        WriteBytes(ms, [0xD7, 0x81, 0x01, 0x83, 0x81, 0x02]);
        WriteBytes(ms, [0xE1, 0x89, 0xB5, 0x84]);
        WriteSingleBE(ms, 48000.0f);
        WriteBytes(ms, [0x9F, 0x81, 0x02]);

        // Cues placeholder (29 bytes)
        long cuesHeaderPos = ms.Position;
        WriteBytes(ms, new byte[29]);

        long cluster1SegmentOffset = ms.Position - segmentStart;
        WriteBytes(ms, [0x1F, 0x43, 0xB6, 0x75, 0xFF]);
        WriteBytes(ms, [0xE7, 0x81, 0x00]);
        WriteBytes(ms, [0xA3, 0x88, 0x81, 0x00, 0x00, 0x80, 0xAA, 0xBB, 0xCC, 0xDD]);

        cluster2AbsolutePos = ms.Position;
        cluster2SegmentOffset = ms.Position - segmentStart;
        WriteBytes(ms, [0x1F, 0x43, 0xB6, 0x75, 0xFF]);
        WriteBytes(ms, [0xE7, 0x81, 0xC8]);
        WriteBytes(ms, [0xA3, 0x88, 0x81, 0x00, 0x00, 0x80, 0x11, 0x22, 0x33, 0x44]);

        long endPos = ms.Position;
        ms.Position = cuesHeaderPos;
        WriteBytes(ms, [0x1C, 0x53, 0xBB, 0x6B, 0x98]);
        WriteBytes(ms, [0xBB, 0x8A, 0xB3, 0x82]);
        WriteUInt16BE(ms, 0);
        WriteBytes(ms, [0xB7, 0x84, 0xF1, 0x82]);
        WriteUInt16BE(ms, (ushort)cluster1SegmentOffset);
        WriteBytes(ms, [0xBB, 0x8A, 0xB3, 0x82]);
        WriteUInt16BE(ms, 200);
        WriteBytes(ms, [0xB7, 0x84, 0xF1, 0x82]);
        WriteUInt16BE(ms, (ushort)cluster2SegmentOffset);
        ms.Position = endPos;

        return ms.ToArray();
    }
}