import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// Exercise the exact interop module shipped in the controls package.
const source = readFileSync(new URL('../../src/RemoteSpace.Controls/BrowserSemanticInput.cs', import.meta.url), 'utf8')
    .replaceAll('\r\n', '\n').split('private const string Source = """\n')[1].split('\n""";')[0];
const { bind } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
class Node {
    listeners = [];
    constructor(role = null) { this.role = role; }
    getAttribute(name) { return name === 'role' ? this.role : null; }
    addEventListener(type, fn, capture = false) { this.listeners.push({type, fn, capture}); }
    removeEventListener(type, fn, capture = false) {
        this.listeners = this.listeners.filter(l => l.type !== type || l.fn !== fn || l.capture !== capture);
    }
    dispatch(type, e, capture = false) {
        for (const l of [...this.listeners]) if (l.type === type && l.capture === capture) l.fn(e);
    }
}
function fixture() {
    const root = new Node(), host = new Node(), item = new Node('menuitem');
    const close = bind(root, host);
    let native = 0, invoked = 0;
    function dispatch(type, {key = 'Enter', code = 'Enter', handled = true, target = item, inScope = true} = {}) {
        const e = {key, code, defaultPrevented: false, stopped: false,
            preventDefault() { this.defaultPrevented = true; }, stopPropagation() { this.stopped = true; },
            composedPath: () => [target, ...(inScope ? [root] : []), host]};
        host.dispatch(type, e, true);
        if (!e.stopped && type === 'keydown') {
            // Same ordering as the real host: semantic provider, root, then the
            // document-level native-XAML router. Removal does not alter the path.
            if (handled) { e.preventDefault(); invoked++; }
            root.dispatch(type, e);
        }
        if (!e.stopped) native++;
        return e;
    }
    return {root, host, item, close, dispatch, counts: () => ({native, invoked})};
}
test('handled menu invokes once and never re-enters native XAML on keydown or keyup', () => {
    const f = fixture();
    f.dispatch('keydown'); f.dispatch('keyup');
    assert.deepEqual(f.counts(), {native: 0, invoked: 1});
});
test('closing/removing the menu and changing focus cannot redirect its keyup to another command', () => {
    const f = fixture(); f.dispatch('keydown');
    f.dispatch('keyup', {target: new Node('button'), inScope: false});
    assert.deepEqual(f.counts(), {native: 0, invoked: 1});
});
test('held activation repeats cannot invoke newly focused commands', () => {
    const f = fixture(); f.dispatch('keydown');
    for (let i = 0; i < 1000; i++) f.dispatch('keydown', {target: new Node('button')});
    f.dispatch('keyup');
    assert.deepEqual(f.counts(), {native: 0, invoked: 1});
    f.dispatch('keydown'); f.dispatch('keyup');
    assert.deepEqual(f.counts(), {native: 0, invoked: 2});
});
test('unhandled menu keys and keys outside menu items retain native routing', () => {
    for (const options of [{handled:false}, {target:new Node('button')}, {inScope:false}, {key:'a',code:'KeyA'},
        {key:'ArrowDown',code:'ArrowDown'}, {key:'Escape',code:'Escape'}, {key:'Tab',code:'Tab'}]) {
        const f = fixture(); const e = f.dispatch('keydown', options); f.dispatch('keyup', options);
        assert.equal(e.stopped, false); assert.equal(f.counts().native, 2);
    }
});
test('Enter, numeric Enter and Space consume only their matching release', () => {
    const f = fixture();
    for (const code of ['Enter','NumpadEnter','Space']) f.dispatch('keydown', {code, key:code==='Space'?' ':'Enter'});
    f.dispatch('keyup', {key:'a',code:'KeyA'});
    for (const code of ['Enter','NumpadEnter','Space']) f.dispatch('keyup', {code, key:code==='Space'?' ':'Enter'});
    assert.deepEqual(f.counts(), {native:1,invoked:3});
});
test('blur and pagehide release bounded gesture state without swallowing later keys', () => {
    for (const type of ['blur','pagehide']) {
        const f = fixture(); f.dispatch('keydown'); f.host.dispatch(type, {});
        f.dispatch('keyup'); assert.equal(f.counts().native,1);
    }
});
test('disposal removes every listener and retained activation', () => {
    const f = fixture(); f.dispatch('keydown'); f.close(); f.close();
    assert.equal(f.root.listeners.length,0); assert.equal(f.host.listeners.length,0);
    f.dispatch('keyup'); assert.equal(f.counts().native,1);
});
