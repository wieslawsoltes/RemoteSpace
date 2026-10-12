int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++; Console.WriteLine("PASS " + name);
}
void Reject(Action action, string name)
{
    bool rejected = false;
    try { action(); } catch (Exception e) when (e is ArgumentException or FormatException or IOException) { rejected = true; }
    Check(rejected, name);
}
CatalogRegressions.Run();
WorkspaceReviewRegressions.Run();
WorkspaceImportRegressions.Run();
await FileStoreRegressions.RunAsync();
WorkspaceTests.Run(Check, Reject);
Console.WriteLine($"RESULT: {checks} workspace and input checks passed.");
