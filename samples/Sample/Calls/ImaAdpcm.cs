using System.Buffers.Binary;

namespace Sample.Calls;

/// <summary>
/// IMA ADPCM: 4 bits a sample, a quarter of PCM16. Each block starts with the encoder state it was coded from
/// <c>[predictor i16][step index u8][reserved u8]</c>, so a block decodes on its own and a lost packet costs only itself.
/// Nibbles are packed low first.
/// </summary>
internal sealed class ImaAdpcm
{
    public const int HeaderSize = 4;

    static readonly int[] IndexTable = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];

    static readonly int[] StepTable =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107,
        118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
        1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894,
        6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794,
        32767
    ];

    int predictor;
    int index;


    public static int EncodedSize(int pcmBytes) => HeaderSize + (pcmBytes / 2 + 1) / 2;
    public static int DecodedSize(int blockBytes) => Math.Max(0, blockBytes - HeaderSize) * 4;


    /// <summary>
    /// Encodes PCM16 mono little-endian. The encoder carries its state into the next block.
    /// </summary>
    public byte[] Encode(ReadOnlySpan<byte> pcm16)
    {
        var samples = pcm16.Length / 2;
        var block = new byte[EncodedSize(pcm16.Length)];
        BinaryPrimitives.WriteInt16LittleEndian(block, (short)this.predictor);
        block[2] = (byte)this.index;

        for (var i = 0; i < samples; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 2, 2));
            var nibble = this.EncodeSample(sample);
            if ((i & 1) == 0)
                block[HeaderSize + i / 2] = (byte)nibble;
            else
                block[HeaderSize + i / 2] |= (byte)(nibble << 4);
        }
        return block;
    }


    /// <summary>
    /// Decodes one block to PCM16 mono little-endian. Stateless: the block carries its own starting state.
    /// </summary>
    public static byte[] Decode(ReadOnlySpan<byte> block)
    {
        if (block.Length < HeaderSize)
            throw new FormatException("ADPCM block shorter than its header");

        int predictor = BinaryPrimitives.ReadInt16LittleEndian(block);
        var index = Math.Clamp((int)block[2], 0, StepTable.Length - 1);
        var data = block[HeaderSize..];
        var pcm = new byte[data.Length * 4];

        for (var i = 0; i < data.Length * 2; i++)
        {
            var nibble = (i & 1) == 0 ? data[i / 2] & 0x0F : data[i / 2] >> 4;
            predictor = DecodeSample(nibble, predictor, ref index);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), (short)predictor);
        }
        return pcm;
    }


    int EncodeSample(short sample)
    {
        var step = StepTable[this.index];
        var diff = sample - this.predictor;
        var nibble = 0;
        if (diff < 0)
        {
            nibble = 8;
            diff = -diff;
        }

        var mask = 4;
        var tempStep = step;
        for (var i = 0; i < 3; i++)
        {
            if (diff >= tempStep)
            {
                nibble |= mask;
                diff -= tempStep;
            }
            tempStep >>= 1;
            mask >>= 1;
        }

        // track exactly what the decoder will reconstruct
        this.predictor = DecodeSample(nibble, this.predictor, ref this.index);
        return nibble;
    }


    static int DecodeSample(int nibble, int predictor, ref int index)
    {
        var step = StepTable[index];
        var diff = step >> 3;
        if ((nibble & 4) != 0) diff += step;
        if ((nibble & 2) != 0) diff += step >> 1;
        if ((nibble & 1) != 0) diff += step >> 2;

        predictor = (nibble & 8) != 0 ? predictor - diff : predictor + diff;
        predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);
        index = Math.Clamp(index + IndexTable[nibble], 0, StepTable.Length - 1);
        return predictor;
    }
}
