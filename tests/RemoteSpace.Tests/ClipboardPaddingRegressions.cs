using System.Runtime.CompilerServices;
using RemoteSpace.Protocol;

internal static class ClipboardPaddingRegressions
{
    [ModuleInitializer]
    internal static void Run()
    {
        byte[] monitorReady = [1, 0, 0, 0, 0, 0, 0, 0];
        foreach (int padding in new[] { 0, 4 })
        {
            var pdu = ClipboardCodec.Parse(monitorReady.Concat(new byte[padding]).ToArray());
            if (pdu.Type != 1 || pdu.Data.Length != 0) throw new Exception("Monitor-ready clipboard padding failed.");
        }
        byte[] text = ClipboardCodec.TextResponse("A\U0001f642");
        if (ClipboardCodec.ReadText(ClipboardCodec.Parse(text.Concat(new byte[4]).ToArray()).Data) != "A\U0001f642") throw new Exception("Clipboard trailer entered Unicode text.");
        for (int count = 1; count < 9; count++)
        {
            if (count == 4) continue;
            bool failed = false;
            try { ClipboardCodec.Parse(monitorReady.Concat(new byte[count]).ToArray()); } catch (RdpProtocolException) { failed = true; }
            if (!failed) throw new Exception("Invalid clipboard trailer length accepted.");
        }
        bool nonzero = false;
        try { ClipboardCodec.Parse(monitorReady.Concat(new byte[] { 1, 0, 0, 0 }).ToArray()); } catch (RdpProtocolException) { nonzero = true; }
        if (!nonzero) throw new Exception("Nonzero clipboard trailer accepted.");
        bool invalidUnicode = false;
        try { ClipboardCodec.TextResponse("\ud800"); } catch (ArgumentException) { invalidUnicode = true; }
        if (!invalidUnicode) throw new Exception("Malformed clipboard UTF-16 accepted.");
        Console.WriteLine("PASS 12 clipboard alignment and strict UTF-16 regressions");
    }
}
