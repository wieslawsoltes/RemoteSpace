import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const source = readFileSync(new URL('../../src/RemoteSpace.Controls/WindowFullscreenController.cs', import.meta.url), 'utf8')
    .replaceAll('\r\n','\n').split('private const string Source = """\n')[1].split('\n""";')[0];
const api = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
const {createController} = api;
function fixture() {
    const listeners = new Set(), changes = [], calls = [];
    let enter = () => { doc.fullscreenElement = doc.documentElement; notify(); return Promise.resolve(); };
    let exit = () => { doc.fullscreenElement = null; notify(); return Promise.resolve(); };
    const doc = { fullscreenEnabled:true, fullscreenElement:null,
        addEventListener(type, f) { assert.equal(type,'fullscreenchange'); listeners.add(f); },
        removeEventListener(type, f) { assert.equal(type,'fullscreenchange'); listeners.delete(f); },
        exitFullscreen() {calls.push('exit');return exit();},
        documentElement: { requestFullscreen(options) {assert.deepEqual(options,{navigationUI:'hide'});calls.push('enter');return enter();} } };
    const notify = () => { for (const listener of listeners) listener(); };
    const control = createController(doc, value => changes.push(value));
    return {doc, control, listeners, changes, calls, notify, setEnter:f=>enter=f, setExit:f=>exit=f};
}
function deferred() {let resolve,reject;const promise=new Promise((a,b)=>{resolve=a;reject=b;});return {promise,resolve,reject};}
test('entry invokes native request on the activation stack and publishes actual state',async()=>{
    const f=fixture();const result=f.control.set(true);assert.deepEqual(f.calls,['enter']);
    assert.equal(await result,true);assert.deepEqual(f.changes,[true]);await f.control.dispose();
});
test('exit follows platform completion and notifies once per state',async()=>{
    const f=fixture();await f.control.set(true);f.notify();f.notify();await f.control.set(false);
    assert.deepEqual(f.changes,[true,false]);assert.deepEqual(f.calls,['enter','exit']);await f.control.dispose();
});
test('Escape or host exit updates state without a timer or request',async()=>{
    const f=fixture();await f.control.set(true);f.doc.fullscreenElement=null;f.notify();
    assert.deepEqual(f.changes,[true,false]);await f.control.dispose();assert.deepEqual(f.calls,['enter']);
});
test('asynchronous permission denial is returned without optimistic fullscreen',async()=>{
    const f=fixture();f.setEnter(()=>Promise.reject(Error('denied')));await assert.rejects(f.control.set(true),/denied/);
    assert.deepEqual(f.changes,[]);await f.control.dispose();assert.equal(f.listeners.size,0);
});
test('synchronous host failure releases admission for a later command',async()=>{
    const f=fixture();f.setEnter(()=>{throw Error('host');});await assert.rejects(f.control.set(true),/host/);
    assert.equal(await f.control.set(false),false);await f.control.dispose();
});
test('successfully resolved request with unchanged host state is not reported as fullscreen',async()=>{
    const f=fixture();f.setEnter(()=>Promise.resolve());await assert.rejects(f.control.set(true),/did not apply/);
    assert.deepEqual(f.changes,[]);await f.control.dispose();
});
test('pending commands are bounded to one rather than queued',async()=>{
    const f=fixture(),d=deferred();f.setEnter(()=>d.promise);const pending=f.control.set(true);
    for(let i=0;i<1000;i++)await assert.rejects(f.control.set(true),/pending/);
    assert.equal(f.calls.length,1);f.doc.fullscreenElement=f.doc.documentElement;f.notify();d.resolve();await pending;await f.control.dispose();
});
test('disposal drains an outstanding entry and exits owned fullscreen without late notifications',async()=>{
    const f=fixture(),d=deferred();f.setEnter(()=>d.promise);const pending=f.control.set(true);const disposing=f.control.dispose();
    assert.equal(f.control.dispose(),disposing);assert.equal(f.listeners.size,0);
    f.doc.fullscreenElement=f.doc.documentElement;f.notify();d.resolve();await pending;await disposing;
    assert.equal(f.doc.fullscreenElement,null);assert.deepEqual(f.changes,[]);assert.deepEqual(f.calls,['enter','exit']);
});
test('failed entry and dispose do not leave listeners or an unobserved rejection',async()=>{
    const f=fixture(),d=deferred();f.setEnter(()=>d.promise);const pending=f.control.set(true);const dispose=f.control.dispose();
    d.reject(Error('denied'));await assert.rejects(pending,/denied/);await dispose;assert.equal(f.listeners.size,0);
});
test('disposal does not exit another element or pre-existing unowned fullscreen',async()=>{
    for(const other of [true,false]){
        const f=fixture();f.doc.fullscreenElement=other?{}:f.doc.documentElement;f.notify();await f.control.dispose();
        assert.deepEqual(f.calls,[]);
    }
});
test('unavailable or foreign fullscreen rejects entry and leaves its owner alone',async()=>{
    const f=fixture();f.doc.fullscreenEnabled=false;await assert.rejects(f.control.set(true),/unavailable/);
    f.doc.fullscreenEnabled=true;f.doc.fullscreenElement={};await assert.rejects(f.control.set(true),/unavailable/);
    assert.equal(await f.control.set(false),false);assert.deepEqual(f.calls,[]);await f.control.dispose();
});
test('disposed or invalid requests cannot invoke platform APIs',async()=>{
    const f=fixture();await assert.rejects(f.control.set(1),/Invalid/);await f.control.dispose();
    await assert.rejects(f.control.set(true),/disposed/);assert.deepEqual(f.calls,[]);
});
test('idle repeated confirmations allocate no callback work and repeated set does not request platform changes',async()=>{
    const f=fixture();await f.control.set(true);
    for(let i=0;i<10000;i++)f.notify();for(let i=0;i<10;i++)await f.control.set(true);
    assert.deepEqual(f.changes,[true]);assert.deepEqual(f.calls,['enter']);await f.control.dispose();
});
test('document registration is single-owner and does not release ownership before disposal completes',async()=>{
    const f=fixture(),d=deferred();globalThis.document=f.doc;
    try {
        api.register('first',()=>{});assert.throws(()=>api.register('second',()=>{}),/already/);
        f.setEnter(()=>d.promise);const request=api.set('first',true);
        const disposal=api.unregister('first');const repeated=api.unregister('first');
        assert.throws(()=>api.register('second',()=>{}),/already/);
        f.doc.fullscreenElement=f.doc.documentElement;d.resolve();await request;await disposal;await repeated;
        api.register('second',()=>{});await api.unregister('second');await api.unregister('missing');
        await assert.rejects(api.set('missing',true),/not initialized/);
    } finally {delete globalThis.document;await f.control.dispose();}
});

test('a denied pending request never acquires unrelated document fullscreen',async()=>{
    const f=fixture(),d=deferred();f.setEnter(()=>d.promise);
    const request=f.control.set(true);
    // Another host enters document fullscreen while our own request is denied.
    f.doc.fullscreenElement=f.doc.documentElement;f.notify();
    d.reject(Error('denied'));
    await assert.rejects(request,/denied/);await f.control.dispose();
    assert.equal(f.doc.fullscreenElement,f.doc.documentElement);
    assert.deepEqual(f.calls,['enter']);
});
test('no-op repeated initialization requests do not claim pre-existing fullscreen ownership',async()=>{
    const f=fixture();f.doc.fullscreenElement=f.doc.documentElement;f.notify();
    await f.control.set(true);await f.control.dispose();
    assert.equal(f.doc.fullscreenElement,f.doc.documentElement);assert.deepEqual(f.calls,[]);
});

test('terminal exit drains a pending entry and coalesces repeated close requests',async()=>{
    const f=fixture(),d=deferred();f.setEnter(()=>d.promise);
    const entering=f.control.set(true),closing=f.control.exit();
    for(let i=0;i<1000;i++)assert.equal(f.control.exit(),closing);
    await assert.rejects(f.control.set(true),/pending/);
    assert.deepEqual(f.calls,['enter']);
    f.doc.fullscreenElement=f.doc.documentElement;f.notify();d.resolve();
    await entering;assert.equal(await closing,false);
    assert.deepEqual(f.calls,['enter','exit']);assert.equal(f.doc.fullscreenElement,null);await f.control.dispose();
});
test('terminal exit after denied entry does not fail cleanup or request a redundant exit',async()=>{
    const f=fixture(),d=deferred();f.setEnter(()=>d.promise);
    const entering=f.control.set(true),closing=f.control.exit();d.reject(Error('denied'));
    await assert.rejects(entering,/denied/);assert.equal(await closing,false);
    assert.deepEqual(f.calls,['enter']);await f.control.dispose();
});
test('terminal exit shares an already pending successful exit',async()=>{
    const f=fixture(),d=deferred();await f.control.set(true);f.setExit(()=>d.promise);
    const request=f.control.set(false),closing=f.control.exit();
    f.doc.fullscreenElement=null;f.notify();d.resolve();await request;await closing;
    assert.deepEqual(f.calls,['enter','exit']);await f.control.dispose();
});
test('terminal exit completion stays pending until the platform confirms exit',async()=>{
    const f=fixture(),d=deferred();await f.control.set(true);f.setExit(()=>d.promise);
    let done=false;const closing=f.control.exit().then(v=>{done=true;return v;});
    await new Promise(resolve=>setImmediate(resolve));assert.equal(done,false);
    await assert.rejects(f.control.set(true),/pending/);
    f.doc.fullscreenElement=null;f.notify();d.resolve();assert.equal(await closing,false);await f.control.dispose();
});
test('failed terminal exit remains observable and does not loop until it succeeds',async()=>{
    const f=fixture();await f.control.set(true);f.setExit(()=>Promise.reject(Error('host exit failed')));
    await assert.rejects(f.control.exit(),/host exit failed/);assert.deepEqual(f.calls,['enter','exit']);
    f.doc.fullscreenElement=null;f.notify();await f.control.dispose();
});
test('disposing while terminal exit awaits entry cannot recreate fullscreen or leak a listener',async()=>{
    const f=fixture(),d=deferred();f.setEnter(()=>d.promise);
    const entry=f.control.set(true),closing=f.control.exit(),disposal=f.control.dispose();
    f.doc.fullscreenElement=f.doc.documentElement;d.resolve();await entry;await closing;await disposal;
    assert.equal(f.listeners.size,0);assert.deepEqual(f.calls,['enter','exit']);assert.equal(f.doc.fullscreenElement,null);
    await assert.rejects(f.control.exit(),/disposed/);
});
test('completed terminal exit does not reserve the controller for subsequent sessions',async()=>{
    const f=fixture();for(let i=0;i<100;i++){await f.control.set(true);await f.control.exit();}
    assert.equal(f.calls.length,200);assert.equal(f.changes.length,200);await f.control.dispose();
});
test('registered terminal exit drains its owner and rejects a missing registration',async()=>{
    const f=fixture();globalThis.document=f.doc;
    try {api.register('closing',()=>{});await api.set('closing',true);assert.equal(await api.exit('closing'),false);await api.unregister('closing');await assert.rejects(api.exit('missing'),/not initialized/);}
    finally {delete globalThis.document;await f.control.dispose();}
});
