import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
const source = readFileSync(new URL('../../src/RemoteSpace.Controls/SessionControlKeys.cs', import.meta.url),'utf8')
    .replaceAll('\r\n','\n').split('private const string Source = """\n')[1].split('\n""";')[0];
const api = await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'));
function fixture() {
    const listeners = new Map(), calls = [], root = {}, scope = {getAttribute:name=>name==='xamlautomationid'?'first':null};
    const registrations = new Map([['first',value=>calls.push(value)]]);
    const host = {
        addEventListener(type,listener) {const group=listeners.get(type)??new Set();group.add(listener);listeners.set(type,group);},
        removeEventListener(type,listener) {listeners.get(type)?.delete(listener);}
    };
    const router = api.bind(root,host,registrations);
    const event = (values={}) => ({code:'Home',ctrlKey:true,altKey:true,shiftKey:false,metaKey:false,repeat:false,
        isComposing:false,defaultPrevented:false,stopped:false, getModifierState:()=>false,
        composedPath:()=>[scope,root],preventDefault(){this.defaultPrevented=true;},stopPropagation(){this.stopped=true;},...values});
    const fire = (type,values) => {const e=event(values);for(const listener of listeners.get(type)??[])listener(e);return e;};
    return {listeners,calls,root,scope,registrations,host,router,fire};
}
test('Home and Pause invoke their local owner synchronously before native bubbling',()=>{
    const f=fixture();for(const [code,command] of [['Home',1],['Pause',2]]){
        const e=f.fire('keydown',{code});assert.equal(f.calls.at(-1),command);assert.ok(e.defaultPrevented&&e.stopped);
        assert.ok(f.fire('keyup',{code}).stopped);
    }f.router.dispose();
});
test('all modifier and repeat combinations admit only exact initial Ctrl+Alt',()=>{
    const f=fixture();for(const code of ['Home','Pause'])for(let flags=0;flags<64;flags++){
        const e=f.fire('keydown',{code,ctrlKey:!!(flags&1),altKey:!!(flags&2),shiftKey:!!(flags&4),metaKey:!!(flags&8),
            getModifierState:()=>!!(flags&16),repeat:!!(flags&32)});
        assert.equal(e.stopped,flags===3,`${code}/${flags}`);
        assert.equal(f.fire('keyup',{code}).stopped,flags===3);
    }assert.deepEqual(f.calls,[1,2]);f.router.dispose();
});
test('unmodified navigation, ordinary text and unknown codes are untouched',()=>{
    const f=fixture();for(const code of ['KeyA','Enter','Space','End','PageUp','ArrowLeft','','Numpad7']){
        assert.equal(f.fire('keydown',{code}).stopped,false);assert.equal(f.fire('keyup',{code}).stopped,false);
    }assert.deepEqual(f.calls,[]);f.router.dispose();
});
test('composing and already handled events are never intercepted',()=>{
    const f=fixture();assert.equal(f.fire('keydown',{isComposing:true}).stopped,false);
    assert.equal(f.fire('keydown',{defaultPrevented:true}).stopped,false);assert.deepEqual(f.calls,[]);f.router.dispose();
});
test('unrelated roots and unregistered scopes cannot trigger session commands',()=>{
    const f=fixture();for(const path of [[f.scope], [f.root], [{getAttribute:()=> 'foreign'},f.root]])
        assert.equal(f.fire('keydown',{composedPath:()=>path}).stopped,false);
    assert.deepEqual(f.calls,[]);f.router.dispose();
});
test('text inputs, password fields, editable content and dialogs preserve their input',()=>{
    const f=fixture();for(const node of [{tagName:'INPUT'},{tagName:'TEXTAREA'},{isContentEditable:true},{getAttribute:()=> 'dialog'}])
        assert.equal(f.fire('keydown',{composedPath:()=>[node,f.scope,f.root]}).stopped,false);
    assert.deepEqual(f.calls,[]);f.router.dispose();
});
test('owned repeats and release follow the gesture outside its original focus and modifiers',()=>{
    const f=fixture();f.fire('keydown');
    for(let i=0;i<10000;i++)assert.ok(f.fire('keydown',{ctrlKey:false,altKey:false,repeat:true,composedPath:()=>[]}).stopped);
    assert.ok(f.fire('keyup',{ctrlKey:false,altKey:false,composedPath:()=>[]}).stopped);
    assert.equal(f.fire('keyup').stopped,false);assert.deepEqual(f.calls,[1]);f.router.dispose();
});
test('Home and Pause ownership releases independently',()=>{
    const f=fixture();f.fire('keydown');f.fire('keydown',{code:'Pause'});
    assert.ok(f.fire('keyup').stopped);assert.ok(f.fire('keydown',{code:'Pause',repeat:true}).stopped);
    assert.ok(f.fire('keyup',{code:'Pause'}).stopped);assert.deepEqual(f.calls,[1,2]);f.router.dispose();
});
test('blur and pagehide clear ownership without invoking or replaying keys',()=>{
    const f=fixture();for(const type of ['blur','pagehide']){
        f.fire('keydown');f.fire(type);assert.equal(f.fire('keyup').stopped,false);
    }assert.deepEqual(f.calls,[1,1]);f.router.dispose();
});
test('each session is routed to its own registered action',()=>{
    const f=fixture();f.registrations.set('second',n=>f.calls.push(n+10));
    f.fire('keydown',{composedPath:()=>[{getAttribute:()=> 'second'},f.root]});
    f.fire('keyup');f.fire('keydown');f.fire('keyup');assert.deepEqual(f.calls,[11,1]);f.router.dispose();
});
test('removing an owner forgets only that owner and cannot dispatch stale actions',()=>{
    const f=fixture();f.fire('keydown');f.registrations.delete('first');f.router.forget('first');
    assert.equal(f.fire('keyup').stopped,false);assert.equal(f.fire('keydown').stopped,false);f.router.dispose();
});
test('disposal removes every hook and clears pending ownership',()=>{
    const f=fixture();f.fire('keydown');f.router.dispose();f.router.dispose();
    assert.ok([...f.listeners.values()].every(set=>set.size===0));assert.equal(f.fire('keyup').stopped,false);
});
test('a reentrant owner disposal does not leave an owned release or listeners',()=>{
    const f=fixture();f.registrations.set('first',()=>f.router.dispose());f.fire('keydown');
    assert.equal(f.fire('keyup').stopped,false);assert.ok([...f.listeners.values()].every(set=>set.size===0));
});
test('registration validates, stays bounded and removes global listeners after the last session',()=>{
    const f=fixture();f.router.dispose();const old={document:globalThis.document,addEventListener:globalThis.addEventListener,removeEventListener:globalThis.removeEventListener};
    Object.assign(globalThis,{document:{getElementById:()=>f.root},addEventListener:f.host.addEventListener,removeEventListener:f.host.removeEventListener});
    try {
        assert.throws(()=>api.register('',()=>{}),/Invalid/);assert.throws(()=>api.register('a',null),/Invalid/);
        for(let i=0;i<32;i++)api.register('owner'+i,()=>{});
        assert.throws(()=>api.register('owner0',()=>{}),/already/);assert.throws(()=>api.register('extra',()=>{}),/limit/);
        for(let i=0;i<32;i++)api.unregister('owner'+i);
        assert.ok([...f.listeners.values()].every(set=>set.size===0));api.unregister('missing');
        api.register('again',()=>{});api.unregister('again');
    } finally {Object.assign(globalThis,old);}
});
