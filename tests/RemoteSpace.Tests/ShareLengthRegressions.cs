using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using RemoteSpace.Protocol;

internal static class ShareLengthRegressions
{
    [ModuleInitializer]
    internal static void CheckShareLengths()
    {
        // Microsoft's independent client/server wire examples, not a self-round-trip:
        // MS-RDPBCGR 4.1.16: https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpbcgr/8ae890a0-3030-48b1-9982-f857b9161eff
        // MS-RDPBCGR 4.1.21: https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpbcgr/d5624241-aa4d-465a-b66d-698b19d0d826
        byte[] server = Convert.FromHexString("1A001700EA03EA03010012021A00140000000200EF03EA030000");
        byte[] client = Convert.FromHexString("1A001700EF03EA03010000010C00140000000100000000000000");
        foreach (byte[] packet in new[] { server, client })
        {
            var share = Activation.ParseShares(packet).Single(); var data = Activation.ParseData(share.Body);
            if (data.Type != 20 || data.ShareId != 0x103ea || data.Body.Length != 8) throw new Exception("Published control PDU did not decode.");
        }
        Console.WriteLine("PASS published client and server share-data length forms");
        var body = Activation.ParseShares(server).Single().Body;
        for (int length = 1; length < 40; length++)
        {
            if (length is 12 or 26) continue;
            var mutated = (byte[])body.Clone(); BinaryPrimitives.WriteUInt16LittleEndian(mutated.AsSpan(6), (ushort)length);
            bool rejected = false; try { Activation.ParseData(mutated); } catch (RdpProtocolException) { rejected = true; }
            if (!rejected) throw new Exception("Corrupt share-data length accepted: " + length);
        }
        for (int length = 0; length < 12; length++)
        {
            bool rejected = false; try { Activation.ParseData(body.AsSpan(0, length)); } catch (RdpProtocolException) { rejected = true; }
            if (!rejected) throw new Exception("Truncated share-data header accepted.");
        }
        body[9] = 0x20;
        bool compressedRejected = false; try { Activation.ParseData(body); } catch (RdpProtocolException) { compressedRejected = true; }
        if (!compressedRejected) throw new Exception("Unnegotiated bulk compression accepted.");
        Console.WriteLine("PASS 50 share-data corruption, truncation and compression regressions");
    }
}
