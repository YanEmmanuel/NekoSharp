using System.Text;

namespace NekoSharp.Core.Providers.Comix;

internal static class ComixHash
{
    private static readonly string[] Keys =
    [
        "13YDu67uDgFczo3DnuTIURqas4lfMEPADY6Jaeqky+w=",
        "yEy7wBfBc+gsYPiQL/4Dfd0pIBZFzMwrtlRQGwMXy3Q=",
        "yrP+EVA1Dw==",
        "vZ23RT7pbSlxwiygkHd1dhToIku8SNHPC6V36L4cnwM=",
        "QX0sLahOByWLcWGnv6l98vQudWqdRI3DOXBdit9bxCE=",
        "WJwgqCmf",
        "BkWI8feqSlDZKMq6awfzWlUypl88nz65KVRmpH0RWIc=",
        "v7EIpiQQjd2BGuJzMbBA0qPWDSS+wTJRQ7uGzZ6rJKs=",
        "1SUReYlCRA==",
        "RougjiFHkSKs20DZ6BWXiWwQUGZXtseZIyQWKz5eG34=",
        "LL97cwoDoG5cw8QmhI+KSWzfW+8VehIh+inTxnVJ2ps=",
        "52iDqjzlqe8=",
        "U9LRYFL2zXU4TtALIYDj+lCATRk/EJtH7/y7qYYNlh8=",
        "e/GtffFDTvnw7LBRixAD+iGixjqTq9kIZ1m0Hj+s6fY=",
        "xb2XwHNB"
    ];

    public static string GenerateHash(string path, int bodySize = 0, long time = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var baseString = $"{path}:{bodySize}:{time}";
        var encoded = Uri.EscapeDataString(baseString);
        var initialBytes = ToIntArray(Encoding.ASCII.GetBytes(encoded));

        var rounds = Round5(Round4(Round3(Round2(Round1(initialBytes)))));
        var finalBytes = new byte[rounds.Length];
        for (var index = 0; index < rounds.Length; index++)
            finalBytes[index] = (byte)rounds[index];

        return Convert.ToBase64String(finalBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static int[] Round1(int[] data)
    {
        var encrypted = Rc4(GetKeyBytes(0), data);
        var mutKey = GetKeyBytes(1);
        var prefKey = GetKeyBytes(2);
        var output = new List<int>(encrypted.Length * 2);

        for (var index = 0; index < encrypted.Length; index++)
        {
            if (index < 7 && index < prefKey.Length)
                output.Add(prefKey[index]);

            var value = encrypted[index] ^ GetMutKey(mutKey, index);
            value = (index % 10) switch
            {
                0 or 9 => MutC(value),
                1 => MutB(value),
                2 => MutY(value),
                3 => MutDollar(value),
                4 or 6 => MutH(value),
                5 => MutS(value),
                7 => MutK(value),
                8 => MutL(value),
                _ => value
            };

            output.Add(value & 255);
        }

        return [.. output];
    }

    private static int[] Round2(int[] data)
    {
        var encrypted = Rc4(GetKeyBytes(3), data);
        var mutKey = GetKeyBytes(4);
        var prefKey = GetKeyBytes(5);
        var output = new List<int>(encrypted.Length * 2);

        for (var index = 0; index < encrypted.Length; index++)
        {
            if (index < 6 && index < prefKey.Length)
                output.Add(prefKey[index]);

            var value = encrypted[index] ^ GetMutKey(mutKey, index);
            value = (index % 10) switch
            {
                0 or 8 => MutC(value),
                1 => MutB(value),
                2 or 6 => MutDollar(value),
                3 => MutH(value),
                4 or 9 => MutS(value),
                5 => MutK(value),
                7 => MutUnderscore(value),
                _ => value
            };

            output.Add(value & 255);
        }

        return [.. output];
    }

    private static int[] Round3(int[] data)
    {
        var encrypted = Rc4(GetKeyBytes(6), data);
        var mutKey = GetKeyBytes(7);
        var prefKey = GetKeyBytes(8);
        var output = new List<int>(encrypted.Length * 2);

        for (var index = 0; index < encrypted.Length; index++)
        {
            if (index < 7 && index < prefKey.Length)
                output.Add(prefKey[index]);

            var value = encrypted[index] ^ GetMutKey(mutKey, index);
            value = (index % 10) switch
            {
                0 => MutC(value),
                1 => MutF(value),
                2 or 8 => MutS(value),
                3 => MutG(value),
                4 => MutY(value),
                5 => MutM(value),
                6 => MutDollar(value),
                7 => MutK(value),
                9 => MutB(value),
                _ => value
            };

            output.Add(value & 255);
        }

        return [.. output];
    }

    private static int[] Round4(int[] data)
    {
        var encrypted = Rc4(GetKeyBytes(9), data);
        var mutKey = GetKeyBytes(10);
        var prefKey = GetKeyBytes(11);
        var output = new List<int>(encrypted.Length * 2);

        for (var index = 0; index < encrypted.Length; index++)
        {
            if (index < 8 && index < prefKey.Length)
                output.Add(prefKey[index]);

            var value = encrypted[index] ^ GetMutKey(mutKey, index);
            value = (index % 10) switch
            {
                0 => MutB(value),
                1 or 9 => MutM(value),
                2 or 7 => MutL(value),
                3 or 5 => MutS(value),
                4 or 6 => MutUnderscore(value),
                8 => MutY(value),
                _ => value
            };

            output.Add(value & 255);
        }

        return [.. output];
    }

    private static int[] Round5(int[] data)
    {
        var encrypted = Rc4(GetKeyBytes(12), data);
        var mutKey = GetKeyBytes(13);
        var prefKey = GetKeyBytes(14);
        var output = new List<int>(encrypted.Length * 2);

        for (var index = 0; index < encrypted.Length; index++)
        {
            if (index < 6 && index < prefKey.Length)
                output.Add(prefKey[index]);

            var value = encrypted[index] ^ GetMutKey(mutKey, index);
            value = (index % 10) switch
            {
                0 => MutUnderscore(value),
                1 or 7 => MutS(value),
                2 => MutC(value),
                3 or 5 => MutM(value),
                4 => MutB(value),
                6 => MutF(value),
                8 => MutDollar(value),
                9 => MutG(value),
                _ => value
            };

            output.Add(value & 255);
        }

        return [.. output];
    }

    private static int[] Rc4(int[] key, int[] data)
    {
        if (key.Length == 0)
            return [.. data];

        var state = new int[256];
        for (var index = 0; index < state.Length; index++)
            state[index] = index;

        var j = 0;
        for (var index = 0; index < state.Length; index++)
        {
            j = (j + state[index] + key[index % key.Length]) % 256;
            (state[index], state[j]) = (state[j], state[index]);
        }

        var output = new int[data.Length];
        var i = 0;
        j = 0;

        for (var index = 0; index < data.Length; index++)
        {
            i = (i + 1) % 256;
            j = (j + state[i]) % 256;
            (state[i], state[j]) = (state[j], state[i]);
            output[index] = data[index] ^ state[(state[i] + state[j]) % 256];
        }

        return output;
    }

    private static int[] GetKeyBytes(int index)
    {
        if (index < 0 || index >= Keys.Length)
            return [];

        try
        {
            var key = Keys[index];
            var padding = (4 - key.Length % 4) % 4;
            if (padding > 0)
                key = key.PadRight(key.Length + padding, '=');

            return ToIntArray(Convert.FromBase64String(key));
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static int[] ToIntArray(byte[] bytes)
    {
        var values = new int[bytes.Length];
        for (var index = 0; index < bytes.Length; index++)
            values[index] = bytes[index] & 0xFF;

        return values;
    }

    private static int GetMutKey(int[] mutKey, int index)
        => mutKey.Length != 0 && index % 32 < mutKey.Length ? mutKey[index % 32] : 0;

    private static int MutS(int value) => (value + 143) % 256;
    private static int MutL(int value) => ((value >> 1) | (value << 7)) & 255;
    private static int MutC(int value) => (value + 115) % 256;
    private static int MutM(int value) => value ^ 177;
    private static int MutF(int value) => (value - 188 + 256) % 256;
    private static int MutG(int value) => ((value << 2) | (value >> 6)) & 255;
    private static int MutH(int value) => (value - 42 + 256) % 256;
    private static int MutDollar(int value) => ((value << 4) | (value >> 4)) & 255;
    private static int MutB(int value) => (value - 12 + 256) % 256;
    private static int MutUnderscore(int value) => (value - 20 + 256) % 256;
    private static int MutY(int value) => ((value >> 1) | (value << 7)) & 255;
    private static int MutK(int value) => (value - 241 + 256) % 256;
}