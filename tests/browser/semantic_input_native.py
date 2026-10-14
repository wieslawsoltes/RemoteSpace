"""Exact semantic adapter with physical Chromium keyboard/default-button events."""
import asyncio
import json
import os
from pathlib import Path
import sys
from playwright.async_api import async_playwright

async def run(output):
    output.mkdir(parents=True, exist_ok=True)
    source = (Path(__file__).parents[2] / 'src/RemoteSpace.Controls/BrowserSemanticInput.cs').read_text()
    source = source.split('private const string Source = """\n', 1)[1].split('\n""";', 1)[0]
    checks, errors = [], []
    async with async_playwright() as p:
        options = {'headless': True}
        if os.environ.get('CHROMIUM'):
            options['executable_path'] = os.environ['CHROMIUM']
        browser = await p.chromium.launch(**options)
        page = await browser.new_page()
        page.on('pageerror', lambda error: errors.append(str(error)))
        try:
            await page.set_content('''<!doctype html><title>Owned activation state</title>
                <div id="uno-semantics-root">
                <button id="owned" xamlautomationid="remotespace-session-command-test">Toolbar</button>
                <button id="other">Other</button>
                <div role="menuitem" tabindex="0" id="item">Menu command</div></div>''')
            await page.add_script_tag(type='module', content=source+'''
                globalThis.held = isActivationHeld;
                globalThis.calls = {owned:0,other:0,item:0,native:0};
                document.querySelector('#owned').onclick=()=>calls.owned++;
                document.querySelector('#other').onclick=()=>calls.other++;
                document.querySelector('#item').onkeydown=event=>{
                    if(event.key==='Enter' || event.key===' '){event.preventDefault();calls.item++;}
                };
                // Same bubbling boundary as the shipped Uno runtime's native
                // keyboard source. The adapter must keep owned keys from it.
                document.addEventListener('keydown',()=>calls.native++);
                document.addEventListener('keyup',()=>calls.native++);
                install();
            ''')
            await page.wait_for_function('globalThis.held !== undefined')
            assert await page.evaluate('held()') is False
            await page.locator('#owned').focus()
            await page.keyboard.down('Enter')
            await page.wait_for_timeout(240)
            assert await page.evaluate('held()') is True
            assert await page.evaluate('calls') == {'owned':1,'other':0,'item':0,'native':0}
            for _ in range(4):
                await page.keyboard.down('Enter')
            assert await page.evaluate('calls.owned') == 1
            await page.keyboard.up('Enter')
            assert await page.evaluate('held()') is False
            assert await page.evaluate('calls.native') == 0
            checks.append('Real held Enter is visible to the release barrier while native routing and repeated activation stay suppressed')

            await page.locator('#owned').focus()
            await page.keyboard.down('Space')
            assert await page.evaluate('held()') is True
            assert await page.evaluate('calls.owned') == 1
            await page.keyboard.up('Space')
            assert await page.evaluate('held()') is False
            assert await page.evaluate('calls.owned') == 2
            await page.keyboard.down('Space')
            await page.locator('#other').focus()
            await page.keyboard.up('Space')
            assert await page.evaluate('held()') is False
            assert await page.evaluate('calls') == {'owned':2,'other':0,'item':0,'native':0}
            checks.append('Real Space keeps its original default button activation and cannot click a newly focused command on release')

            await page.locator('#item').focus()
            await page.keyboard.down('Enter')
            assert await page.evaluate('held()') is True
            await page.locator('#other').focus()
            await page.keyboard.down('Enter')
            await page.keyboard.up('Enter')
            assert await page.evaluate('held()') is False
            assert await page.evaluate('calls') == {'owned':2,'other':0,'item':1,'native':0}
            await page.keyboard.press('x')
            assert await page.evaluate('held()') is False
            assert await page.evaluate('calls.native') == 2
            checks.append('Handled menu gestures retain release ownership across focus changes while ordinary input keeps native routing')
            assert not errors, errors
            for check in checks:
                print('PASS '+check, flush=True)
        finally:
            (output/'checks.json').write_text(json.dumps(checks, indent=2))
            (output/'errors.json').write_text(json.dumps(errors, indent=2))
            await browser.close()

if __name__ == '__main__':
    asyncio.run(run(Path(sys.argv[1])))
