using System.Buffers.Binary;

namespace Krakarr.Media.Files;

/// <summary>What the HEVC/AVC bitstream says beyond the container: bit depth, transfer characteristics (H.273) and HDR10+.</summary>
internal sealed class VideoStreamDetails
{
    public int? BitDepth;
    public ulong Transfer;
    public bool Hdr10Plus;
    /// <summary>Size of the NAL unit length prefix in samples/blocks (from hvcC/avcC); 4 when unknown.</summary>
    public int LengthSize = 4;
}

/// <summary>
/// Reads the sequence parameter set (bit depth, VUI transfer characteristics) of HEVC and AVC streams from their
/// decoder configuration records (hvcC/avcC, also Matroska's CodecPrivate) and scans the start of the first frame
/// for the in-band parameter sets and the HDR10+ SEI. Only the bytes it is handed are read; callers bound them.
/// </summary>
internal static class VideoBitstream
{
    /// <summary>Most bytes of a parameter set or SEI unescaped; real ones are well under 1 KB.</summary>
    private const int MaxNalBytes = 16 * 1024;

    /// <summary>Details from an HEVC decoder configuration record (hvcC).</summary>
    public static VideoStreamDetails Hevc(ReadOnlySpan<byte> hvcC)
    {
        var details = new VideoStreamDetails();
        if (hvcC.Length < 23 || hvcC[0] != 1) return details;
        details.LengthSize = (hvcC[21] & 0x03) + 1;
        var pos = 23;
        for (var array = 0; array < hvcC[22] && pos + 3 <= hvcC.Length; array++)
        {
            var type = hvcC[pos] & 0x3F;
            int count = BinaryPrimitives.ReadUInt16BigEndian(hvcC[(pos + 1)..]);
            pos += 3;
            for (var i = 0; i < count && pos + 2 <= hvcC.Length; i++)
            {
                int length = BinaryPrimitives.ReadUInt16BigEndian(hvcC[pos..]);
                pos += 2;
                if (pos + length > hvcC.Length) return details;
                if (type == 33 && details.BitDepth is null) ReadHevcSps(hvcC.Slice(pos, length), details);
                pos += length;
            }
        }

        return details;
    }

    /// <summary>Details from an AVC decoder configuration record (avcC).</summary>
    public static VideoStreamDetails Avc(ReadOnlySpan<byte> avcC)
    {
        var details = new VideoStreamDetails();
        if (avcC.Length < 7 || avcC[0] != 1) return details;
        details.LengthSize = (avcC[4] & 0x03) + 1;
        var count = avcC[5] & 0x1F;
        var pos = 6;
        for (var i = 0; i < count && pos + 2 <= avcC.Length; i++)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(avcC[pos..]);
            pos += 2;
            if (pos + length > avcC.Length) break;
            if (details.BitDepth is null) ReadAvcSps(avcC.Slice(pos, length), details);
            pos += length;
        }

        return details;
    }

    /// <summary>
    /// Scans the length-prefixed NAL units at the start of a frame (possibly cut off) up to its first slice: in-band
    /// parameter sets fill what the configuration record lacked, and HEVC prefix SEI flag HDR10+.
    /// </summary>
    public static void ScanFrame(ReadOnlySpan<byte> frame, bool hevc, VideoStreamDetails details)
    {
        var lengthSize = details.LengthSize;
        if (lengthSize is < 1 or > 4) return;
        var pos = 0;
        while (pos + lengthSize < frame.Length)
        {
            uint length = 0;
            for (var i = 0; i < lengthSize; i++) length = (length << 8) | frame[pos + i];
            pos += lengthSize;
            if (length == 0) continue;
            var nal = frame.Slice(pos, (int)Math.Min(length, (uint)(frame.Length - pos)));
            if (hevc)
            {
                var type = (nal[0] >> 1) & 0x3F;
                if (type < 32) return; // a slice: the SEI and parameter sets come before it
                if (type == 33 && details.BitDepth is null) ReadHevcSps(nal, details);
                else if (type == 39 && IsHdr10PlusSei(nal)) details.Hdr10Plus = true;
            }
            else
            {
                var type = nal[0] & 0x1F;
                if (type is >= 1 and <= 5) return;
                if (type == 7 && details.BitDepth is null) ReadAvcSps(nal, details);
            }

            if (length > (uint)(frame.Length - pos)) return;
            pos += (int)length;
        }
    }

    /// <summary>NAL payload with the emulation prevention bytes (00 00 03) removed.</summary>
    private static byte[] Unescape(ReadOnlySpan<byte> nal)
    {
        nal = nal[..Math.Min(nal.Length, MaxNalBytes)];
        var output = new byte[nal.Length];
        var n = 0;
        var zeros = 0;
        foreach (var b in nal)
        {
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }

            zeros = b == 0 ? zeros + 1 : 0;
            output[n++] = b;
        }

        return output.AsSpan(0, n).ToArray();
    }

    // ---- HEVC (ITU-T H.265 7.3.2.2) ------------------------------------------------------------------------------

    internal static void ReadHevcSps(ReadOnlySpan<byte> nal, VideoStreamDetails details)
    {
        if (nal.Length < 3) return;
        var r = new BitReader(Unescape(nal[2..]));
        try
        {
            r.Skip(4); // sps_video_parameter_set_id
            var maxSubLayersMinus1 = (int)r.Bits(3);
            r.Skip(1);
            SkipProfileTierLevel(ref r, maxSubLayersMinus1);
            r.Ue(); // sps_seq_parameter_set_id
            if (r.Ue() == 3) r.Skip(1); // chroma_format_idc, separate_colour_plane_flag
            r.Ue();
            r.Ue(); // pic width/height
            if (r.Flag()) { r.Ue(); r.Ue(); r.Ue(); r.Ue(); } // conformance window
            var bitDepthLuma = r.Ue() + 8;
            if (bitDepthLuma <= 16) details.BitDepth = (int)bitDepthLuma;
            r.Ue(); // bit_depth_chroma_minus8
            var log2MaxPocLsb = (int)r.Ue() + 4;
            if (log2MaxPocLsb > 16) return;
            var orderingInfo = r.Flag();
            for (var i = orderingInfo ? 0 : maxSubLayersMinus1; i <= maxSubLayersMinus1; i++) { r.Ue(); r.Ue(); r.Ue(); }
            for (var i = 0; i < 6; i++) r.Ue(); // coding/transform block sizes and hierarchy depths
            if (r.Flag() && r.Flag()) SkipHevcScalingListData(ref r);
            r.Skip(2); // amp_enabled_flag, sample_adaptive_offset_enabled_flag
            if (r.Flag()) { r.Skip(8); r.Ue(); r.Ue(); r.Skip(1); } // PCM
            if (!SkipShortTermRefPicSets(ref r)) return;
            if (r.Flag())
            {
                var longTerm = r.Ue();
                if (longTerm > 32) return;
                for (var i = 0u; i < longTerm; i++) r.Skip(log2MaxPocLsb + 1);
            }

            r.Skip(2); // sps_temporal_mvp_enabled_flag, strong_intra_smoothing_enabled_flag
            if (r.Flag()) details.Transfer = ReadVuiTransfer(ref r) ?? details.Transfer;
        }
        catch (EndOfStreamException)
        {
        }
    }

    private static void SkipProfileTierLevel(ref BitReader r, int maxSubLayersMinus1)
    {
        r.Skip(96); // general profile space/tier/idc, compatibility flags, constraint flags, level
        Span<bool> profilePresent = stackalloc bool[8];
        Span<bool> levelPresent = stackalloc bool[8];
        for (var i = 0; i < maxSubLayersMinus1; i++)
        {
            profilePresent[i] = r.Flag();
            levelPresent[i] = r.Flag();
        }

        if (maxSubLayersMinus1 > 0) r.Skip(2 * (8 - maxSubLayersMinus1));
        for (var i = 0; i < maxSubLayersMinus1; i++)
        {
            if (profilePresent[i]) r.Skip(88);
            if (levelPresent[i]) r.Skip(8);
        }
    }

    private static void SkipHevcScalingListData(ref BitReader r)
    {
        for (var sizeId = 0; sizeId < 4; sizeId++)
        {
            for (var matrixId = 0; matrixId < 6; matrixId += sizeId == 3 ? 3 : 1)
            {
                if (!r.Flag())
                {
                    r.Ue(); // scaling_list_pred_matrix_id_delta
                    continue;
                }

                var coefficients = Math.Min(64, 1 << (4 + (sizeId << 1)));
                if (sizeId > 1) r.Ue(); // scaling_list_dc_coef_minus8 (se, same length)
                for (var i = 0; i < coefficients; i++) r.Ue(); // scaling_list_delta_coef (se)
            }
        }
    }

    /// <summary>Skips st_ref_pic_set() for each set (7.3.7); false when the values are out of range.</summary>
    private static bool SkipShortTermRefPicSets(ref BitReader r)
    {
        var count = r.Ue();
        if (count > 64) return false;
        Span<int> deltaPocs = stackalloc int[65];
        for (var i = 0; i < (int)count; i++)
        {
            if (i != 0 && r.Flag())
            {
                // inter_ref_pic_set_prediction_flag: predicted from the previous set.
                r.Skip(1);
                r.Ue();
                var n = 0;
                for (var j = 0; j <= deltaPocs[i - 1]; j++)
                {
                    var used = r.Flag();
                    if (used || r.Flag()) n++;
                }

                deltaPocs[i] = n;
            }
            else
            {
                var negative = r.Ue();
                var positive = r.Ue();
                if (negative > 16 || positive > 16) return false;
                for (var j = 0u; j < negative + positive; j++)
                {
                    r.Ue();
                    r.Skip(1);
                }

                deltaPocs[i] = (int)(negative + positive);
            }
        }

        return true;
    }

    /// <summary>
    /// HDR10+ (SMPTE ST 2094-40) is carried in a user_data_registered_itu_t_t35 SEI: country 0xB5, provider 0x003C,
    /// provider oriented code 0x0001, application identifier 4.
    /// </summary>
    private static bool IsHdr10PlusSei(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 3) return false;
        var sei = Unescape(nal[2..]);
        var pos = 0;
        while (pos < sei.Length && sei[pos] != 0x80) // rbsp_trailing_bits
        {
            if (!TrySeiValue(sei, ref pos, out var type) || !TrySeiValue(sei, ref pos, out var size)) return false;
            if (size > sei.Length - pos) size = sei.Length - pos;
            var payload = sei.AsSpan(pos, size);
            if (type == 4 && payload.Length >= 6 && payload[0] == 0xB5
                && BinaryPrimitives.ReadUInt16BigEndian(payload[1..]) == 0x003C
                && BinaryPrimitives.ReadUInt16BigEndian(payload[3..]) == 0x0001
                && payload[5] == 4)
            {
                return true;
            }

            pos += size;
        }

        return false;
    }

    private static bool TrySeiValue(byte[] sei, ref int pos, out int value)
    {
        value = 0;
        while (pos < sei.Length)
        {
            var b = sei[pos++];
            value += b;
            if (b != 0xFF) return true;
            if (value > MaxNalBytes) return false;
        }

        return false;
    }

    // ---- AVC (ITU-T H.264 7.3.2.1.1) -----------------------------------------------------------------------------

    internal static void ReadAvcSps(ReadOnlySpan<byte> nal, VideoStreamDetails details)
    {
        if (nal.Length < 4) return;
        var r = new BitReader(Unescape(nal[1..]));
        try
        {
            var profile = r.Bits(8);
            r.Skip(16); // constraint flags, level_idc
            r.Ue(); // seq_parameter_set_id
            var bitDepth = 8;
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                var chroma = r.Ue();
                if (chroma == 3) r.Skip(1);
                bitDepth = (int)Math.Min(r.Ue() + 8, 17);
                r.Ue(); // bit_depth_chroma_minus8
                r.Skip(1); // qpprime_y_zero_transform_bypass_flag
                if (r.Flag())
                {
                    for (var i = 0; i < (chroma != 3 ? 8 : 12); i++)
                    {
                        if (r.Flag()) SkipAvcScalingList(ref r, i < 6 ? 16 : 64);
                    }
                }
            }

            if (bitDepth <= 16) details.BitDepth = bitDepth;
            r.Ue(); // log2_max_frame_num_minus4
            var pocType = r.Ue();
            if (pocType == 0) r.Ue();
            else if (pocType == 1)
            {
                r.Skip(1);
                r.Ue();
                r.Ue();
                var cycle = r.Ue();
                if (cycle > 255) return;
                for (var i = 0u; i < cycle; i++) r.Ue();
            }

            r.Ue(); // max_num_ref_frames
            r.Skip(1);
            r.Ue();
            r.Ue(); // width/height in macroblocks
            if (!r.Flag()) r.Skip(1); // frame_mbs_only_flag, mb_adaptive_frame_field_flag
            r.Skip(1); // direct_8x8_inference_flag
            if (r.Flag()) { r.Ue(); r.Ue(); r.Ue(); r.Ue(); } // frame cropping
            if (r.Flag()) details.Transfer = ReadVuiTransfer(ref r) ?? details.Transfer;
        }
        catch (EndOfStreamException)
        {
        }
    }

    private static void SkipAvcScalingList(ref BitReader r, int size)
    {
        int last = 8, next = 8;
        for (var j = 0; j < size; j++)
        {
            if (next != 0)
            {
                var delta = r.Se();
                next = (int)((last + delta + 256) % 256);
            }

            last = next == 0 ? last : next;
        }
    }

    /// <summary>The start of vui_parameters(), common to HEVC and AVC, up to transfer_characteristics.</summary>
    private static ulong? ReadVuiTransfer(ref BitReader r)
    {
        if (r.Flag() && r.Bits(8) == 255) r.Skip(32); // aspect_ratio_idc, Extended_SAR
        if (r.Flag()) r.Skip(1); // overscan
        if (!r.Flag()) return null; // video_signal_type_present_flag
        r.Skip(4); // video_format, video_full_range_flag
        if (!r.Flag()) return null; // colour_description_present_flag
        r.Skip(8); // colour_primaries
        return r.Bits(8);
    }

    /// <summary>MSB-first bit reader over an RBSP; reading past the end throws <see cref="EndOfStreamException"/>.</summary>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private long _bit;

        public bool Flag() => Bits(1) != 0;

        public void Skip(int bits)
        {
            _bit += bits;
            if (_bit > (long)_data.Length * 8) throw new EndOfStreamException();
        }

        public ulong Bits(int count)
        {
            ulong value = 0;
            for (var i = 0; i < count; i++)
            {
                var index = (int)(_bit >> 3);
                if (index >= _data.Length) throw new EndOfStreamException();
                value = (value << 1) | (uint)((_data[index] >> (7 - (int)(_bit & 7))) & 1);
                _bit++;
            }

            return value;
        }

        /// <summary>Unsigned Exp-Golomb.</summary>
        public ulong Ue()
        {
            var zeros = 0;
            while (!Flag())
            {
                if (++zeros > 31) throw new EndOfStreamException();
            }

            return (1UL << zeros) - 1 + Bits(zeros);
        }

        /// <summary>Signed Exp-Golomb.</summary>
        public long Se()
        {
            var k = Ue();
            return (k & 1) != 0 ? (long)((k + 1) / 2) : -(long)(k / 2);
        }
    }
}
