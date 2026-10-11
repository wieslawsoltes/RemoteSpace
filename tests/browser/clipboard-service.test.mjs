import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import test from 'node:test';

// Execute the exact fixed module shipped by the reusable Uno service, not a reimplementation.
const source = readFileSync(new URL('../../src/RemoteSpace.Controls/SystemTextClipboard.cs', import.meta.url), 'utf8')
    .split('private const string Source = """\n')[1].split('\n""";')[0].replaceAll('export async function', 'async function');
function service(overrides = {}) {
    let content = 'initial';
    const context = vm.createContext({ isSecureContext: true, document: { hasFocus: () => true },
        navigator: { clipboard: { readText: async () => content, writeText: async text => { content = text; } } }, ...overrides });
    vm.runInContext(source, context);
    return context;
}
test('Unicode and empty text round trip without normalization', async () => {
    const s = service();
    for (const text of ['Żółć 日本語 Ελληνικά العربية 🙂', '', 'a\r\nb\nc\u0301']) {
        assert.equal(await s.writeText(text), 'written'); assert.equal(await s.readText(), text);
    }
});
test('Permission errors reject, never silently succeed or leak platform error details', async () => {
    const denied = () => { throw Error('private platform detail'); };
    const s = service({ navigator: { clipboard: { readText: denied, writeText: denied } } });
    await assert.rejects(s.readText(), /Clipboard read was denied or failed/);
    await assert.rejects(s.writeText('secret'), /Clipboard write was denied or failed/);
});
test('Insecure context and missing clipboard are diagnosed', async () => {
    await assert.rejects(service({ isSecureContext: false }).readText(), /secure browser context/);
    await assert.rejects(service({ navigator: {} }).writeText('text'), /secure browser context/);
});
test('Unfocused window never accesses the clipboard', async () => {
    await assert.rejects(service({ document: { hasFocus: () => false } }).readText(), /Focus the RemoteSpace/);
});
test('Write reports completion only after the platform promise settles', async () => {
    let release; let complete = false;
    const s = service({ navigator: { clipboard: { writeText: () => new Promise(resolve => { release = resolve; }) } } });
    const task = s.writeText('text').then(() => { complete = true; });
    await Promise.resolve(); assert.equal(complete, false); release(); await task; assert.equal(complete, true);
});
