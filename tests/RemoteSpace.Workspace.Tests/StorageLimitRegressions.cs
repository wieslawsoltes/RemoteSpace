using System.Runtime.CompilerServices;
using System.Text.Json;
using RemoteSpace.Profiles;
using RemoteSpace.Workspace;

internal static class StorageLimitRegressions
{
    [ModuleInitializer]
    internal static void Run()
    {
        string wide = new('日', 255);
        var devices = Enumerable.Range(0, 3000).Select(_ => new SavedDevice(new ConnectionProfile
            { Host = "pc.test.invalid", Name = wide, Group = wide, UserName = wide, Domain = wide })).ToArray();
        var document = new WorkspaceDocument { Devices = devices };
        var catalog = new DeviceCatalog(document);
        // Each field and the device count are individually valid. Escaped JSON can
        // nevertheless exceed the document size accepted by the next application load.
        string previousBehavior = JsonSerializer.Serialize(document, WorkspaceJsonContext.Default.WorkspaceDocument);
        if (System.Text.Encoding.UTF8.GetByteCount(previousBehavior) <= DeviceCatalog.MaximumDocumentBytes)
            throw new Exception("Storage-limit fixture did not exceed the encoded document limit.");
        bool rejected = false;
        try { catalog.Serialize(); } catch (FormatException) { rejected = true; }
        if (!rejected || catalog.Snapshot().Devices.Length != devices.Length)
            throw new Exception("Oversized workspace was returned for persistence or device data was destroyed.");
        var smaller = new DeviceCatalog(document with { Devices = devices[..20] });
        if (DeviceCatalog.Parse(smaller.Serialize()).Snapshot().Devices.Length != 20)
            throw new Exception("Valid Unicode workspace did not round trip after size validation.");
        Console.WriteLine("PASS oversized workspace rejected before storage replacement; Unicode escaping and retained device data regressions");
    }
}
