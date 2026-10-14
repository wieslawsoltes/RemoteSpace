"""Exact session-shortcut module and real browser keyboard/focus/fullscreen events."""
import asyncio
import json
import os
from pathlib import Path
import sys
from playwright.async_api import async_playwright, expect

async def run(output):
    output.mkdir(parents=True, exist_ok=True)
    source = (Path(__file__).parents[2] / 'src/RemoteSpace.Controls/SessionControlKeys.cs').read_text()
    source = source.split('private const string Source = """\n', 1)[1].split('\n""";', 1)[0]
    checks, errors = [], []
    async with async_playwright() as p:
        options = {'headless': True}
        if os.environ.get('CHROMIUM'): options['executable_path'] = os.environ['CHROMIUM']
        browser = await p.chromium.launch(**options)
        page = await browser.new_page()
        page.on('pageerror', lambda error: errors.append(str(error)))
        try:
            await page.set_content('''<!doctype html><div id="uno-semantics-root">
                <div tabindex="0" id="surface" xamlautomationid="session">Desktop scope <input id="editor"></div>
                <button id="local">Local commands</button></div>''')
            await page.add_script_tag(type='module', content=source+'''
                globalThis.commands=[];globalThis.ordinary=0;globalThis.result=null;
                globalThis.removeSession=()=>unregister('session');
                register('session',command=>{
                    commands.push(command);
                    if(command===1) document.querySelector('#local').focus();
                    else {
                        result=null;
                        const request=document.fullscreenElement ? document.exitFullscreen() : document.documentElement.requestFullscreen();
                        request.then(()=>result=Boolean(document.fullscreenElement),error=>result={error:error.message});
                    }
                });
                document.addEventListener('keydown',event=>{if(event.code==='Home'||event.code==='Pause') ordinary++;});
                globalThis.ready=true;
            ''')
            await page.wait_for_function('globalThis.ready === true')
            surface = page.locator('#surface')
            for _ in range(2):
                await surface.focus(); await page.keyboard.press('Control+Alt+Pause')
                await page.wait_for_function('result === true && document.fullscreenElement === document.documentElement')
                await surface.focus(); await page.keyboard.press('Control+Alt+Pause')
                await page.wait_for_function('result === false && document.fullscreenElement === null')
            assert await page.evaluate('commands') == [2,2,2,2]
            assert await page.evaluate('ordinary') == 0
            checks.append('Real Ctrl+Alt+Pause enters and exits actual fullscreen twice without native command-key bubbling')
            await surface.focus()
            await page.keyboard.down('Control'); await page.keyboard.down('Alt'); await page.keyboard.down('Home')
            await expect(page.locator('#local')).to_be_focused()
            await page.keyboard.up('Control'); await page.keyboard.up('Alt')
            for _ in range(10): await page.keyboard.down('Home')
            await page.keyboard.up('Home')
            assert await page.evaluate('commands') == [2,2,2,2,1]
            assert await page.evaluate('ordinary') == 0
            checks.append('Held Home remains one local command after focus transfer and early modifier release')
            await surface.focus(); await page.keyboard.press('Home')
            await page.locator('#editor').focus(); await page.keyboard.press('Control+Alt+Home')
            assert await page.evaluate('commands.length') == 5 and await page.evaluate('ordinary') == 2
            checks.append('Ordinary Home navigation and modified keys in a real input field are not intercepted')
            await surface.focus(); await page.evaluate('removeSession()'); await page.keyboard.press('Control+Alt+Home')
            assert await page.evaluate('commands.length') == 5 and await page.evaluate('ordinary') == 3
            checks.append('Unregistering the session removes its real key interception')
            assert not errors, errors
            for check in checks: print('PASS '+check, flush=True)
        finally:
            (output / 'checks.json').write_text(json.dumps(checks, indent=2))
            (output / 'errors.json').write_text(json.dumps(errors, indent=2))
            await browser.close()

if __name__ == '__main__': asyncio.run(run(Path(sys.argv[1])))
