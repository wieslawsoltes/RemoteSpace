"""Validate the actual published Uno WASM application, not an HTML substitute."""
import asyncio
import functools
import http.server
import json
from pathlib import Path
import sys
import threading
import tempfile
import traceback
import time
from io import BytesIO
from PIL import Image
from playwright.async_api import async_playwright, expect
from driver import boot, command, edit, navigate
from workspace_ui import validate_workspace_ui

async def run(site, output):
    output.mkdir(parents=True, exist_ok=True)
    webroot = tempfile.TemporaryDirectory(prefix='remotespace-pages-')
    (Path(webroot.name)/'RemoteSpace').symlink_to(site.resolve(), target_is_directory=True)
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(http.server.SimpleHTTPRequestHandler, directory=webroot.name))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    messages, errors, checks = [], [], []
    def passed(name):
        checks.append(name)
        print('PASS', name, flush=True)
    async with async_playwright() as playwright:
        browser = await playwright.chromium.launch(args=['--enable-unsafe-swiftshader'])
        # An explicit context supports multiple app windows sharing IndexedDB.
        context = await browser.new_context(viewport={'width':1440, 'height':1000}, accept_downloads=True)
        page = await context.new_page()
        page.on('console', lambda message: messages.append(message.type + ': ' + message.text))
        page.on('pageerror', lambda error: errors.append(str(error)))
        try:
            await boot(page, f'http://127.0.0.1:{server.server_port}/RemoteSpace/')
            await expect(page.get_by_role('button', name='Add your first PC', exact=True)).to_be_attached()
            passed('Uno WASM boot and accessible command tree at a Pages-style project subpath')
            await page.screenshot(path=str(output/'01-workspace.png'))
            await page.get_by_role('button', name='Add your first PC', exact=True).click(force=True)
            await expect(page.get_by_label('Friendly name', exact=True)).to_have_count(0)
            await edit(page, 'Friendly name', 'Test workstation')
            expansion = page.locator('button[xamlautomationid="ExpanderToggleButton"]')
            await expansion.press('Space')
            await expect(page.get_by_label('Friendly name', exact=True)).to_have_count(0)
            await expansion.press('Space')
            await expect(page.get_by_label('Friendly name', exact=True)).to_be_visible()
            await expect(page.get_by_label('Friendly name', exact=True)).to_have_value('Test workstation')
            passed('Collapsed additional settings remove hidden input peers and preserve values after reopening')
            await edit(page, 'PC address', 'rdp.test.invalid')
            await command(page, 'Add')
            connect = page.get_by_role('button', name='Connect to Test workstation', exact=True)
            await expect(connect).to_be_attached(timeout=30000)
            await expect(connect).to_be_visible()
            until = time.monotonic() + 10
            while True:
                png = await page.screenshot()
                image = Image.open(BytesIO(png)).convert('RGB')
                colors = image.getcolors(image.width * image.height)
                saturated = sum(n for n, color in colors if max(color) - min(color) > 45)
                if saturated >= 10_000:
                    (output/'02-first-device.png').write_bytes(png)
                    break
                if time.monotonic() >= until:
                    raise AssertionError('Saved device exists but its independently drawn preview was not visibly rendered.')
                await asyncio.sleep(.1)
            passed('Add-PC dialog saves the exact entered name and address and paints the actual device card')
            await edit(page, 'Search devices', 'no-match')
            await expect(connect).to_have_count(0)
            await edit(page, 'Search devices', '')
            await expect(connect).to_be_attached()
            passed('Search filters and restores matching devices')
            await command(page, 'Toggle favorite Test workstation')
            await navigate(page, 'Favorites')
            await expect(connect).to_be_attached(timeout=30000)
            await page.screenshot(path=str(output/'02-device-card.png'))
            await boot(page)
            await navigate(page, 'Favorites')
            await expect(connect).to_be_attached(timeout=30000)
            passed('Device and favorite state survive browser reload')
            await command(page, 'Connect to Test workstation')
            await expect(page.get_by_text('Set your authenticated wss:// bridge endpoint in Settings before connecting from the browser.', exact=True)).to_be_attached()
            passed('Missing bridge is diagnosed without pretending to connect')
            await page.screenshot(path=str(output/'03-connection-requirement.png'))
            await validate_workspace_ui(page, output, passed)
            if errors: raise AssertionError('Uncaught browser errors: ' + '\n'.join(errors))
            passed('No uncaught JavaScript or WASM errors')
        except BaseException:
            (output/'failure.txt').write_text(traceback.format_exc())
            raise
        finally:
            (output/'console.log').write_text('\n'.join(messages))
            (output/'errors.json').write_text(json.dumps(errors, indent=2))
            (output/'checks.json').write_text(json.dumps(checks, indent=2))
            (output/'final-dom.html').write_text(await page.content())
            await page.screenshot(path=str(output/'final.png'))
            await context.close()
            await browser.close()
            server.shutdown(); server.server_close(); webroot.cleanup()

if __name__ == '__main__':
    asyncio.run(run(Path(sys.argv[1]), Path(sys.argv[2])))
