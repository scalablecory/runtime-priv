// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3
{
    internal static partial class Http3Frame
    {
        public static bool TryReadIntegerPair(ReadOnlySpan<byte> buffer, out long a, out long b, out int bytesRead)
        {
            if (VariableLengthIntegerHelper.TryRead(buffer, out a, out int aLength))
            {
                buffer = buffer.Slice(aLength);
                if (VariableLengthIntegerHelper.TryRead(buffer, out b, out int bLength))
                {
                    bytesRead = aLength + bLength;
                    return true;
                }
            }

            b = 0;
            bytesRead = 0;
            return false;
        }
    }
}
