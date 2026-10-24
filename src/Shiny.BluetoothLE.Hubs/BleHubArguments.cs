using System.Buffers.Binary;
using System.ComponentModel;

namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// Writes hub method/push arguments: <c>[count:1]</c> then <c>count x ([length:4][value])</c>.
/// Each value is serialized with its static type so the serializer stays AOT friendly. Used by generated code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class BleHubArgumentWriter(IBleHubSerializer serializer)
{
    readonly List<byte[]> values = new();

    public BleHubArgumentWriter Write<T>(T value)
    {
        if (this.values.Count == byte.MaxValue)
            throw new BleHubException($"A hub call can have at most {byte.MaxValue} arguments");

        this.values.Add(serializer.Serialize(value));
        return this;
    }


    public byte[] ToArray()
    {
        var size = 1 + this.values.Sum(x => 4 + x.Length);
        var buffer = new byte[size];
        buffer[0] = (byte)this.values.Count;
        var offset = 1;
        foreach (var value in this.values)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), (uint)value.Length);
            offset += 4;
            value.CopyTo(buffer, offset);
            offset += value.Length;
        }
        return buffer;
    }


    public static byte[] Empty { get; } = [0];
}


/// <summary>
/// Reads arguments written by <see cref="BleHubArgumentWriter"/>. Used by generated code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class BleHubArgumentReader
{
    readonly IBleHubSerializer serializer;
    readonly ReadOnlyMemory<byte> data;
    int offset;
    int read;


    public BleHubArgumentReader(IBleHubSerializer serializer, ReadOnlyMemory<byte> data)
    {
        this.serializer = serializer;
        this.data = data;
        if (data.Length == 0)
        {
            this.Count = 0;
        }
        else
        {
            this.Count = data.Span[0];
            this.offset = 1;
        }
    }


    public int Count { get; }


    /// <summary>
    /// Throws when the caller sent a different number of arguments than the method declares (mismatched contract versions)
    /// </summary>
    public BleHubArgumentReader Expect(int count, string member)
    {
        if (this.Count != count)
            throw new BleHubProtocolException($"'{member}' expects {count} argument(s) but {this.Count} were sent - are both sides using the same contract?");

        return this;
    }


    public T Read<T>()
    {
        if (this.read >= this.Count)
            throw new BleHubProtocolException("Read past the last argument");

        var span = this.data.Span;
        if (this.offset + 4 > span.Length)
            throw new BleHubProtocolException("Argument header is truncated");

        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[this.offset..]);
        this.offset += 4;
        if (length < 0 || this.offset + length > span.Length)
            throw new BleHubProtocolException("Argument value is truncated");

        var value = this.serializer.Deserialize<T>(span.Slice(this.offset, length));
        this.offset += length;
        this.read++;
        return value;
    }
}
