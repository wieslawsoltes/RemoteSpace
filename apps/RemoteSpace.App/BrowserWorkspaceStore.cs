#if __WASM__
using System.Runtime.InteropServices.JavaScript;
using System.Text;

namespace RemoteSpace.App;

/// <summary>Transactional browser workspace persistence with compare-and-replace conflict detection.
/// Uses source-generated .NET interop; no eval, Uno DOM-only async helpers or user-generated script.</summary>
internal static partial class BrowserWorkspaceStore
{
    private static readonly Task<JSObject> Module = JSHost.ImportAsync("remotespace.workspace",
        "data:text/javascript;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(Source)));
    public static async Task<string> ReadAsync() { await Module; return await Execute(false, null, null); }
    public static async Task<string> ReplaceAsync(string? expected, string value) { await Module; return await Execute(true, expected, value); }
    [JSImport("execute", "remotespace.workspace")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string> Execute(bool write, string? expected, string? value);
    private const string Source = """
export function execute(write, expected, value) {
            return new Promise((resolve, reject) => {
                let db, tx, finished = false;
                const fail = e => { if (finished) return; finished = true; clearTimeout(timer); try { if (tx) tx.abort(); } catch {} if (db) db.close(); reject(String(e?.message || e)); };
                const timer = setTimeout(() => fail(new Error('Browser workspace storage timed out. Existing data was preserved.')), 15000);
                const open = indexedDB.open('RemoteSpace.Workspaces', 1);
                open.onupgradeneeded = () => open.result.createObjectStore('documents');
                open.onerror = () => fail(open.error);
                open.onblocked = () => fail(new Error('Close older RemoteSpace windows to upgrade workspace storage.'));
                open.onsuccess = () => {
                    db = open.result;
                    if (finished) { db.close(); return; }
                    db.onversionchange = () => db.close();
                    try {
                        tx = db.transaction('documents', write ? 'readwrite' : 'readonly');
                        const store = tx.objectStore('documents');
                        const read = store.get('workspace'); let result = '';
                        read.onerror = () => fail(read.error);
                        read.onsuccess = () => {
                            try {
                                const current = read.result === undefined ? null : read.result;
                                if (current !== null && typeof current !== 'string') throw new Error('Invalid persisted workspace. Existing data was preserved.');
                                if (write) {
                                    if (current !== expected) throw new Error('Saved devices changed in another window. Reload before saving; the other window’s data was preserved.');
                                    store.put(value, 'workspace'); result = 'saved';
                                } else result = current ?? '';
                            } catch (e) { fail(e); }
                        };
                        tx.onabort = () => fail(tx.error || new Error('Workspace transaction aborted.'));
                        tx.onerror = () => fail(tx.error);
                        tx.oncomplete = () => { if (!finished) { finished = true; clearTimeout(timer); db.close(); resolve(result); } };
                    } catch (e) { fail(e); }
                };
            });

}
""";
}
#endif
