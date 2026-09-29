using EchoRelay.Core.Utils;

namespace EchoRelay.Core.Server.Messages.Login
{
    /// <summary>
    /// A halloween 2017 client's version check ("SNSClientVersionRequest"), sent to the login service right after it logs
    /// in. It waits for <see cref="ClientVersionResponse"/> before going on to the lobby. Its one byte is not used.
    /// </summary>
    public class ClientVersionRequest : Message
    {
        public override long MessageTypeSymbol => unchecked((long)0xE9FC2D49C62D70F0);

        public byte[] Data = Array.Empty<byte>();

        public override void Stream(StreamIO io)
        {
            if (io.StreamMode == StreamMode.Read)
                Data = io.ReadBytes((int)(io.Length - io.Position));
            else
                io.Write(Data);
        }

        public override string ToString() => $"{GetType().Name}(data={Convert.ToHexString(Data)})";
    }

    /// <summary>
    /// The answer to <see cref="ClientVersionRequest"/> ("SNSClientVersionResponse"): the oldest client version the service
    /// accepts. The client carries on if its own (its clientversion, e.g. 1508435927) is at least that; otherwise it stops
    /// with its "version too old" state.
    /// </summary>
    public class ClientVersionResponse : Message
    {
        public override long MessageTypeSymbol => unchecked((long)0x9C43281BC799FD9F);

        public ulong MinimumVersion;

        public ClientVersionResponse() { }
        public ClientVersionResponse(ulong minimumVersion) { MinimumVersion = minimumVersion; }

        public override void Stream(StreamIO io)
        {
            io.Stream(ref MinimumVersion);
        }

        public override string ToString() => $"{GetType().Name}(minimum_version={MinimumVersion})";
    }
}
