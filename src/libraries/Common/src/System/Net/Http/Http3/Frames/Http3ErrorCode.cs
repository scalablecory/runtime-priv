// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3
{
    internal enum Http3ErrorCode : uint
    {
        /// <summary>
        /// HTTP_NO_ERROR (0x100):
        /// No error. This is used when the connection or stream needs to be closed, but there is no error to signal.
        /// </summary>
        NoError = 0x100,
        /// <summary>
        /// HTTP_GENERAL_PROTOCOL_ERROR (0x101):
        /// Peer violated protocol requirements in a way which doesn’t match a more specific error code,
        /// or endpoint declines to use the more specific error code.
        /// </summary>
        ProtocolError = 0x101,
        /// <summary>
        /// HTTP_INTERNAL_ERROR (0x102):
        /// An internal error has occurred in the HTTP stack.
        /// </summary>
        InternalError = 0x102,
        /// <summary>
        ///  HTTP_STREAM_CREATION_ERROR (0x103):
        /// The endpoint detected that its peer created a stream that it will not accept.
        /// </summary>
        StreamCreationError = 0x103,
        /// <summary>
        /// HTTP_CLOSED_CRITICAL_STREAM (0x104):
        /// A stream required by the connection was closed or reset.
        /// </summary>
        ClosedCriticalStream = 0x104,
        /// <summary>
        /// HTTP_UNEXPECTED_FRAME (0x105):
        /// A frame was received which was not permitted in the current state.
        /// </summary>
        UnexpectedFrame = 0x105,
        /// <summary>
        /// HTTP_FRAME_ERROR (0x106):
        /// A frame that fails to satisfy layout requirements or with an invalid size was received.
        /// </summary>
        FrameError = 0x106,
        /// <summary>
        /// HTTP_EXCESSIVE_LOAD (0x107):
        /// The endpoint detected that its peer is exhibiting a behavior that might be generating excessive load.
        /// </summary>
        ExcessiveLoad = 0x107,
        /// <summary>
        /// HTTP_WRONG_STREAM (0x108):
        /// A frame was received on a stream where it is not permitted.
        /// </summary>
        WrongStream = 0x108,
        /// <summary>
        /// HTTP_ID_ERROR (0x109):
        /// A Stream ID, Push ID, or Placeholder ID was used incorrectly, such as exceeding a limit, reducing a limit, or being reused.
        /// </summary>
        IdError = 0x109,
        /// <summary>
        /// HTTP_SETTINGS_ERROR (0x10A):
        /// An endpoint detected an error in the payload of a SETTINGS frame: a duplicate setting was detected,
        /// a client-only setting was sent by a server, or a server-only setting by a client.
        /// </summary>
        SettingsError = 0x10a,
        /// <summary>
        /// HTTP_MISSING_SETTINGS (0x10B):
        /// No SETTINGS frame was received at the beginning of the control stream.
        /// </summary>
        MissingSettings = 0x10b,
        /// <summary>
        /// HTTP_REQUEST_REJECTED (0x10C):
        /// A server rejected a request without performing any application processing.
        /// </summary>
        RequestRejected = 0x10c,
        /// <summary>
        /// HTTP_REQUEST_CANCELLED (0x10D):
        /// The request or its response (including pushed response) is cancelled.
        /// </summary>
        RequestCancelled = 0x10d,
        /// <summary>
        /// HTTP_REQUEST_INCOMPLETE (0x10E):
        /// The client’s stream terminated without containing a fully-formed request.
        /// </summary>
        RequestIncomplete = 0x10e,
        /// <summary>
        /// HTTP_EARLY_RESPONSE (0x10F):
        /// The remainder of the client’s request is not needed to produce a response. For use in STOP_SENDING only.
        /// </summary>
        EarlyResponse = 0x10f,
        /// <summary>
        /// HTTP_CONNECT_ERROR (0x110):
        /// The connection established in response to a CONNECT request was reset or abnormally closed.
        /// </summary>
        ConnectError = 0x110,
        /// <summary>
        /// HTTP_VERSION_FALLBACK (0x111):
        /// The requested operation cannot be served over HTTP/3. The peer should retry over HTTP/1.1.
        /// </summary>
        VersionFallback = 0x111,
    }
}
