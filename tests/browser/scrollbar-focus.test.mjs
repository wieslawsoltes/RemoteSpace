import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import test from 'node:test';

const source = readFileSync(new URL('../../src/RemoteSpace.Controls/BrowserScrollBarFocus.cs', import.meta.url), 'utf8')
    .replaceAll('\r\n','\n').split('private const string Source = """\n')[1].split('\n""";')[0].replaceAll('export function', 'function');
function setup() {
    const listeners = new Set();
    const root = { addEventListener(type, callback) { assert.equal(type, 'focusin'); listeners.add(callback); },
        removeEventListener(type, callback) { assert.equal(type, 'focusin'); listeners.delete(callback); } };
    const context = vm.createContext({ document: { getElementById: id => id === 'uno-semantics-root' ? root : null } });
    vm.runInContext(source, context);
    const focus = (id, role='scrollbar') => {
        const target = { getAttribute: name => name === 'role' ? role : name === 'xamlautomationid' ? id : null };
        for (const callback of [...listeners]) callback({ target });
    };
    return { context, listeners, focus };
}
test('only explicitly registered scrollbar focus is forwarded', () => {
    const s=setup(); let calls=0;
    s.context.register('a', () => calls++);
    s.focus('a'); s.focus('b'); s.focus('a','button');
    assert.equal(calls,1); assert.equal(s.listeners.size,1);
});
test('native focus synchronization cannot recurse indefinitely', () => {
    const s=setup(); let calls=0;
    s.context.register('a', () => { calls++; s.focus('a'); });
    s.focus('a'); assert.equal(calls,1);
});
test('callback failure restores reentrancy state', () => {
    const s=setup(); let calls=0;
    s.context.register('a', () => { calls++; throw Error('fixture failure'); });
    assert.throws(() => s.focus('a'), /fixture/);
    assert.throws(() => s.focus('a'), /fixture/);
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
test('removed registrations never receive late focus', () => {
    const s=setup(); let a=0,b=0;
    s.context.register('a',()=>a++); s.context.register('b',()=>b++);
    s.context.unregister('a'); s.focus('a'); s.focus('b');
    assert.equal(a,0); assert.equal(b,1);
});
