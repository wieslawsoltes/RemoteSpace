import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';

// Run the exact interop module in the controls library. No copied product logic.
const source = readFileSync(new URL('../../src/RemoteSpace.Controls/SystemFileAccess.cs', import.meta.url), 'utf8')
    .replaceAll('\r\n', '\n').split('private const string Source = """\n')[1].split('\n""";')[0].replaceAll('export function ', 'function ');
function service() {
    let next = 0, fault = null;
    const elements = [], readers = [], revoked = [], timers = new Map(), events = new Map();
    const counts = { reads:0, aborts:0 };
    function fail(stage) { if (fault === stage) throw Error('fixture ' + stage); }
    class Reader {
        readyState = 0; result = null; error = null;
        constructor() { fail('reader'); readers.push(this); }
        readAsArrayBuffer(blob) {
            fail('read'); this.readyState = 1;
            blob.arrayBuffer().then(buffer => {
                if (this.readyState !== 1) return;
                this.result = buffer; this.readyState = 2; this.onload?.();
            }, error => {
                if (this.readyState !== 1) return;
                this.error = error; this.readyState = 2; this.onerror?.();
            });
        }
        abort() { counts.aborts++; this.readyState = 2; this.result = null; this.onabort?.(); }
    }
    const context = vm.createContext({ Uint8Array, ArrayBuffer, Blob, FileReader: Reader,
        document: { body: { append: () => fail('append') }, createElement: tag => {
            fail('create');
            const e = { tag, style: {}, files: [], removed: false,
                click() { fail('click'); }, remove() { this.removed = true; fail('remove'); } };
            elements.push(e); return e;
        } },
        URL: { createObjectURL: () => { fail('url'); return 'blob:' + ++next; }, revokeObjectURL: value => revoked.push(value) },
        setTimeout: action => { fail('timer'); const id = ++next; timers.set(id, action); return id; }, clearTimeout: id => timers.delete(id),
        addEventListener: (event, action) => events.set(event, action),
    });
    vm.runInContext(source, context);
    const file = (name, bytes, options={}) => ({ name, size:options.reportedSize ?? bytes.length,
        arrayBuffer: async () => { counts.reads++; return options.promise ? await options.promise : Uint8Array.from(bytes).buffer; },
        slice(start, end) {
            options.onSlice?.(start,end);
            return { arrayBuffer: async () => {
                counts.reads++;
                const buffer = options.promise ? await options.promise : Uint8Array.from(bytes).buffer;
                return buffer.slice(start,end);
            } };
        }
    });
    const expire = () => { for (const [id, action] of [...timers]) { timers.delete(id); action(); } };
    const hide = () => events.get('pagehide')();
    const cancelNext = async () => {
        const operation=context.pick('.json',10); elements.at(-1).oncancel();
        assert.equal((await operation).cancelled,true);
    };
    return { context,elements,readers,revoked,timers,events,counts,file,expire,hide,cancelNext,fail: stage => fault=stage };
}
function deferred() { let resolve,reject; const promise=new Promise((a,b)=>{resolve=a;reject=b;}); return {promise,resolve,reject}; }

test('bounded typed bytes returned intact and every resource cleaned', async () => {
    const s=service(), chosen=s.context.pick('.rdp',4), input=s.elements.at(-1);
    input.files=[s.file('PC.RDP',[255,0,128,7])]; await input.onchange(); const result=await chosen;
    assert.equal(result.cancelled,false);assert.equal(result.name,'PC.RDP');assert.deepEqual([...result.bytes],[255,0,128,7]);
    assert.equal(s.counts.reads,1);assert.equal(input.removed,true);assert.equal(s.timers.size,0);
});
test('cancel completes and frees single-picker guard', async () => {
    const s=service(); await s.cancelNext(); await s.cancelNext();
});
test('metadata rejects oversized file before reading its content', async () => {
    const s=service(), picked=s.context.pick('.json',3); s.elements.at(-1).files=[s.file('data.json',[1,2,3,4])];
    await s.elements.at(-1).onchange();await assert.rejects(picked,/byte limit/);assert.equal(s.counts.reads,0);
});
test('second bound rejects files with understated metadata', async () => {
    const s=service(), picked=s.context.pick('.json',3);
    s.elements.at(-1).files=[s.file('data.json',[1,2,3,4],{reportedSize:1})];
    await s.elements.at(-1).onchange();await assert.rejects(picked,/byte limit/);
});
test('concurrent pick, wrong extension and invalid limits fail', async () => {
    const s=service();assert.throws(()=>s.context.pick('json',3),/limits/);
    const picked=s.context.pick('.json',3);assert.throws(()=>s.context.pick('.json',3),/already open/);
    s.elements.at(-1).files=[s.file('data.exe',[1])];await s.elements.at(-1).onchange();await assert.rejects(picked,/wrong extension/);
});
for (const stage of ['create','append','click','timer']) test(`${stage} failure cleans picker state and permits the next import`,async()=>{
    const s=service();s.fail(stage);await assert.rejects(s.context.pick('.rdp',10),new RegExp(stage));
    assert.equal(s.timers.size,0);s.fail(null);await s.cancelNext();
});
for (const end of ['cancel','timeout','pagehide']) test(`${end} revokes queued change handlers before they can read a file`,async()=>{
    const s=service(), picked=s.context.pick('.json',4), input=s.elements.at(-1), lateChange=input.onchange;
    const outcome=picked.then(v=>v,e=>({error:e}));
    if(end==='cancel')input.oncancel();else if(end==='timeout')s.expire();else s.hide();
    const result=await outcome;if(end==='timeout')assert.match(result.error.message,/timed out/);else assert.equal(result.cancelled,true);
    input.files=[s.file('late.json',[1,2,3,4])];await lateChange();
    assert.equal(s.counts.reads,0);assert.equal(input.onchange,null);assert.equal(input.oncancel,null);
    assert.equal(input.removed,true);assert.equal(s.timers.size,0);await s.cancelNext();
});
for (const end of ['cancel','timeout','pagehide']) test(`${end} aborts a pending native read and stale completion cannot unlock a newer picker`,async()=>{
    const s=service(), read=deferred(), picked=s.context.pick('.json',4), input=s.elements.at(-1);
    const outcome=picked.then(v=>v,e=>({error:e}));
    input.files=[s.file('pending.json',[1],{promise:read.promise})]; input.onchange();
    if(end==='cancel')input.oncancel();else if(end==='timeout')s.expire();else s.hide();
    await outcome; assert.equal(s.counts.aborts,1);
    const newer=s.context.pick('.json',4), next=s.elements.at(-1);
    read.resolve(new Uint8Array([1,2,3,4]).buffer);await Promise.resolve();await Promise.resolve();
    assert.throws(()=>s.context.pick('.json',4),/already open/);
    next.oncancel();assert.equal((await newer).cancelled,true);assert.equal(s.timers.size,0);
});
test('repeated change notifications start only one native read',async()=>{
    const s=service(), read=deferred(), picked=s.context.pick('.json',4), input=s.elements.at(-1);
    input.files=[s.file('once.json',[1],{promise:read.promise})];const changed=input.onchange;
    for(let i=0;i<10000;i++)changed();
    assert.equal(s.counts.reads,1);assert.equal(s.readers.length,1);
    read.resolve(new Uint8Array([1,2,3,4]).buffer);assert.equal((await picked).bytes.length,4);
});
test('native read is restricted to the configured range plus one overflow byte',async()=>{
    const s=service(), picked=s.context.pick('.json',3), ranges=[];
    s.elements.at(-1).files=[s.file('bounded.json',new Uint8Array(10000),{reportedSize:1,onSlice:(a,b)=>ranges.push([a,b])})];
    s.elements.at(-1).onchange();await assert.rejects(picked,/byte limit/);assert.deepEqual(ranges,[[0,4]]);
});
for(const stage of ['reader','read']) test(`${stage} failure releases pending selection`,async()=>{
    const s=service(), picked=s.context.pick('.json',4);s.fail(stage);s.elements.at(-1).files=[s.file('bad.json',[1])];
    s.elements.at(-1).onchange();await assert.rejects(picked,new RegExp(stage));s.fail(null);await s.cancelNext();
});
test('read error is reported and native abort cancels rather than succeeds',async()=>{
    const s=service(), read=deferred(), picked=s.context.pick('.json',4);
    s.elements.at(-1).files=[s.file('read.json',[1],{promise:read.promise})];s.elements.at(-1).onchange();
    read.reject(Error('fixture unreadable'));await assert.rejects(picked,/read|unreadable/);
    const next=s.context.pick('.json',4), pending=deferred();
    s.elements.at(-1).files=[s.file('abort.json',[1],{promise:pending.promise})];s.elements.at(-1).onchange();
    s.readers.at(-1).abort();assert.equal((await next).cancelled,true);assert.equal(s.timers.size,0);
});
test('cleanup exception cannot strand import or override the original error',async()=>{
    const s=service(), picked=s.context.pick('.json',4), input=s.elements.at(-1);
    s.fail('remove');input.files=[s.file('wrong.exe',[1])];input.onchange();await assert.rejects(picked,/wrong extension/);
    assert.equal(s.timers.size,0);s.fail(null);await s.cancelNext();
});
test('downloads have bounded retained URLs and pagehide clears timers too',()=>{
    const s=service();for(let i=0;i<4;i++)s.context.download('file.json',new Uint8Array([1,2,3]),'application/json');
    assert.throws(()=>s.context.download('file.json',new Uint8Array([4]),'application/json'),/Previous downloads/);
    assert.ok(s.elements.every(e=>e.removed));s.hide();assert.equal(s.revoked.length,4);assert.equal(s.timers.size,0);
    s.context.download('next.json',new Uint8Array(),'application/json');s.expire();assert.equal(s.revoked.length,5);assert.equal(s.timers.size,0);
});
for(const stage of ['create','append','click','remove','timer']) test(`${stage} download failure revokes its URL and retained byte reservation`,()=>{
    const s=service();s.fail(stage);assert.throws(()=>s.context.download('file.json',new Uint8Array([1]),'application/json'),new RegExp(stage));
    assert.equal(s.revoked.length,1);assert.equal(s.timers.size,0);s.fail(null);
    for(let i=0;i<4;i++)s.context.download('ok.json',new Uint8Array([1]),'application/json');s.hide();
});
test('repeated lifecycle exits retain no file nodes, readers, URLs or timers',async()=>{
    const s=service();
    for(let i=0;i<1000;i++){
        const picked=s.context.pick('.json',0);s.context.download('empty.json',new Uint8Array(),'application/json');
        s.hide();assert.equal((await picked).cancelled,true);assert.equal(s.timers.size,0);
    }
    assert.equal(s.counts.reads,0);assert.equal(s.revoked.length,1000);assert.ok(s.elements.every(e=>e.removed));
});
