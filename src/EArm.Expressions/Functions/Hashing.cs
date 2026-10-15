using System;
using System.Security.Cryptography;
using System.Text;

namespace EArm.Expressions.Functions;

/// <summary>
/// Deterministic hashes behind <c>guid()</c> and <c>uniqueString()</c>.
/// <para>
/// ARM does not document either algorithm. <see cref="UniqueString"/> follows the widely used community port
/// (MurmurHash64A, seed 0, over the UTF-8 text; 13 base-32 characters, alphabet <c>a-z2-7</c>, 5 bits per character
/// from the low end of the 64-bit hash). <see cref="GuidV5"/> is an RFC 4122 version-5 UUID (SHA-1) in a fixed
/// EARM namespace. Neither is guaranteed to reproduce the value Azure Resource Manager would compute for the
/// same input; they are stable across EARM runs, which is what state keys and <c>guid()</c>-named resources need.
/// </para>
/// </summary>
public static class Hashing
{
    /// <summary>The namespace of EARM's <c>guid()</c> (RFC 4122 v5).</summary>
    public static readonly Guid GuidNamespace = new("3f8a2c6e-7b1d-4e5a-9c0f-6d2b8e4a1c73");

    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>13-character base-32 hash of <paramref name="text"/>.</summary>
    public static string UniqueString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ulong hash = MurmurHash64A(Encoding.UTF8.GetBytes(text), 0);
        var sb = new StringBuilder(13);
        for (int i = 0; i < 13; i++)
        {
            sb.Append(Base32Alphabet[(int)(hash & 31)]);
            hash >>= 5;
        }

        return sb.ToString();
    }

    /// <summary>RFC 4122 version-5 UUID of <paramref name="name"/> under <paramref name="namespaceId"/>.</summary>
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

    private static void SwapGuidByteOrder(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }

    /// <summary>MurmurHash64A (Austin Appleby), 64-bit variant for 64-bit platforms.</summary>
    public static ulong MurmurHash64A(ReadOnlySpan<byte> data, ulong seed)
    {
        const ulong m = 0xc6a4a7935bd1e995UL;
        const int r = 47;

        ulong h = seed ^ ((ulong)data.Length * m);
        int blocks = data.Length / 8;
        for (int i = 0; i < blocks; i++)
        {
            ulong k = BitConverter.ToUInt64(data.Slice(i * 8, 8));
            k *= m;
            k ^= k >> r;
            k *= m;
            h ^= k;
            h *= m;
        }

        int remaining = data.Length & 7;
        if (remaining > 0)
        {
            var tail = data[(blocks * 8)..];
            switch (remaining)
            {
                case 7:
                    h ^= (ulong)tail[6] << 48;
                    goto case 6;
                case 6:
                    h ^= (ulong)tail[5] << 40;
                    goto case 5;
                case 5:
                    h ^= (ulong)tail[4] << 32;
                    goto case 4;
                case 4:
                    h ^= (ulong)tail[3] << 24;
                    goto case 3;
                case 3:
                    h ^= (ulong)tail[2] << 16;
                    goto case 2;
                case 2:
                    h ^= (ulong)tail[1] << 8;
                    goto case 1;
                case 1:
                    h ^= tail[0];
                    h *= m;
                    break;
            }
        }

        h ^= h >> r;
        h *= m;
        h ^= h >> r;
        return h;
    }
}
