﻿using System;
using Voltaic.Serialization.Utf8;

namespace Voltaic.Serialization.Json
{
    public static partial class JsonReader
    {
        public static bool TryReadBoolean(ref ReadOnlySpan<byte> remaining, out bool result)
        {
            result = default;

            if (remaining.Length == 0)
                return false;

            switch (remaining[0])
            {
                case (byte)'t': // True
                case (byte)'T':
                    if (remaining.Length < 4)
                        return false;
                    result = true;
                    remaining = remaining.Slice(4);
                    return true;
                case (byte)'f': // False
                case (byte)'F':
                    if (remaining.Length < 5)
                        return false;
                    result = false;
                    remaining = remaining.Slice(5);
                    return true;
                case (byte)'"': // String
                    remaining = remaining.Slice(1);
                    if (!Utf8Reader.TryReadBoolean(ref remaining, out result))
                        return false;
                    if (remaining.Length == 0 || remaining[0] != '"')
                        return false;
                    remaining = remaining.Slice(1);
                    return true;
            }
            return false;
        }

    }
}
