#if __WASM__
using System.Text;
using Uno.Foundation;

namespace RemoteSpace.App;

/// <summary>Await IndexedDB transaction completion, not just the in-memory filesystem write.
/// A single read/write transaction compares the full previous document before replacement,
/// so other browser windows cannot silently overwrite one another's saved devices.</summary>
internal static class BrowserWorkspaceStore
{
    public static Task<string> ReadAsync() => ExecuteAsync(false, null, null);
    public static Task<string> ReplaceAsync(string? expected, string value) => ExecuteAsync(true, expected, value);
    private static Task<string> ExecuteAsync(bool write, string? expected, string? value)
    {
        // Only base64 (not user-supplied JavaScript) is substituted in this expression.
        string code = "((write, expected64, value64) => {" + """
            const decode = s => s === null ? null : new TextDecoder('utf-8', {fatal:true}).decode(Uint8Array.from(atob(s), c => c.charCodeAt(0)));
            const expected = decode(expected64), value = decode(value64);
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
        """ + "})(" + (write ? "true" : "false") + "," + Base64Literal(expected) + "," + Base64Literal(value) + ")";
        return WebAssemblyRuntime.InvokeAsync(code, CancellationToken.None);
    }
    private static string Base64Literal(string? value) => value is null ? "null" : "'" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "'";
}
#endif
