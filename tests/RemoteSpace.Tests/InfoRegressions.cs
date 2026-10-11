using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using RemoteSpace.Core;
using RemoteSpace.Protocol;

internal static class InfoRegressions
{
    [ModuleInitializer]
    internal static void CheckClientInfo()
    {
        byte[] bytes = Activation.ClientInfo(new ConnectionOptions { Host = "localhost" }, new SessionCredentials("user", "secret"), false);
        var r = new PacketReader(bytes);
        if (r.U16() != 0x40 || r.U16() != 0) throw new Exception("Client info security header regression.");
        r.U32(); uint flags = r.U32();
        if ((flags & 0x33) != 0x33 || (flags & 0x80) != 0) throw new Exception("Client info flags regression: normal logon required, bulk compression not negotiated.");
        if (flags != Activation.ClientInfoFlags) throw new Exception("Client info flag constant differs from wire data.");
        Console.WriteLine("PASS client info normal-logon flags and compression policy regression");
        byte[] initial = Mcs.ConnectInitial(new() { Host = "localhost" }, SecurityProtocols.Tls);
        int core = initial.AsSpan().IndexOf(new byte[] { 1, 0xc0, 216, 0 });
        if (core < 0 || BinaryPrimitives.ReadUInt32LittleEndian(initial.AsSpan(core + 20, 4)) != Mcs.ClientProtocolBuild || Mcs.ClientProtocolBuild <= 419)
            throw new Exception("Client core build must not select pre-resize server compatibility behavior.");
        Console.WriteLine("PASS independent client build preserves server-side resize negotiation");
    }
}
