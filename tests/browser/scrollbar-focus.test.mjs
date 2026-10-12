import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import test from 'node:test';

const source = readFileSync(new URL('../../src/RemoteSpace.Controls/BrowserScrollBarFocus.cs', import.meta.url), 'utf8')
    .replaceAll('\r\n','\n').split('private const string Source = """\n')[1].split('\n""";')[0].replaceAll('export function', 'function');
function setup() {
    const listeners = new Set(), microtasks = [];
    const document = { activeElement: null };
    const root = { ownerDocument: document,
        addEventListener(type, callback) { assert.equal(type, 'focusin'); listeners.add(callback); },
        removeEventListener(type, callback) { assert.equal(type, 'focusin'); listeners.delete(callback); } };
    document.getElementById = id => id === 'uno-semantics-root' ? root : null;
    const context = vm.createContext({ document, queueMicrotask: callback => microtasks.push(callback) });
    vm.runInContext(source, context);
    const focus = (id, role='scrollbar') => {
        const target = { getAttribute: name => name === 'role' ? role : name === 'xamlautomationid' ? id : null };
        document.activeElement = target;
        for (const callback of [...listeners]) callback({ target });
    };
    const flush = () => { while (microtasks.length) microtasks.shift()(); };
    return { context, listeners, focus, flush, microtasks, document };
}
test('only explicitly registered scrollbar focus is forwarded after the event turn', () => {
    const s=setup(); let calls=0;
    s.context.register('a', () => calls++);
    s.focus('a'); assert.equal(calls,0); s.flush();
    s.focus('b'); s.flush(); s.focus('a','button'); s.flush();
    assert.equal(calls,1); assert.equal(s.listeners.size,1);
});
test('native focus synchronization cannot recurse indefinitely', () => {
    const s=setup(); let calls=0;
    s.context.register('a', () => { calls++; s.focus('a'); });
    s.focus('a'); s.flush(); assert.equal(calls,1); assert.equal(s.microtasks.length,0);
});
test('callback failure restores reentrancy state', () => {
    const s=setup(); let calls=0;
    s.context.register('a', () => { calls++; throw Error('fixture failure'); });
    s.focus('a'); assert.throws(() => s.flush(), /fixture/);
    s.focus('a'); assert.throws(() => s.flush(), /fixture/);
    assert.equal(calls,2);
});
test('registrations are bounded and disposal removes the last listener', () => {
    const s=setup();
    for(let i=0;i<64;i++) s.context.register('s'+i,()=>{});
    assert.throws(()=>s.context.register('excess',()=>{}),/limit/);
    assert.throws(()=>s.context.register('s0',()=>{}),/Duplicate/);
    for(let i=0;i<64;i++) s.context.unregister('s'+i);
    assert.equal(s.listeners.size,0);
    s.context.unregister('s0'); s.context.register('again',()=>{}); assert.equal(s.listeners.size,1);
    s.context.unregister('again'); assert.equal(s.listeners.size,0);
});
test('removed registrations never receive queued or late focus', () => {
    const s=setup(); let a=0,b=0;
    s.context.register('a',()=>a++); s.context.register('b',()=>b++);
    s.focus('a'); s.context.unregister('a'); s.flush();
    s.focus('a'); s.flush(); s.focus('b'); s.flush();
    assert.equal(a,0); assert.equal(b,1);
});
test('a batch of competing native focus notifications relays only the final owner', () => {
    const s=setup(); let a=0,b=0;
    s.context.register('horizontal',()=>a++); s.context.register('vertical',()=>b++);
    for(let i=0;i<10000;i++) { s.focus('horizontal'); s.focus('vertical'); }
    assert.equal(s.microtasks.length,1); s.flush();
    assert.equal(a,0); assert.equal(b,1); assert.equal(s.microtasks.length,0);
});
test('moving focus to the toolbar cancels a pending scrollbar echo', () => {
    const s=setup(); let calls=0;
    s.context.register('bar',()=>calls++);
    s.focus('bar'); s.focus('fit','button'); s.flush();
    assert.equal(calls,0);
});
test('a detached or no longer focused target cannot steal focus on the queued turn', () => {
    const s=setup(); let calls=0;
    s.context.register('bar',()=>calls++);
    s.focus('bar'); s.document.activeElement=null; s.flush();
    assert.equal(calls,0);
});
test('disposing a binding invalidates its queued relay even after registration is reused', () => {
    const s=setup(); let old=0,current=0;
    s.context.register('bar',()=>old++); s.focus('bar'); s.context.unregister('bar');
    s.context.register('bar',()=>current++); s.flush();
    assert.equal(old,0); assert.equal(current,0);
    s.focus('bar'); s.flush(); assert.equal(current,1);
});
