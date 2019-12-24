// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3
{
    internal enum Http3StreamType : ulong
    {
        Control = 0x00,
        Push = 0x01,
        QPackEncoder = 0x02,
        QPackDecoder = 0x03
    }
}
