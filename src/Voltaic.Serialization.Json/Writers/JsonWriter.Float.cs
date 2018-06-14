﻿using System.Buffers.Text;

namespace Voltaic.Serialization.Json
{
    public static partial class JsonWriter
    {
        public static bool TryWrite(ref MemoryBufferWriter<byte> writer, float value)
        {
            var data = writer.GetSpan(13); // -3.402823E+38
            if (!Utf8Formatter.TryFormat(value, data, out int bytesWritten))
            {
                DebugLog.WriteFailure("Utf8Formatter failed");
                return false;
            }
            writer.Write(data.Slice(0, bytesWritten));
            return true;
        }

        public static bool TryWrite(ref MemoryBufferWriter<byte> writer, double value)
        {
            var data = writer.GetSpan(22); // -1.79769313486232E+308
            if (!Utf8Formatter.TryFormat(value, data, out int bytesWritten))
            {
                DebugLog.WriteFailure("Utf8Formatter failed");
                return false;
            }
            writer.Write(data.Slice(0, bytesWritten));
            return true;
        }

        public static bool TryWrite(ref MemoryBufferWriter<byte> writer, decimal value)
        {
            var data = writer.GetSpan(64); // ???
            if (!Utf8Formatter.TryFormat(value, data, out int bytesWritten))
            {
                DebugLog.WriteFailure("Utf8Formatter failed");
                return false;
            }
            writer.Write(data.Slice(0, bytesWritten));
            return true;
        }
    }
}
