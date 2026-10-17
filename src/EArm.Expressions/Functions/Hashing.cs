using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace EArm.Expressions.Functions;

/// <summary>
/// Deterministic hashes behind <c>guid()</c> and <c>uniqueString()</c>, reproducing the values Azure Resource Manager computes.
/// <para>
/// <see cref="GuidV5"/> is an RFC 4122 version-5 UUID (SHA-1) over the UTF-8 text; <c>guid()</c> uses ARM's namespace
/// <see cref="GuidNamespace"/> (documented on Microsoft Learn). <see cref="UniqueString"/> is ARM's 64-bit Murmur variant
/// (<see cref="MurmurHash64"/>, seed 0, over the UTF-8 text) written as 13 base-32 characters (alphabet <c>a-z2-7</c>, 5 bits per character
/// from the high end of the hash). Both are checked against values produced by Azure Resource Manager (see the unit tests).
/// </para>
/// </summary>
public static class Hashing
{
    /// <summary>The namespace of ARM's <c>guid()</c> (RFC 4122 v5).</summary>
    public static readonly Guid GuidNamespace = new("11fb06fb-712d-4ddd-98c7-e71bbd588830");

    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>13-character base-32 hash of <paramref name="text"/>, as ARM's <c>uniqueString()</c> computes it.</summary>
    public static string UniqueString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ulong hash = MurmurHash64(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder(13);
        for (int i = 0; i < 13; i++)
        {
            sb.Append(Base32Alphabet[(int)(hash >> 59)]);
            hash <<= 5;
        }

        return sb.ToString();
    }

    /// <summary>RFC 4122 version-5 UUID of <paramref name="name"/> (UTF-8) under <paramref name="namespaceId"/>.</summary>
    public static Guid GuidV5(Guid namespaceId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // RFC 4122 wants the namespace in network byte order; Guid.ToByteArray is little-endian for the first three fields.
        byte[] namespaceBytes = namespaceId.ToByteArray();
        SwapGuidByteOrder(namespaceBytes);
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);

        byte[] input = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, input, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, input, namespaceBytes.Length, nameBytes.Length);
        byte[] hash = SHA1.HashData(input);

        byte[] result = new byte[16];
        Array.Copy(hash, 0, result, 0, 16);
        result[6] = (byte)((result[6] & 0x0F) | 0x50);
        result[8] = (byte)((result[8] & 0x3F) | 0x80);
        SwapGuidByteOrder(result);
        return new Guid(result);
    }

    /// <summary>
    /// The 64-bit Murmur variant of ARM's <c>uniqueString()</c>: two 32-bit MurmurHash3-style lanes over 8-byte blocks (little-endian), combined
    /// and finalised with the MurmurHash3 mixer; the second lane is the high half of the result.
    /// </summary>
    public static ulong MurmurHash64(ReadOnlySpan<byte> data, uint seed = 0)
    {
        const uint c1 = 597399067;
        const uint c2 = 2869860233;
        int length = data.Length;
        uint h1 = seed;
        uint h2 = seed;
        int index = 0;
        for (; index + 7 < length; index += 8)
        {
            uint k1 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(index, 4));
            uint k2 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(index + 4, 4));

            k1 *= c1;
            k1 = BitOperations.RotateLeft(k1, 15);
            k1 *= c2;
            h1 ^= k1;
            h1 = BitOperations.RotateLeft(h1, 19);
            h1 += h2;
            h1 = (h1 * 5) + 1444728091;

            k2 *= c2;
            k2 = BitOperations.RotateLeft(k2, 17);
            k2 *= c1;
            h2 ^= k2;
            h2 = BitOperations.RotateLeft(h2, 13);
            h2 += h1;
            h2 = (h2 * 5) + 197830471;
        }

        int tail = length - index;
        if (tail > 0)
        {
            uint k1 = ReadPartial(data.Slice(index, Math.Min(tail, 4)));
            k1 *= c1;
            k1 = BitOperations.RotateLeft(k1, 15);
            k1 *= c2;
            h1 ^= k1;

            if (tail > 4)
            {
                uint k2 = ReadPartial(data.Slice(index + 4, tail - 4));
                k2 *= c2;
                k2 = BitOperations.RotateLeft(k2, 17);
                k2 *= c1;
                h2 ^= k2;
            }
        }

        h1 ^= (uint)length;
        h2 ^= (uint)length;
        h1 += h2;
        h2 += h1;
        h1 = Mix(h1);
        h2 = Mix(h2);
        h1 += h2;
        h2 += h1;
        return ((ulong)h2 << 32) | h1;
    }

    private static void SwapGuidByteOrder(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }

    /// <summary>Up to four bytes as a little-endian integer.</summary>
    private static uint ReadPartial(ReadOnlySpan<byte> bytes)
    {
        uint value = 0;
        for (int i = bytes.Length - 1; i >= 0; i--)
        {
            value = (value << 8) | bytes[i];
        }

        return value;
    }

    /// <summary>The MurmurHash3 32-bit finaliser.</summary>
    private static uint Mix(uint h)
    {
        h ^= h >> 16;
        h *= 2246822507;
        h ^= h >> 13;
        h *= 3266489909;
        h ^= h >> 16;
        return h;
    }
}
