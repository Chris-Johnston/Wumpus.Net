﻿using System;
using System.Buffers;
using Voltaic.Serialization.Utf8;

namespace Voltaic.Serialization.Json
{
    public static partial class JsonWriter
    {
        public static bool TryWrite(ref ResizableMemory<byte> writer, Guid value, StandardFormat standardFormat)
        {
            writer.Append((byte)'"');
            if (!Utf8Writer.TryWrite(ref writer, value, standardFormat))
                return false;
            writer.Append((byte)'"');
            return true;
        }
    }
}
