"""Exact fullscreen module tested against real Chromium platform promises/events."""
import asyncio
import html
import json
import os
from pathlib import Path
import sys
from playwright.async_api import async_playwright, expect

async def run(output):
    output.mkdir(parents=True, exist_ok=True)
    source = (Path(__file__).parents[2]/'src/RemoteSpace.Controls/WindowFullscreenController.cs').read_text()
    source = source.split('private const string Source = """\n',1)[1].split('\n""";',1)[0]
    checks, errors = [], []
    async with async_playwright() as p:
        options = {'headless': True}
        if os.environ.get('CHROMIUM'): options['executable_path'] = os.environ['CHROMIUM']
        browser = await p.chromium.launch(**options)
        page = await browser.new_page()
        page.on('pageerror', lambda error: errors.append(str(error)))
        try:
            await page.set_content('<!doctype html><title>Fullscreen lifecycle</title><button id="enter">Enter</button><button id="exit">Exit</button><button id="dispose">Dispose</button>')
            script = source + '''
                globalThis.changes=[];
                globalThis.controller=createController(document, value=>changes.push(value));
                globalThis.result=null;
                document.querySelector('#enter').onclick=()=>controller.set(true).then(v=>result=v,e=>result={error:e.message});
                document.querySelector('#exit').onclick=()=>controller.set(false).then(v=>result=v,e=>result={error:e.message});
                document.querySelector('#dispose').onclick=()=>controller.dispose().then(v=>result=v,e=>result={error:e.message});
            '''
            await page.add_script_tag(type='module',content=script)
            await page.wait_for_function('globalThis.controller !== undefined')
            for _ in range(3):
                await page.click('#enter'); await page.wait_for_function('document.fullscreenElement === document.documentElement && result === true')
                await page.click('#exit'); await page.wait_for_function('document.fullscreenElement === null && result === false')
            assert await page.evaluate('changes') == [True,False]*3
            checks.append('Three native full-screen entry/exit cycles each publish one actual state change')
            await page.click('#enter'); await page.wait_for_function('document.fullscreenElement !== null')
            await page.evaluate('document.exitFullscreen()')
            await page.wait_for_function('changes.length === 8 && changes[7] === false')
            checks.append('Actual external platform exit is observed without dispatching a synthetic event')
            await page.click('#enter'); await page.wait_for_function('document.fullscreenElement !== null')
            await page.click('#dispose'); await page.wait_for_function('document.fullscreenElement === null && result === false')
            assert len(await page.evaluate('changes')) == 9
            checks.append('Disposing an active owner exits fullscreen and removes its notification listener')
            # The sandbox creates an opaque-origin child; fullscreen is denied
            # by its real permissions policy even after clicking its own button.
            denied = '<!doctype html><button id="enter">Enter</button><script type="module">'+source+'''
                const owner=createController(document,()=>{});
                globalThis.result=null;
                document.querySelector('#enter').onclick=()=>owner.set(true).then(v=>result=v,e=>result={error:e.message});
            </script>'''
            await page.set_content('<iframe sandbox="allow-scripts" allow="fullscreen \'none\'" srcdoc="'+html.escape(denied,quote=True)+'"></iframe>')
            frame = page.frames[-1]
            await frame.locator('#enter').click()
            await frame.wait_for_function('globalThis.result !== null')
            assert isinstance(await frame.evaluate('result'),dict) and not await frame.evaluate('Boolean(document.fullscreenElement)')
            checks.append('Real iframe fullscreen policy denies entry without claiming a successful transition')
            assert not errors, errors
            for check in checks: print('PASS '+check,flush=True)
        finally:
            (output/'checks.json').write_text(json.dumps(checks,indent=2));(output/'errors.json').write_text(json.dumps(errors,indent=2))
            await browser.close()

if __name__ == '__main__': asyncio.run(run(Path(sys.argv[1])))
