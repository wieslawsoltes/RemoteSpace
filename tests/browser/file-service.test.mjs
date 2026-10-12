import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';

// Exercise the exact module shipped in SystemFileAccess, not a separate implementation.
const source = readFileSync(new URL('../../src/RemoteSpace.Controls/SystemFileAccess.cs', import.meta.url), 'utf8')
    .replaceAll('\r\n', '\n').split('private const string Source = """\n')[1].split('\n""";')[0].replaceAll('export function ', 'function ');
function service() {
    let next = 0, failClick = false;
    const elements = [], revoked = [], timers = new Map(), events = new Map();
    const context = vm.createContext({ Uint8Array, Blob,
        document: { body: { append: () => {} }, createElement: tag => {
            const e = { tag, style: {}, files: [], removed: false, click() { if (failClick) throw Error('picker denied'); }, remove() { this.removed = true; } };
            elements.push(e); return e;
        } },
        URL: { createObjectURL: () => 'blob:' + ++next, revokeObjectURL: value => revoked.push(value) },
        setTimeout: action => { const id = ++next; timers.set(id, action); return id; }, clearTimeout: id => timers.delete(id),
        addEventListener: (event, action) => events.set(event, action),
    });
    vm.runInContext(source, context);
    return { context, elements, revoked, timers, events, fail: () => failClick = true };
}
test('bounded typed bytes returned intact and DOM cleaned', async () => {
    const s = service(); const chosen = s.context.pick('.rdp', 4); const input = s.elements.at(-1);
    let read = 0;
    input.files = [{ name:'PC.RDP', size:4, arrayBuffer: async () => { read++; return new Uint8Array([255,0,128,7]).buffer; } }];
    await input.onchange(); const result = await chosen;
    assert.equal(result.cancelled, false); assert.equal(result.name, 'PC.RDP'); assert.deepEqual([...result.bytes], [255,0,128,7]);
    assert.equal(read, 1); assert.equal(input.removed, true); assert.equal(s.timers.size, 0);
});
test('cancel completes and frees single-picker guard', async () => {
    const s = service(); let picked = s.context.pick('.json', 100); s.elements.at(-1).oncancel();
    assert.equal((await picked).cancelled, true);
    picked = s.context.pick('.json', 100); s.elements.at(-1).oncancel(); assert.equal((await picked).cancelled, true);
});
test('metadata rejects oversized file before allocating content', async () => {
    const s = service(); const picked = s.context.pick('.json', 3); let read = false;
    s.elements.at(-1).files = [{ name:'data.json', size:4, arrayBuffer: async () => { read = true; return new ArrayBuffer(4); } }];
    await s.elements.at(-1).onchange(); await assert.rejects(picked, /byte limit/); assert.equal(read, false);
});
test('second bound rejects files that grew or lied about length', async () => {
    const s = service(); const picked = s.context.pick('.json', 3);
    s.elements.at(-1).files = [{ name:'data.json', size:1, arrayBuffer: async () => new ArrayBuffer(4) }];
    await s.elements.at(-1).onchange(); await assert.rejects(picked, /byte limit/);
});
test('concurrent pick, wrong extension and invalid limits fail', async () => {
    const s = service(); assert.throws(() => s.context.pick('json', 3), /limits/);
    const picked = s.context.pick('.json', 3); assert.throws(() => s.context.pick('.json', 3), /already open/);
    s.elements.at(-1).files = [{ name:'data.exe', size:1 }]; await s.elements.at(-1).onchange(); await assert.rejects(picked, /wrong extension/);
});
test('failed user gesture cleans picker state', async () => {
    const s = service(); s.fail(); await assert.rejects(s.context.pick('.rdp', 10), /picker denied/);
    assert.equal(s.elements.at(-1).removed, true); assert.equal(s.timers.size, 0);
    await assert.rejects(s.context.pick('.rdp', 10), /picker denied/);
});
test('downloads have bounded retained URLs and pagehide cleanup', () => {
    const s = service();
    for (let i = 0; i < 4; i++) s.context.download('file.json', new Uint8Array([1,2,3]), 'application/json');
    assert.throws(() => s.context.download('file.json', new Uint8Array([4]), 'application/json'), /Previous downloads/);
    assert.ok(s.elements.every(e => e.removed)); s.events.get('pagehide')(); assert.equal(s.revoked.length, 4);
    s.context.download('next.json', new Uint8Array(), 'application/json');
    for (const action of s.timers.values()) action(); assert.equal(s.revoked.length, 5);
});
test('failed download releases URL immediately', () => {
    const s = service(); s.fail(); assert.throws(() => s.context.download('file.json', new Uint8Array([1]), 'application/json'), /picker denied/);
    assert.equal(s.revoked.length, 1); assert.equal(s.elements.at(-1).removed, true);
});
