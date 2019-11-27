using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Resources;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Headers
{
    internal class AltSvcHeaderParser : BaseHeaderParser
    {
        private const long DefaultMaxAgeTicks = 24 * TimeSpan.TicksPerHour;

        public AltSvcHeaderParser()
            : base(supportsMultipleValues: true)
        {
        }

        protected override int GetParsedValueLength(string value, int startIndex, object storeValue,
            out object parsedValue)
        {
            Debug.Assert(startIndex >= 0);
            Debug.Assert(startIndex < value.Length);

            if (string.IsNullOrEmpty(value))
            {
                parsedValue = null;
                return 0;
            }

            int idx = startIndex;

            if (!TryReadPercentEncodedAlpnProtocolName(value, idx, out string alpnProtocolName, out int alpnProtocolNameLength))
            {
                parsedValue = null;
                return 0; // TODO: this (and other failures) should skip the entire remaining header line?
            }

            idx += alpnProtocolNameLength;

            if (alpnProtocolName == "clear")
            {
                if (idx != value.Length)
                {
                    // Clear has no parameters and should be the only Alt-Svc value present, so there should be nothing after it.
                    parsedValue = null;
                    return 0;
                }

                parsedValue = AlternateService.Clear;
                return idx - startIndex;
            }

            if (idx == value.Length || value[idx++] != '=')
            {
                parsedValue = null;
                return 0;
            }

            if (!TryReadQuotedAltAuthority(value, idx, out string altAuthorityHost, out int altAuthorityPort, out int altAuthorityLength))
            {
                parsedValue = null;
                return 0;
            }
            idx += altAuthorityLength;

            // Parse parameters.
            int? maxAge = null;

            while (idx != value.Length)
            {
                // Skip OWS.
                while (idx != value.Length && IsOptionalWhiteSpace(value[idx])) ++idx;

                // Get the parameter key length.
                int tokenLength = HttpRuleParser.GetTokenLength(value, idx);
                if (tokenLength == 0)
                {
                    // End of header, or end of segment in a multi-value header.
                    break;
                }

                if (value[idx + tokenLength + 1] != '=')
                {
                    parsedValue = null;
                    return 0;
                }

                if (tokenLength == 2 && value[idx] == 'm' && value[idx + 1] == 'a')
                {
                    // Parse "ma" (Max Age).

                    idx += 3; // Skip "ma="
                    if (!TryReadTokenOrQuotedInt32(value, idx, out int maxAgeTmp, out int parameterLength))
                    {
                        parsedValue = null;
                        return 0;
                    }

                    if (maxAge == null)
                    {
                        maxAge = maxAgeTmp;
                    }
                    else
                    {
                        // RFC makes it unclear what to do if a duplciate parameter is found. For now, take the minimum.
                        maxAge = Math.Min(maxAge.GetValueOrDefault(), maxAgeTmp);
                    }

                    idx += parameterLength;
                }
                else
                {
                    // Some unknown parameter. Skip it.

                    idx += tokenLength + 1;
                    if (!TrySkipTokenOrQuoted(value, idx, out int parameterLength))
                    {
                        parsedValue = null;
                        return 0;
                    }
                    idx += parameterLength;
                }
            }

            // If no "ma" parameter present, use the default.
            var maxAgeTimeSpan = new TimeSpan(maxAge * TimeSpan.TicksPerSecond ?? DefaultMaxAgeTicks);

            parsedValue = new AlternateService(alpnProtocolName, altAuthorityHost, altAuthorityPort, maxAgeTimeSpan);
            return idx - startIndex;
        }

        private static bool IsOptionalWhiteSpace(char ch)
        {
            return ch == ' ' || ch == '\t';
        }

        private static bool TryReadPercentEncodedAlpnProtocolName(string value, int startIndex, out string result, out int readLength)
        {
            int tokenLength = HttpRuleParser.GetTokenLength(value, startIndex);

            if (tokenLength == 0)
            {
                result = null;
                readLength = 0;
                return false;
            }

            ReadOnlySpan<char> span = value.AsSpan(startIndex, tokenLength);

            readLength = tokenLength;

            // Special-case expected values to avoid allocating one-off strings.
            switch (span.Length)
            {
                case 2:
                    if (span[0] == 'h')
                    {
                        char ch = span[1];
                        if (ch == '3')
                        {
                            result = "h3";
                            return true;
                        }
                        if (ch == '2')
                        {
                            result = "h2";
                            return true;
                        }
                    }
                    break;
                case 3:
                    if (span[0] == 'h' && span[1] == '2' && span[2] == 'c')
                    {
                        result = "h2c";
                        readLength = 3;
                        return true;
                    }
                    break;
                case 5:
                    if (span.SequenceEqual("clear"))
                    {
                        result = "clear";
                        return true;
                    }
                    break;
                case 10:
                    if (span.StartsWith("http%2") && span[7] == '1' && span[8] == '.')
                    {
                        char ch = span[6];
                        if (ch == 'F' || ch == 'f')
                        {
                            ch = span[9];
                            if (ch == '1')
                            {
                                result = "http/1.1";
                                return true;
                            }
                            if (ch == '0')
                            {
                                result = "http/1.0";
                                return true;
                            }
                        }
                    }
                    break;
            }

            // Unrecognized ALPN protocol name. Percent-decode.
            return TryReadPercentEncodedValue(span, out result);

        }

        private static bool TryReadPercentEncodedValue(ReadOnlySpan<char> value, out string result)
        {
            int idx = value.IndexOf('%');

            if (idx == -1)
            {
                result = new string(value);
                return true;
            }

            var builder = new ValueStringBuilder(value.Length <= 128 ? stackalloc char[128] : new char[value.Length]);

            do
            {
                if (idx != 0)
                {
                    builder.Append(value.Slice(0, idx));
                }

                if ((value.Length - idx) < 3 || !TryReadHexDigit(value[1], out int hi) || !TryReadHexDigit(value[2], out int lo))
                {
                    result = null;
                    return false;
                }

                builder.Append((char)((hi << 8) | lo));

                value = value.Slice(idx + 3);
                idx = value.IndexOf('%');
            }
            while (idx != -1);

            if (value.Length != 0)
            {
                builder.Append(value);
            }

            result = builder.ToString();
            return true;
        }

        private static bool TryReadHexDigit(char ch, out int nibble)
        {
            if (ch >= '0' && ch <= '9')
            {
                nibble = ch - '0';
                return true;
            }

            if (ch >= 'a' && ch <= 'f')
            {
                nibble = ch - 'a' + 10;
                return true;
            }

            if (ch >= 'A' && ch <= 'F')
            {
                nibble = ch - 'a' + 10;
                return true;
            }

            nibble = 0;
            return false;
        }

        private static bool TryReadQuotedAltAuthority(string value, int startIndex, out string host, out int port, out int readLength)
        {
            if (HttpRuleParser.GetQuotedStringLength(value, startIndex, out int quotedLength) != HttpParseResult.Parsed)
            {
                goto parseError;
            }

            Debug.Assert(value[startIndex] == '"' && value[startIndex + quotedLength - 1] == '"', $"{nameof(HttpRuleParser.GetQuotedStringLength)} should return {nameof(HttpParseResult.NotParsed)} if the opening/closing quotes are missing.");
            ReadOnlySpan<char> quoted = value.AsSpan(startIndex + 1, quotedLength - 2);

            int idx = quoted.IndexOf(':');
            if (idx == -1)
            {
                goto parseError;
            }

            // Parse the port. Port comes at the end of the string, but do this first so we don't allocate a host string if port fails to parse.
            if (!TryReadQuotedInt32Value(quoted.Slice(idx + 1), out port))
            {
                goto parseError;
            }

            // Parse the optional host.
            if (idx == 0)
            {
                host = null;
            }
            else if (!TryReadQuotedValue(quoted.Slice(0, idx), out host))
            {
                goto parseError;
            }

            readLength = quotedLength;
            return true;

        parseError:
            host = null;
            port = 0;
            readLength = 0;
            return false;
        }

        private static bool TryReadQuotedValue(ReadOnlySpan<char> value, out string result)
        {
            int idx = value.IndexOf('\\');

            if (idx == -1)
            {
                // Hostnames shouldn't require quoted pairs, so this should be the hot path.
                result = value.Length != 0 ? new string(value) : null;
                return true;
            }

            var builder = new ValueStringBuilder(stackalloc char[128]);

            do
            {
                if (idx + 1 == value.Length)
                {
                    // quoted-pair requires two characters: the quote, and the quoted character.
                    builder.Dispose();
                    result = null;
                    return false;
                }

                if (idx != 0)
                {
                    builder.Append(value.Slice(0, idx));
                }

                builder.Append(value[idx + 1]);

                value = value.Slice(idx + 2);
                idx = value.IndexOf('\\');
            }
            while (idx != -1);

            if (value.Length != 0)
            {
                builder.Append(value);
            }

            result = builder.ToString();
            return true;
        }

        private static bool TryReadTokenOrQuotedInt32(string value, int startIndex, out int result, out int readLength)
        {
            if (startIndex >= value.Length)
            {
                result = 0;
                readLength = 0;
                return false;
            }

            if (HttpRuleParser.IsTokenChar(value[startIndex]))
            {
                // No reason for integers to be quoted, so this should be the hot path.

                int tokenLength = HttpRuleParser.GetTokenLength(value, startIndex);

                readLength = tokenLength;
                return HeaderUtilities.TryParseInt32(value, startIndex, tokenLength, out result);
            }

            if (HttpRuleParser.GetQuotedStringLength(value, startIndex, out int quotedLength) == HttpParseResult.Parsed)
            {
                readLength = quotedLength;
                return TryReadQuotedInt32Value(value.AsSpan(1, quotedLength - 2), out result);
            }

            result = 0;
            readLength = 0;
            return false;
        }

        private static bool TryReadQuotedInt32Value(ReadOnlySpan<char> value, out int result)
        {
            if (value.Length == 0)
            {
                result = 0;
                return false;
            }

            int port = 0;

            foreach (char ch in value)
            {
                // The port shouldn't ever need a quoted-pair, but they're still valid... skip if found.
                if (ch == '\\') continue;

                if (ch < '0' || ch > '9')
                {
                    result = 0;
                    return false;
                }

                long portTmp = port * 10L + (ch - '0');

                if (portTmp > int.MaxValue)
                {
                    result = 0;
                    return false;
                }

                port = (int)portTmp;
            }

            result = port;
            return true;
        }

        private static bool TrySkipTokenOrQuoted(string value, int startIndex, out int readLength)
        {
            if (startIndex >= value.Length)
            {
                readLength = 0;
                return false;
            }

            if (HttpRuleParser.IsTokenChar(value[startIndex]))
            {
                readLength = HttpRuleParser.GetTokenLength(value, startIndex);
                return true;
            }

            if (HttpRuleParser.GetQuotedStringLength(value, startIndex, out int quotedLength) == HttpParseResult.Parsed)
            {
                readLength = quotedLength;
                return true;
            }

            readLength = 0;
            return false;
        }
    }
}
