import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import test from 'node:test';

const source = readFileSync(new URL('../../src/RemoteSpace.Controls/BrowserScrollBarFocus.cs', import.meta.url), 'utf8')
    .replaceAll('\r\n','\n').split('private const string Source = """\n')[1].split('\n""";')[0].replaceAll('export function', 'function');
function setup() {
    const listeners = new Map(), microtasks = [], delayed = [], elements = new Map();
    const document = { activeElement: null };
    const root = { ownerDocument: document,
        addEventListener(type, callback) { let set = listeners.get(type); if (!set) listeners.set(type, set=new Set()); set.add(callback); },
        removeEventListener(type, callback) { listeners.get(type)?.delete(callback); } };
    document.getElementById = id => id === 'uno-semantics-root' ? root : null;
    const context = vm.createContext({ document, queueMicrotask: callback => microtasks.push(callback) });
    vm.runInContext(source, context);
    const element = (id, role='scrollbar') => {
        const key = role + ':' + id;
        if (!elements.has(key)) elements.set(key, { getAttribute: name => name === 'role' ? role : name === 'xamlautomationid' ? id : null });
        return elements.get(key);
    };
    const dispatch = (type, target, key='End') => {
        const event = { target, key, stopped:false, defaultPrevented:false,
            stopPropagation() { this.stopped=true; }, preventDefault() { this.defaultPrevented=true; } };
        for (const callback of [...(listeners.get(type) ?? [])]) callback(event);
        return event;
    };
    const focus = (id, role='scrollbar') => {
        document.activeElement = element(id, role); dispatch('focusin', document.activeElement);
    };
    const key = (id, role='scrollbar', value='End') => dispatch('keydown',element(id,role),value);
    const flush = () => { while(microtasks.length) microtasks.shift()(); };
    const pending = () => microtasks.length + delayed.length;
    const count = () => [...listeners.values()].reduce((n,s)=>n+s.size,0);
    return { context,listeners,focus,key,flush,microtasks,delayed,document,element,pending,count };
}
test('owned keyboard input synchronizes native focus before routing without changing the original event', () => {
    const s=setup(); let focused=null;
    s.context.register('bar',()=>focused='bar'); s.focus('bar');
    assert.equal(focused,null);
    const event=s.key('bar');
    assert.equal(focused,'bar'); assert.equal(event.key,'End');
    assert.equal(event.stopped,false); assert.equal(event.defaultPrevented,false); assert.equal(s.pending(),0);
});
test('focus-only native notifications never schedule another native focus request', () => {
    const s=setup(); let calls=0;
    s.context.register('a',()=>calls++); s.context.register('b',()=>calls++);
    for(let i=0;i<10000;i++) { s.focus('a');s.flush();s.focus('b');s.flush(); }
    assert.equal(calls,0);assert.equal(s.pending(),0);
});
test('asynchronous native echoes cannot sustain a cross-scrollbar focus loop', () => {
    const s=setup(); let calls=0;
    const native=id=>{calls++;s.delayed.push(()=>s.focus(id));};
    s.context.register('a',()=>native('a'));s.context.register('b',()=>native('b'));
    s.focus('a');s.key('a');s.flush();s.focus('b');s.key('b');s.flush();
    // Model independent host render turns, not just notifications in one batch.
    // The previous focusin/microtask relay creates another echo for each turn.
    for(let i=0;i<20 && s.delayed.length;i++) { s.delayed.shift()();s.flush(); }
    assert.equal(s.pending(),0);assert.equal(calls,2);
    s.focus('fit','button');s.flush();
    assert.equal(s.document.activeElement,s.element('fit','button'));
    assert.equal(s.pending(),0);
});
test('alternating physical bar keys stay native even with late replies between them', () => {
    const s=setup();let native;
    for(const id of ['a','b']) s.context.register(id,()=>{native=id;s.delayed.push(()=>s.focus(id));});
    for(let i=0;i<1000;i++) {
        const id=i%2?'a':'b';s.focus(id);const event=s.key(id);
        assert.equal(native,id);assert.equal(event.stopped,false);
        while(s.delayed.length){s.delayed.shift()();s.flush();}
    }
    assert.equal(s.pending(),0);
});
test('native focus synchronization cannot recursively dispatch another key', () => {
    const s=setup();let calls=0;
    s.context.register('a',()=>{calls++;s.key('a');});s.focus('a');s.key('a');assert.equal(calls,1);
});
test('callback failure restores the reentrancy guard', () => {
    const s=setup();let calls=0;
    s.context.register('a',()=>{calls++;throw Error('fixture failure');});s.focus('a');
    assert.throws(()=>s.key('a'),/fixture/);assert.throws(()=>s.key('a'),/fixture/);assert.equal(calls,2);
});
test('unregistered controls and off-focus events cannot steal native focus', () => {
    const s=setup();let calls=0;s.context.register('a',()=>calls++);
    s.focus('fit','button');s.key('a');s.key('fit','button');
    s.focus('unknown');s.key('unknown');assert.equal(calls,0);
});
test('registered toolbar and canvas keystrokes are left untouched', () => {
    const s=setup();let calls=0;s.context.register('a',()=>calls++);
    for(const role of ['button','textbox','pane']) {s.focus('a',role);const event=s.key('a',role,'a');assert.equal(event.stopped,false);assert.equal(event.defaultPrevented,false);}
    assert.equal(calls,0);
});
test('registration count is bounded and disposal removes the last listener', () => {
    const s=setup();for(let i=0;i<64;i++)s.context.register('s'+i,()=>{});
    assert.throws(()=>s.context.register('excess',()=>{}),/limit/);
    assert.throws(()=>s.context.register('s0',()=>{}),/Duplicate/);
    for(let i=0;i<64;i++)s.context.unregister('s'+i);
    assert.equal(s.count(),0);s.context.unregister('s0');
    s.context.register('again',()=>{});assert.equal(s.count(),1);s.context.unregister('again');assert.equal(s.count(),0);
});
test('removed registrations never receive late focus or keyboard callbacks', () => {
    const s=setup();let a=0,b=0;s.context.register('a',()=>a++);s.context.register('b',()=>b++);
    s.context.unregister('a');s.focus('a');s.key('a');s.focus('b');s.key('b');assert.equal(a,0);assert.equal(b,1);
});
test('disposed bindings retain no queued references or callbacks after ID reuse', () => {
    const s=setup();let old=0,current=0;
    s.context.register('bar',()=>old++);s.focus('bar');s.context.unregister('bar');
    s.context.register('bar',()=>current++);s.flush();assert.equal(old,0);assert.equal(current,0);
    s.focus('bar');s.key('bar');assert.equal(current,1);assert.equal(s.pending(),0);
});
