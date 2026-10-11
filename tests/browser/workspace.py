"""Exercise the actual Uno WASM/Skia app through browser mouse, keyboard and accessibility.
Run: python tests/browser/workspace.py artifacts/browser artifacts/browser-validation
"""
import asyncio
import functools
import http.server
import json
from pathlib import Path
import sys
import threading
import traceback
from playwright.async_api import async_playwright, expect

async def run(site: Path, output: Path):
    output.mkdir(parents=True, exist_ok=True)
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(http.server.SimpleHTTPRequestHandler, directory=str(site.resolve())))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    messages, errors, checks = [], [], []
    def passed(name):
        checks.append(name)
        print('PASS', name, flush=True)
    async with async_playwright() as playwright:
        browser = await playwright.chromium.launch(args=['--enable-unsafe-swiftshader'])
        page = await browser.new_page(viewport={'width': 1440, 'height': 1000}, device_scale_factor=1)
        ready = asyncio.Event()
        def on_console(message):
            messages.append(message.type + ': ' + message.text)
            if 'REMOTESPACE_READY' in message.text: ready.set()
        page.on('console', on_console)
        page.on('pageerror', lambda error: errors.append(str(error)))
        async def enable_accessibility():
            enabler = page.get_by_role('button', name='Enable accessibility', exact=True)
            if await enabler.count(): await enabler.press('Enter')
        async def command(name):
            # Uno's semantic DOM is pointer-events:none. In popup roots its
            # bounding boxes can be parent-relative; force-clicking those boxes
            # clicks unrelated canvas coordinates. Keyboard activation exercises
            # the real automation peer, as a screen-reader user does.
            button = page.get_by_role('button', name=name, exact=True)
            await expect(button).to_be_attached()
            await button.press('Enter')
        try:
            await page.goto(f'http://127.0.0.1:{server.server_port}/', wait_until='domcontentloaded')
            await asyncio.wait_for(ready.wait(), 120)
            await enable_accessibility()
            await page.wait_for_timeout(1000)
            await page.screenshot(path=str(output/'01-workspace.png'))
            passed('Uno WASM application booted and rendered')
            (output/'initial-dom.html').write_text(await page.content())
            await expect(page.get_by_role('button', name='Add your first PC', exact=True)).to_be_attached()
            passed('Skia accessible command tree is available')
            # Retain a genuine mouse path for the initial canvas button.
            await page.get_by_role('button', name='Add your first PC', exact=True).click(force=True)
            await expect(page.get_by_role('textbox', name='Friendly name', exact=True)).to_be_attached()
            await page.get_by_role('textbox', name='Friendly name', exact=True).fill('Test workstation')
            await page.get_by_role('textbox', name='PC address', exact=True).fill('rdp.test.invalid')
            await command('Save')
            await expect(page.get_by_role('textbox', name='Friendly name', exact=True)).to_have_count(0)
            await expect(page.get_by_role('button', name='Connect to Test workstation', exact=True)).to_be_attached()
            passed('Add-PC dialog saves and renders a real device card')
            await page.get_by_role('textbox', name='Search devices', exact=True).fill('no-match')
            await expect(page.get_by_role('button', name='Connect to Test workstation', exact=True)).to_have_count(0)
            await page.get_by_role('textbox', name='Search devices', exact=True).fill('')
            await expect(page.get_by_role('button', name='Connect to Test workstation', exact=True)).to_be_attached()
            passed('Search filters saved devices and restores the match')
            await command('Toggle favorite Test workstation')
            await page.screenshot(path=str(output/'02-device-card.png'))
            ready.clear()
            await page.reload(wait_until='domcontentloaded')
            await asyncio.wait_for(ready.wait(), 120)
            await enable_accessibility()
            await expect(page.get_by_role('button', name='Connect to Test workstation', exact=True)).to_be_attached()
            passed('Saved devices survive a browser reload')
            await command('Connect to Test workstation')
            await expect(page.get_by_text('Set your authenticated wss:// bridge endpoint in Settings before connecting from the browser.', exact=True)).to_be_attached()
            passed('Missing bridge is diagnosed without pretending to connect')
            await page.screenshot(path=str(output/'03-connection-requirement.png'))
            if errors: raise AssertionError('Browser errors: ' + '\n'.join(errors))
            passed('No uncaught JavaScript or WASM application errors')
        except BaseException:
            (output/'failure.txt').write_text(traceback.format_exc())
            raise
        finally:
            (output/'console.log').write_text('\n'.join(messages))
            (output/'errors.json').write_text(json.dumps(errors, indent=2))
            (output/'checks.json').write_text(json.dumps(checks, indent=2))
            (output/'final-dom.html').write_text(await page.content())
            await page.screenshot(path=str(output/'final.png'))
            await browser.close()
            server.shutdown()

if __name__ == '__main__':
    asyncio.run(run(Path(sys.argv[1]), Path(sys.argv[2])))
