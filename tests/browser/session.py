"""Real browser -> authenticated bridge -> authorized loopback XRDP fixture.
No fake surfaces, runtime test mode, private .NET API calls, or stored credentials.
"""
import asyncio
import functools
import http.server
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import threading
import tempfile
import time
import traceback
import urllib.request
from playwright.async_api import async_playwright, expect
from driver import boot, command, edit, navigate
from clipboard import round_trip
from graphics_output import validate_output_controls
from session_display import validate_session_display
from input_events import validate_extended_input

async def run(site, output):
    if os.environ.get('RDP_TEST_HOST') != '127.0.0.1':
        raise RuntimeError('This test is restricted to its explicitly configured loopback fixture.')
    password, pin = os.environ['RDP_TEST_PASSWORD'], os.environ['RDP_TEST_PIN']
    token = secrets.token_urlsafe(48)
    output.mkdir(parents=True, exist_ok=True)
    webroot = tempfile.TemporaryDirectory(prefix='remotespace-pages-')
    (Path(webroot.name)/'RemoteSpace').symlink_to(site.resolve(), target_is_directory=True)
    server = http.server.ThreadingHTTPServer(('127.0.0.1',0), functools.partial(http.server.SimpleHTTPRequestHandler,directory=webroot.name))
    threading.Thread(target=server.serve_forever,daemon=True).start()
    origin = f'http://127.0.0.1:{server.server_port}'
    endpoint = 'ws://127.0.0.1:5088/session'
    env = dict(os.environ, REMOTESPACE_TOKEN=token, REMOTESPACE_ORIGINS=origin, REMOTESPACE_TARGETS='127.0.0.1:3389', ASPNETCORE_URLS='http://127.0.0.1:5088')
    bridge_log = (output/'bridge.log').open('w')
    bridge = subprocess.Popen(['dotnet','apps/RemoteSpace.Bridge/bin/Release/net10.0/RemoteSpace.Bridge.dll'],env=env,stdout=bridge_log,stderr=subprocess.STDOUT)
    messages, errors, checks = [], [], []
    def clean(text):
        for secret in (password,token): text=text.replace(secret,'[redacted]')
        return text
    def passed(name): checks.append(name); print('PASS',name,flush=True)
    deadline=time.monotonic()+30
    try:
        while True:
            if bridge.poll() is not None: raise RuntimeError('The local bridge exited during startup.')
            try:
                with urllib.request.urlopen('http://127.0.0.1:5088/health',timeout=1) as response:
                    if response.status == 200: break
            except OSError:
                if time.monotonic()>deadline: raise
                await asyncio.sleep(.1)
        async with async_playwright() as playwright:
            browser=await playwright.chromium.launch(args=['--enable-unsafe-swiftshader'])
            page=await browser.new_page(viewport={'width':1600,'height':1100})
            page.on('console',lambda m:messages.append(m.type+': '+m.text))
            page.on('pageerror',lambda e:errors.append(str(e)))
            active, resized = asyncio.Event(), asyncio.Event()
            metrics={'layout_requests':0,'binary_bytes':0,'surface_packets':0,'input_packets':0,'input_batches':0,'max_input_batch':0,'clipboard_packets':0,'clipboard_lengths':[],'sizes':[],'server_controls':[]}
            def received(payload):
                if isinstance(payload,bytes):
                    if payload.startswith(b'RSU1'):
                        metrics['surface_packets']+=1;metrics['binary_bytes']+=len(payload)
                    return
                msg=json.loads(payload)
                if msg.get('Type') in ('state','diagnostic','error','display','input-capabilities'):
                    metrics['server_controls'].append(msg)
                if msg.get('Type')=='state' and msg.get('State')==6: active.set()
                if msg.get('Type')=='size':
                    metrics['sizes'].append([msg.get('Width'),msg.get('Height')])
                    if metrics['sizes'][-1]==[1024,768]: resized.set()
            def sent(payload):
                if isinstance(payload,str):
                    message = json.loads(payload)
                    if message.get('Type')=='display-layout': metrics['layout_requests']+=1
                    if message.get('Type')=='input': metrics['input_packets']+=1
                    if message.get('Type')=='input-batch':
                        count=len(message.get('Inputs') or [])
                        metrics['input_packets']+=count;metrics['input_batches']+=1
                        metrics['max_input_batch']=max(metrics['max_input_batch'],count)
                    events = [message.get('Input')] if message.get('Type') == 'input' else message.get('Inputs') or []
                    for event in events:
                        if event and event.get('Kind') == 2 and event.get('Flags') == 0x0800:
                            metrics['last_pointer'] = {key:event[key] for key in ('X','Y')}
                    if message.get('Type')=='clipboard':
                        metrics['clipboard_packets']+=1
                        metrics['clipboard_lengths'].append(len(message.get('Text') or ''))
            def socket(ws): ws.on('framereceived',received);ws.on('framesent',sent)
            page.on('websocket',socket)
            async def rendered(name):
                until=time.monotonic()+20
                while True:
                    await page.screenshot(path=str(output/name))
                    verified=subprocess.run(['python3','tests/browser/assert_pixels.py',str(output/name)],capture_output=True,text=True)
                    if verified.returncode == 0:
                        print(verified.stdout.strip(),flush=True);return
                    if time.monotonic()>until:
                        raise AssertionError('Remote pixels were received but not visibly presented: '+verified.stderr)
                    await asyncio.sleep(.25)
            try:
                await boot(page,origin+'/RemoteSpace/')
                for supplied, host in [(token+'invalid','127.0.0.1'),(token,'not-allowed.invalid')]:
                    code=await page.evaluate('''({endpoint,token,host}) => new Promise((resolve,reject) => {
                        const ws=new WebSocket(endpoint,'remotespace.v1');
                        const timeout=setTimeout(()=>{ws.close();reject(new Error('Policy check timed out'));},10000);
                        ws.onopen=()=>ws.send(JSON.stringify({Type:'connect',Token:token,Options:{Host:host}}));
                        ws.onclose=e=>{clearTimeout(timeout);resolve(e.code);};
                        ws.onerror=()=>{};
                    })''',{'endpoint':endpoint,'token':supplied,'host':host})
                    if code!=1008: raise AssertionError(f'Expected policy close 1008, received {code}')
                passed('Browser bridge rejects invalid authentication and non-allowlisted targets')
                await navigate(page,'Settings')
                await edit(page,'Bridge endpoint',endpoint)
                await command(page,'Save bridge endpoint')
                await navigate(page,'Devices')
                await command(page,'Add your first PC')
                await edit(page,'Friendly name','Loopback desktop')
                await edit(page,'PC address','127.0.0.1')
                await edit(page,'User name',os.environ.get('RDP_TEST_USER','rdptest'))
                await edit(page,'Certificate SHA-256 pin (optional)',pin)
                await page.get_by_role('checkbox',name='Allow text clipboard redirection (opt-in)',exact=True).press('Space')
                security=page.get_by_role('combobox',name='Connection security',exact=True)
                # Change selection while closed. Enter in a ContentDialog can
                # submit its default Save command rather than open this peer.
                await security.press('Home');await security.press('ArrowDown');await security.press('Tab')
                await command(page,'Add')
                await expect(page.get_by_role('button',name='Connect to Loopback desktop',exact=True)).to_be_attached(timeout=30000)
                await command(page,'Connect to Loopback desktop')
                await edit(page,'Connection password',password)
                await edit(page,'Bridge access token',token)
                await command(page,'Connect')
                await asyncio.wait_for(active.wait(),60)
                until=time.monotonic()+30
                while metrics['binary_bytes']<100000:
                    if time.monotonic()>until: raise AssertionError('No real desktop surface data reached the browser.')
                    await asyncio.sleep(.1)
                await page.wait_for_timeout(1500)
                await rendered('01-live-desktop.png')
                passed('Skia visibly presents the real remote terminal and desktop background')
                # A socket can deliver bytes before the managed receiver faults.
                # Never count that as a usable remote desktop.
                await expect(page.get_by_role('tab',name='Loopback desktop',exact=True)).to_be_attached()
                failed = page.locator('[aria-label="Faulted"]')
                if await failed.count():
                    await navigate(page,'Settings')
                    await page.screenshot(path=str(output/'failure-diagnostics.png'))
                    raise AssertionError('The managed browser session faulted after receiving desktop packets. See diagnostics.')
                passed('Actual Uno client authenticates and receives real XRDP desktop pixels')
                await command(page,'Fit')
                await page.keyboard.type('touch browserproof',delay=50)
                await page.keyboard.press('Enter')
                until=time.monotonic()+15
                while subprocess.run(['sudo','test','-f','/home/rdptest/browserproof']).returncode:
                    if time.monotonic()>until: raise AssertionError('Browser keyboard input did not produce the independent server-side file.')
                    await asyncio.sleep(.2)
                if metrics['input_packets']<20: raise AssertionError('Expected browser keyboard packets were not observed.')
                passed('Browser keyboard input creates an independently checked file on the remote desktop')
                await page.keyboard.press('Pause')
                until=time.monotonic()+10
                while metrics['max_input_batch'] < 4:
                    if time.monotonic()>until: raise AssertionError('Pause key did not generate its ordered four-event batch.')
                    await asyncio.sleep(.1)
                passed('Actual browser Pause key uses negotiated ordered input batching')
                await validate_extended_input(page, metrics, passed)
                resolution=page.get_by_role('button',name='Remote resolution',exact=True)
                await expect(resolution).to_be_enabled(timeout=30000)
                await command(page,'Remote resolution')
                before=metrics['surface_packets']
                await page.get_by_role('menuitem',name='1024 × 768',exact=True).press('Enter')
                await asyncio.wait_for(resized.wait(),30)
                until=time.monotonic()+15
                while metrics['surface_packets']<=before:
                    if time.monotonic()>until: raise AssertionError('Resized desktop did not deliver new pixels.')
                    await asyncio.sleep(.1)
                await page.wait_for_timeout(1000)
                await rendered('02-resized-desktop.png')
                passed('Browser resolution command produces server-confirmed 1024x768 pixels')
                await round_trip(page, origin, passed)
                await validate_session_display(page, metrics, output, passed)
                await validate_output_controls(page, metrics, output, passed)
                # TabView's built-in close button is deliberately not a tab stop.
                # Its semantic HTML proxy cannot focus the native button on Enter;
                # exercise the actual canvas mouse hit instead.
                await page.get_by_role('button',name='Close Tab',exact=True).click(force=True)
                await command(page,'Disconnect')
                await expect(page.get_by_role('tab',name='Loopback desktop',exact=True)).to_have_count(0,timeout=10000)
                passed('Closing the active session drains input and removes its tab without hanging')
                active.clear()
                await navigate(page,'Devices')
                await command(page,'Connect to Loopback desktop')
                await edit(page,'Connection password',password)
                await edit(page,'Bridge access token',token)
                await command(page,'Connect')
                await asyncio.wait_for(active.wait(),60)
                await rendered('04-reconnected-desktop.png')
                passed('Saved profile reconnects through a fresh session after explicit credential entry')
                if errors: raise AssertionError('Uncaught browser errors: '+'\n'.join(errors))
                passed('No uncaught JavaScript or WASM session errors')
            except BaseException:
                (output/'failure.txt').write_text(clean(traceback.format_exc()))
                raise
            finally:
                (output/'final-dom.html').write_text(clean(await page.content()))
                (output/'metrics.json').write_text(clean(json.dumps(metrics,indent=2)))
                (output/'console.log').write_text(clean('\n'.join(messages)))
                (output/'checks.json').write_text(json.dumps(checks,indent=2))
                (output/'errors.json').write_text(clean(json.dumps(errors,indent=2)))
                await page.screenshot(path=str(output/'final.png'))
                try:
                    await navigate(page,'Settings')
                    (output/'diagnostics-dom.html').write_text(clean(await page.content()))
                    await page.screenshot(path=str(output/'diagnostics.png'))
                except Exception:
                    pass
                await browser.close()
    finally:
        bridge.terminate()
        try: bridge.wait(timeout=10)
        except subprocess.TimeoutExpired: bridge.kill();bridge.wait()
        bridge_log.close();server.shutdown();server.server_close();webroot.cleanup()

if __name__=='__main__':
    asyncio.run(run(Path(sys.argv[1]),Path(sys.argv[2])))
