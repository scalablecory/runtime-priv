// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.HPack;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3.QPack
{
    internal class QPackEncoder
    {
        private IEnumerator<KeyValuePair<string, string>> _enumerator;

        // TODO these all need to be updated!

        /*
         *      0   1   2   3   4   5   6   7
               +---+---+---+---+---+---+---+---+
               | 1 | S |      Index (6+)       |
               +---+---+-----------------------+
         */
        public static bool EncodeIndexedHeaderField(int index, Span<byte> destination, out int bytesWritten)
        {
            return IntegerEncoder.Encode(index, 6, destination, out bytesWritten);
        }

        public static bool EncodeStaticIndexedHeaderField(int index, Span<byte> destination, out int bytesWritten)
        {
            if (destination.IsEmpty)
            {
                bytesWritten = 0;
                return false;
            }

            destination[0] = 0b11000000;
            return IntegerEncoder.Encode(index, 6, destination, out bytesWritten);
        }

        public static byte[] EncodeStaticIndexedHeaderFieldToNewArray(int index)
        {
            Span<byte> buffer = stackalloc byte[16];

            bool res = EncodeStaticIndexedHeaderField(index, buffer, out int bytesWritten);
            Debug.Assert(res == true);

            return buffer.ToArray();
        }

        public static bool EncodeIndexHeaderFieldWithPostBaseIndex(int index, Span<byte> destination, out int bytesWritten)
        {
            bytesWritten = 0;
            return false;
        }

        public static bool EncodeLiteralHeaderFieldWithStaticNameReference(int index, string value, Span<byte> destination, out int bytesWritten)
        {
            if (destination.Length < 2)
            {
                // Requires at least two bytes (one for name reference header, one for value length)
                bytesWritten = 0;
                return false;
            }

            destination[0] = 0b01110000;
            if (!IntegerEncoder.Encode(index, 4, destination, out int headerBytesWritten))
            {
                bytesWritten = 0;
                return false;
            }

            destination = destination.Slice(headerBytesWritten);

            if (!EncodeValueString(value, destination, out int valueBytesWritten))
            {
                bytesWritten = 0;
                return false;
            }

            bytesWritten = headerBytesWritten + valueBytesWritten;
            return true;
        }

        public static byte[] EncodeLiteralHeaderFieldWithStaticNameReferenceToArray(int index, string value)
        {
            //TODO: find actual values for these lengths and make them constants.
            Span<byte> temp = value.Length < 256 ? stackalloc byte[256 + 8] : new byte[value.Length + 16];
            bool res = EncodeLiteralHeaderFieldWithStaticNameReference(index, value, temp, out int bytesWritten);
            Debug.Assert(res == true);
            return temp.Slice(bytesWritten).ToArray();
        }

        /*
         *         0   1   2   3   4   5   6   7
                  +---+---+---+---+---+---+---+---+
                  | 0 | 1 | N | S |Name Index (4+)|
                  +---+---+---+---+---------------+
                  | H |     Value Length (7+)     |
                  +---+---------------------------+
                  |  Value String (Length bytes)  |
                  +-------------------------------+
         */
        public static bool EncodeLiteralHeaderFieldWithPostBaseNameReference(int index, Span<byte> destination, out int bytesWritten)
        {
            bytesWritten = 0;
            return false;
        }

        public static bool EncodeLiteralHeaderFieldWithoutNameReference(string name, string value, Span<byte> destination, out int bytesWritten)
        {
            if (!EncodeNameString(name, destination, out int nameLength))
            {
                bytesWritten = 0;
                return false;
            }

            if (!EncodeValueString(value, destination.Slice(nameLength), out int valueLength))
            {
                bytesWritten = 0;
                return false;
            }

            bytesWritten = nameLength + valueLength;
            return true;
        }

        public static bool EncodeLiteralHeaderFieldWithoutNameReference(string name, ReadOnlySpan<string> values, string valueSeparator, Span<byte> destination, out int bytesWritten)
        {
            if (!EncodeNameString(name, destination, out int nameLength))
            {
                bytesWritten = 0;
                return false;
            }

            if (!EncodeValueStrings(values, valueSeparator, destination.Slice(nameLength), out int valueLength))
            {
                bytesWritten = 0;
                return false;
            }

            bytesWritten = nameLength + valueLength;
            return true;
        }

        /*
         *     0   1   2   3   4   5   6   7
               +---+---+---+---+---+---+---+---+
               |   Required Insert Count (8+)  |
               +---+---------------------------+
               | S |      Delta Base (7+)      |
               +---+---------------------------+
               |      Compressed Headers     ...
               +-------------------------------+
         *
         */
        private static bool EncodeHeaderBlockPrefix(Span<byte> destination, out int bytesWritten)
        {
            int length;
            bytesWritten = 0;
            // Required insert count as first int
            if (!IntegerEncoder.Encode(0, 8, destination, out length))
            {
                return false;
            }

            bytesWritten += length;
            destination = destination.Slice(length);

            // Delta base
            if (destination.IsEmpty)
            {
                return false;
            }

            destination[0] = 0x00;
            if (!IntegerEncoder.Encode(0, 7, destination, out length))
            {
                return false;
            }

            bytesWritten += length;

            return true;
        }

        private static bool EncodeLiteralHeaderName(string value, Span<byte> destination, out int bytesWritten)
        {
            // From https://tools.ietf.org/html/rfc7541#section-5.2
            // ------------------------------------------------------
            //   0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | H |    String Length (7+)     |
            // +---+---------------------------+
            // |  String Data (Length octets)  |
            // +-------------------------------+

            if (!destination.IsEmpty)
            {
                destination[0] = 0; // TODO: Use Huffman encoding
                if (IntegerEncoder.Encode(value.Length, 7, destination, out int integerLength))
                {
                    Debug.Assert(integerLength >= 1);

                    destination = destination.Slice(integerLength);
                    if (value.Length <= destination.Length)
                    {
                        for (int i = 0; i < value.Length; i++)
                        {
                            char c = value[i];
                            destination[i] = (byte)((uint)(c - 'A') <= ('Z' - 'A') ? c | 0x20 : c);
                        }

                        bytesWritten = integerLength + value.Length;
                        return true;
                    }
                }
            }

            bytesWritten = 0;
            return false;
        }

        private static bool EncodeStringLiteralValue(string value, Span<byte> destination, out int bytesWritten)
        {
            if (value.Length <= destination.Length)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    if ((c & 0xFF80) != 0)
                    {
                        throw new HttpRequestException("");
                    }

                    destination[i] = (byte)c;
                }

                bytesWritten = value.Length;
                return true;
            }

            bytesWritten = 0;
            return false;
        }

        public static bool EncodeStringLiteral(string value, Span<byte> destination, out int bytesWritten)
        {
            // From https://tools.ietf.org/html/rfc7541#section-5.2
            // ------------------------------------------------------
            //   0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | H |    String Length (7+)     |
            // +---+---------------------------+
            // |  String Data (Length octets)  |
            // +-------------------------------+

            if (!destination.IsEmpty)
            {
                destination[0] = 0; // TODO: Use Huffman encoding
                if (IntegerEncoder.Encode(value.Length, 7, destination, out int integerLength))
                {
                    Debug.Assert(integerLength >= 1);

                    if (EncodeStringLiteralValue(value, destination.Slice(integerLength), out int valueLength))
                    {
                        bytesWritten = integerLength + valueLength;
                        return true;
                    }
                }
            }

            bytesWritten = 0;
            return false;
        }

        public static bool EncodeStringLiterals(string[] values, string separator, Span<byte> destination, out int bytesWritten)
        {
            bytesWritten = 0;

            if (values.Length == 0)
            {
                return EncodeStringLiteral("", destination, out bytesWritten);
            }
            else if (values.Length == 1)
            {
                return EncodeStringLiteral(values[0], destination, out bytesWritten);
            }

            if (!destination.IsEmpty)
            {
                int valueLength = 0;

                // Calculate length of all parts and separators.
                foreach (string part in values)
                {
                    valueLength = checked((int)(valueLength + part.Length));
                }

                valueLength = checked((int)(valueLength + (values.Length - 1) * separator.Length));

                if (IntegerEncoder.Encode(valueLength, 7, destination, out int integerLength))
                {
                    Debug.Assert(integerLength >= 1);

                    int encodedLength = 0;
                    for (int j = 0; j < values.Length; j++)
                    {
                        if (j != 0 && !EncodeStringLiteralValue(separator, destination.Slice(integerLength), out encodedLength))
                        {
                            return false;
                        }

                        integerLength += encodedLength;

                        if (!EncodeStringLiteralValue(values[j], destination.Slice(integerLength), out encodedLength))
                        {
                            return false;
                        }

                        integerLength += encodedLength;
                    }

                    bytesWritten = integerLength;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Encodes a "Literal Header Field without Indexing" to a new array, but only the index portion;
        /// a subsequent call to <see cref="EncodeStringLiteral"/> must be used to encode the associated value.
        /// </summary>
        public static byte[] EncodeLiteralHeaderFieldWithoutIndexingToAllocatedArray(int index)
        {
            Span<byte> span = stackalloc byte[256];
            bool success = EncodeLiteralHeaderFieldWithPostBaseNameReference(index, span, out int length);
            Debug.Assert(success, $"Stack-allocated space was too small for index '{index}'.");
            return span.Slice(0, length).ToArray();
        }

        /// <summary>
        /// Encodes a "Literal Header Field without Indexing - New Name" to a new array, but only the name portion;
        /// a subsequent call to <see cref="EncodeStringLiteral"/> must be used to encode the associated value.
        /// </summary>
        public static byte[] EncodeLiteralHeaderFieldWithoutIndexingNewNameToAllocatedArray(string name)
        {
            Span<byte> span = stackalloc byte[256];
            bool success = EncodeLiteralHeaderFieldWithoutIndexingNewName(name, span, out int length);
            Debug.Assert(success, $"Stack-allocated space was too small for \"{name}\".");
            return span.Slice(0, length).ToArray();
        }

        private static bool EncodeLiteralHeaderFieldWithoutIndexingNewName(string name, Span<byte> span, out int length)
        {
            throw new NotImplementedException();
        }

        // TODO these are fairly hard coded for the first two bytes to be zero.
        public bool BeginEncode(IEnumerable<KeyValuePair<string, string>> headers, Span<byte> buffer, out int length)
        {
            _enumerator = headers.GetEnumerator();
            _enumerator.MoveNext();
            buffer[0] = 0;
            buffer[1] = 0;

            return Encode(buffer.Slice(2), out length);
        }

        public bool BeginEncode(int statusCode, IEnumerable<KeyValuePair<string, string>> headers, Span<byte> buffer, out int length)
        {
            _enumerator = headers.GetEnumerator();
            _enumerator.MoveNext();

            // https://quicwg.org/base-drafts/draft-ietf-quic-qpack.html#header-prefix
            buffer[0] = 0;
            buffer[1] = 0;

            var statusCodeLength = EncodeStatusCode(statusCode, buffer.Slice(2));
            var done = Encode(buffer.Slice(statusCodeLength + 2), throwIfNoneEncoded: false, out var headersLength);
            length = statusCodeLength + headersLength + 2;

            return done;
        }

        public bool Encode(Span<byte> buffer, out int length)
        {
            return Encode(buffer, throwIfNoneEncoded: true, out length);
        }

        private bool Encode(Span<byte> buffer, bool throwIfNoneEncoded, out int length)
        {
            length = 0;

            do
            {
                if (!EncodeLiteralHeaderFieldWithoutNameReference(_enumerator.Current.Key, _enumerator.Current.Value, buffer.Slice(length), out var headerLength))
                {
                    if (length == 0 && throwIfNoneEncoded)
                    {
                        throw new QPackEncodingException("TODO sync with corefx" /* CoreStrings.HPackErrorNotEnoughBuffer */);
                    }
                    return false;
                }

                length += headerLength;
            } while (_enumerator.MoveNext());

            return true;
        }

        private static bool EncodeValueString(string s, Span<byte> buffer, out int length)
        {
            if (buffer.IsEmpty)
            {
                length = 0;
                return false;
            }

            buffer[0] = 0;
            if (!IntegerEncoder.Encode(s.Length, 7, buffer, out var nameLength))
            {
                length = 0;
                return false;
            }

            buffer = buffer.Slice(nameLength);
            if (buffer.Length < s.Length)
            {
                length = 0;
                return false;
            }

            EncodeValueStringPart(s, buffer);

            length = nameLength + s.Length;
            return true;
        }

        private static bool EncodeValueStrings(ReadOnlySpan<string> values, string separator, Span<byte> buffer, out int length)
        {
            if (buffer.IsEmpty)
            {
                length = 0;
                return false;
            }

            int valueLength = separator.Length * (values.Length - 1);
            for (int i = 0; i < values.Length; ++i)
            {
                valueLength += values[i].Length;
            }

            buffer[0] = 0;
            if (!IntegerEncoder.Encode(valueLength, 7, buffer, out var nameLength))
            {
                length = 0;
                return false;
            }

            buffer = buffer.Slice(nameLength);
            if (buffer.Length < valueLength)
            {
                length = 0;
                return false;
            }

            if (values.Length > 0)
            {
                string value = values[0];
                EncodeValueStringPart(value, buffer);
                buffer = buffer.Slice(value.Length);

                for (int i = 1; i < values.Length; ++i)
                {
                    EncodeValueStringPart(separator, buffer);
                    buffer = buffer.Slice(separator.Length);

                    value = values[i];
                    EncodeValueStringPart(value, buffer);
                    buffer = buffer.Slice(value.Length);
                }
            }

            length = nameLength + valueLength;
            return true;
        }

        private static void EncodeValueStringPart(string s, Span<byte> buffer)
        {
            for (int i = 0; i < s.Length; ++i)
            {
                char ch = s[i];

                if (ch > 127)
                {
                    throw new QPackEncodingException("ASCII header value.");
                }

                buffer[i] = (byte)ch;
            }
        }

        private static bool EncodeNameString(string s, Span<byte> buffer, out int length)
        {
            const int toLowerMask = 0x20;

            var i = 0;
            length = 0;

            if (buffer.IsEmpty)
            {
                return false;
            }

            buffer[0] = 0x30;

            if (!IntegerEncoder.Encode(s.Length, 3, buffer, out var nameLength))
            {
                return false;
            }

            i += nameLength;

            // TODO: use huffman encoding
            for (var j = 0; j < s.Length; j++)
            {
                if (i >= buffer.Length)
                {
                    return false;
                }

                buffer[i++] = (byte)(s[j] | (s[j] >= (byte)'A' && s[j] <= (byte)'Z' ? toLowerMask : 0));
            }

            length = i;
            return true;
        }

        private int EncodeStatusCode(int statusCode, Span<byte> buffer)
        {
            switch (statusCode)
            {
                case 200:
                case 204:
                case 206:
                case 304:
                case 400:
                case 404:
                case 500:
                    // TODO this isn't safe, some index can be larger than 64. Encoded here!
                    buffer[0] = (byte)(0xC0 | H3StaticTable.Instance.StatusIndex[statusCode]);
                    return 1;
                default:
                    // Send as Literal Header Field Without Indexing - Indexed Name
                    buffer[0] = 0x08;

                    var statusBytes = StatusCodes.ToStatusBytes(statusCode);
                    buffer[1] = (byte)statusBytes.Length;
                    ((ReadOnlySpan<byte>)statusBytes).CopyTo(buffer.Slice(2));

                    return 2 + statusBytes.Length;
            }
        }
    }
}
