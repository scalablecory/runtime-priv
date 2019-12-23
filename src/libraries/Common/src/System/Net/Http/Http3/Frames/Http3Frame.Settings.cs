// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3
{
    internal partial class Http3Frame
    {
        public void PrepareSettings()
        {
            Length = 0;
            Type = Http3FrameType.Settings;
        }
    }
}
