// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;

#if KESTREL
namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3
#else
namespace System.Net.Http
#endif
{
    /// <summary>
    /// Variable length integer encoding and decoding methods. Based on https://tools.ietf.org/html/draft-ietf-quic-transport-24#section-16.
    /// Either will take up 1, 2, 4, or 8 bytes.
    /// </summary>
    internal static class VariableLengthIntegerHelper
    {
        private const byte LengthMask = 0xC0;
        private const byte LengthOneByte = 0x00;
        private const byte LengthTwoByte = 0x40;
        private const byte LengthFourByte = 0x80;
        private const byte LengthEightByte = 0xC0;

        private const uint TwoByteSubtract = 0x4000;
        private const uint FourByteSubtract = 0x80000000;
        private const ulong EightByteSubtract = 0xC000000000000000;

        private const uint OneByteLimit = 64;
        private const uint TwoByteLimit = 16383;
        private const uint FourByteLimit = 1073741823;

        public static bool TryRead(ReadOnlySpan<byte> buffer, out long value, out int bytesRead)
        {
            if (buffer.Length == 0)
            {
                goto needMore;
            }

            // The first two bits of the first byte represent the length of the
            // variable length integer
            // 00 = length 1
            // 01 = length 2
            // 10 = length 4
            // 11 = length 8

            byte firstByte = buffer[0];

            switch (firstByte & LengthMask)
            {
                case LengthOneByte:
                    value = firstByte;
                    bytesRead = 1;
                    return true;
                case LengthTwoByte:
                    if (buffer.Length < 2)
                    {
                        goto needMore;
                    }
                    value = BinaryPrimitives.ReadUInt16BigEndian(buffer) - TwoByteSubtract;
                    bytesRead = 2;
                    return true;
                case LengthFourByte:
                    if (buffer.Length < 4)
                    {
                        goto needMore;
                    }
                    value = BinaryPrimitives.ReadUInt32BigEndian(buffer) - FourByteSubtract;
                    bytesRead = 4;
                    return true;
                default:
                    Debug.Assert((firstByte & LengthMask) == LengthEightByte);

                    if (buffer.Length < 8)
                    {
                        goto needMore;
                    }
                    value = (long)(BinaryPrimitives.ReadUInt64BigEndian(buffer) - EightByteSubtract);
                    bytesRead = 4;
                    return true;
            }

        needMore:
            value = 0;
            bytesRead = 0;
            return false;
        }

        public static bool TryRead(ref SequenceReader<byte> reader, out long value)
        {
            ReadOnlySpan<byte> span = reader.UnreadSpan;

            if (span.Length >= 8)
            {
                // Hot path: call span-based read immediately.
                if (TryRead(span, out value, out int bytesRead))
                {
                    reader.Advance(bytesRead);
                    return true;
                }
                else
                {
                    return false;
                }
            }

            // Cold path: copy to a temporary buffer before calling span-based read.
            return TryReadSlow(ref reader, out value);

            static bool TryReadSlow(ref SequenceReader<byte> reader, out long value)
            {
                ReadOnlySpan<byte> span = reader.CurrentSpan;

                if (!reader.TryPeek(out byte firstByte))
                {
                    value = 0;
                    return false;
                }

                int length =
                    (firstByte & LengthMask) switch
                    {
                        LengthOneByte => 1,
                        LengthTwoByte => 2,
                        LengthFourByte => 4,
                        _ => 8
                    };

                Span<byte> temp = stackalloc byte[length];
                if (reader.TryCopyTo(temp))
                {
                    bool result = TryRead(temp, out value, out int bytesRead);
                    Debug.Assert(result == true);
                    Debug.Assert(bytesRead == length);

                    reader.Advance(bytesRead);
                    return true;
                }

                value = 0;
                return false;
            }
        }

        public static long GetInteger(in ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            var reader = new SequenceReader<byte>(buffer);
            if (TryRead(ref reader, out long value))
            {
                consumed = examined = buffer.GetPosition(reader.Consumed);
                return (long)value;
            }
            else
            {
                consumed = default;
                examined = buffer.End;
                return -1;
            }
        }

        public static bool TryWrite(Span<byte> buffer, long longToEncode, out int bytesWritten)
        {
            if (longToEncode < OneByteLimit)
            {
                if (!buffer.IsEmpty)
                {
                    buffer[0] = (byte)longToEncode;
                    bytesWritten = 1;
                    return true;
                }
            }
            else if (longToEncode < TwoByteLimit)
            {
                if (BinaryPrimitives.TryWriteUInt16BigEndian(buffer, (ushort)((uint)longToEncode | 0x4000u)))
                {
                    bytesWritten = 2;
                    return true;
                }
            }
            else if (longToEncode < FourByteLimit)
            {
                if (BinaryPrimitives.TryWriteUInt32BigEndian(buffer, (uint)longToEncode | 0x80000000))
                {
                    bytesWritten = 4;
                    return true;
                }
            }
            else
            {
                if (BinaryPrimitives.TryWriteUInt64BigEndian(buffer, (ulong)longToEncode | 0xC000000000000000))
                {
                    bytesWritten = 8;
                    return true;
                }
            }

            bytesWritten = 0;
            return false;
        }

        public static int WriteInteger(Span<byte> buffer, long longToEncode)
        {
            bool res = TryWrite(buffer, longToEncode, out int bytesWritten);
            Debug.Assert(res == true);
            return bytesWritten;
        }

        public static int GetByteCount(long value)
        {
            Debug.Assert(value >= 0);
            Debug.Assert(value < long.MaxValue / 2);

            return
                value < OneByteLimit ? 1 :
                value < TwoByteLimit ? 2 :
                value < FourByteLimit ? 4 :
                8;
        }
    }
}
