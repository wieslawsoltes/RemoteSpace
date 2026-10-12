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
        const event = { type, target, key, stopped:false, defaultPrevented:false,
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
    return { context,listeners,focus,key,flush,microtasks,delayed,document,element,pending,count,dispatch };
}
test('owned navigation targets one registered control and does not re-enter native keyboard routing', () => {
    const s=setup(), commands=[];
    s.context.register('bar',true,key=>commands.push(key));s.focus('bar');
    const down=s.key('bar'),up=s.dispatch('keyup',s.element('bar'));
    assert.deepEqual(commands,[35]);assert.equal(down.stopped,true);assert.equal(up.stopped,true);
    assert.equal(down.defaultPrevented,true);assert.equal(up.defaultPrevented,true);assert.equal(s.pending(),0);
});
test('focus-only native notifications never schedule another native focus request', () => {
    const s=setup();let calls=0;
    s.context.register('a',true,()=>calls++);s.context.register('b',false,()=>calls++);
    for(let i=0;i<10000;i++){s.focus('a');s.flush();s.focus('b');s.flush();}
    assert.equal(calls,0);assert.equal(s.pending(),0);
});
test('late native focus does not redirect already dispatched navigation to the other axis',()=>{
    const s=setup(), commands=[];let nativeFocus='b';
    for(const id of ['a','b'])s.context.register(id,id==='a',key=>{
        // Model native focus replies and command application on distinct turns.
        // The target belongs to the registered callback, never global focus.
        s.delayed.push(()=>{nativeFocus=id;s.focus(id);});
        s.microtasks.push(()=>commands.push([id,key]));
    });
    for(let i=0;i<1000;i++){
        const id=i%2?'b':'a';s.focus(id);const event=s.key(id);
        if(!event.stopped)commands.push([nativeFocus,35]);
        // An old native reply may arrive before the new action is consumed.
        while(s.delayed.length)s.delayed.shift()();s.flush();
        assert.deepEqual(commands.at(-1),[id,35]);
    }
    assert.equal(commands.length,1000);assert.equal(s.pending(),0);
});
test('asynchronous native focus replies cannot sustain a cross-scrollbar echo loop',()=>{
    const s=setup();let calls=0;
    for(const id of ['a','b'])s.context.register(id,id==='a',()=>{calls++;s.delayed.push(()=>s.focus(id));});
    s.focus('a');s.key('a');s.focus('b');s.key('b');
    for(let i=0;i<20&&s.delayed.length;i++){s.delayed.shift()();s.flush();}
    assert.equal(calls,2);assert.equal(s.pending(),0);
});
test('recursive command dispatch is guarded',()=>{
    const s=setup();let calls=0;
    s.context.register('a',true,()=>{calls++;s.key('a');});s.focus('a');s.key('a');assert.equal(calls,1);
});
test('callback failure releases reentrancy guard and consumes the failed local navigation',()=>{
    const s=setup();let calls=0;
    s.context.register('a',true,()=>{calls++;throw Error('fixture failure');});s.focus('a');
    assert.throws(()=>s.key('a'),/fixture/);assert.throws(()=>s.key('a'),/fixture/);assert.equal(calls,2);
});
test('unregistered controls and off-focus events cannot navigate an owned scrollbar',()=>{
    const s=setup();let calls=0;s.context.register('a',true,()=>calls++);
    s.focus('fit','button');assert.equal(s.key('a').stopped,false);assert.equal(s.key('fit','button').stopped,false);
    s.focus('unknown');s.key('unknown');assert.equal(calls,0);
});
test('toolbar, textbox and remote pane keys retain their original routing',()=>{
    const s=setup();let calls=0;s.context.register('a',true,()=>calls++);
    for(const role of ['button','textbox','pane'])for(const key of ['Enter','a','Home','End']){
        s.focus('a',role);const event=s.key('a',role,key);assert.equal(event.stopped,false);assert.equal(event.defaultPrevented,false);
    }
    assert.equal(calls,0);
});
test('registration count stays bounded and disposal removes both listeners',()=>{
    const s=setup();for(let i=0;i<64;i++)s.context.register('s'+i,true,()=>{});
    assert.throws(()=>s.context.register('excess',false,()=>{}),/limit/);
    assert.throws(()=>s.context.register('s0',true,()=>{}),/Duplicate/);
    for(let i=0;i<64;i++)s.context.unregister('s'+i);
    assert.equal(s.count(),0);s.context.unregister('s0');
    s.context.register('again',false,()=>{});assert.equal(s.count(),2);s.context.unregister('again');assert.equal(s.count(),0);
});
test('removed registrations never receive late focus or keyboard callbacks',()=>{
    const s=setup();let a=0,b=0;s.context.register('a',true,()=>a++);s.context.register('b',false,()=>b++);
    s.context.unregister('a');s.focus('a');s.key('a');s.focus('b');s.key('b');assert.equal(a,0);assert.equal(b,1);
});
test('disposed bindings retain no queued references after ID reuse',()=>{
    const s=setup();let old=0,current=0;
    s.context.register('bar',true,()=>old++);s.focus('bar');s.context.unregister('bar');
    s.context.register('bar',true,()=>current++);s.flush();assert.equal(old,0);assert.equal(current,0);
    s.focus('bar');s.key('bar');assert.equal(current,1);assert.equal(s.pending(),0);
});
test('axis-specific arrows and common navigation have the correct native virtual-key codes',()=>{
    for(const horizontal of [true,false]){
        const s=setup(),codes=[];s.context.register('bar',horizontal,key=>codes.push(key));s.focus('bar');
        const allowed=new Map([['Home',36],['End',35],['PageUp',33],['PageDown',34],
            ...(horizontal?[['ArrowLeft',37],['ArrowRight',39]]:[['ArrowUp',38],['ArrowDown',40]])]);
        for(const [key,code] of allowed){const event=s.key('bar','scrollbar',key);assert.equal(event.stopped,true);assert.equal(codes.at(-1),code);}
        const count=codes.length;
        for(const key of [...(horizontal?['ArrowUp','ArrowDown']:['ArrowLeft','ArrowRight']),'Tab','Escape','a','Enter',' '])
            assert.equal(s.key('bar','scrollbar',key).stopped,false);
        assert.equal(codes.length,count);
    }
});
test('modifier shortcuts and already-handled events remain untouched',()=>{
    const s=setup();let calls=0;s.context.register('bar',true,()=>calls++);s.focus('bar');
    for(const option of ['ctrlKey','altKey','metaKey','defaultPrevented']){
        const event={type:'keydown',target:s.element('bar'),key:'Home',[option]:true,
            preventDefault(){throw Error('Should not prevent');},stopPropagation(){throw Error('Should not stop');}};
        for(const callback of s.listeners.get('keydown'))callback(event);
    }
    assert.equal(calls,0);
});
test('invalid registrations are rejected before adding listeners',()=>{
    const s=setup();
    for(const args of [['',true,()=>{}],['bar',null,()=>{}],['bar',true,null]])assert.throws(()=>s.context.register(...args),/Invalid/);
    assert.equal(s.count(),0);
});
