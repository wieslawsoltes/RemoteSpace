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
from playwright.async_api import async_playwright, expect
from driver import boot, command, edit, navigate

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
        page = await browser.new_page(viewport={'width':1440, 'height':1000})
        page.on('console', lambda message: messages.append(message.type + ': ' + message.text))
        page.on('pageerror', lambda error: errors.append(str(error)))
        try:
            await boot(page, f'http://127.0.0.1:{server.server_port}/RemoteSpace/')
            await expect(page.get_by_role('button', name='Add your first PC', exact=True)).to_be_attached()
            passed('Uno WASM boot and accessible command tree at a Pages-style project subpath')
            await page.screenshot(path=str(output/'01-workspace.png'))
            await page.get_by_role('button', name='Add your first PC', exact=True).click(force=True)
            await edit(page, 'Friendly name', 'Test workstation')
            await edit(page, 'PC address', 'rdp.test.invalid')
            await command(page, 'Save')
            connect = page.get_by_role('button', name='Connect to Test workstation', exact=True)
            await expect(connect).to_be_attached(timeout=30000)
            passed('Add-PC dialog saves the exact entered name and address')
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
            await browser.close()
            server.shutdown(); server.server_close(); webroot.cleanup()

if __name__ == '__main__':
    asyncio.run(run(Path(sys.argv[1]), Path(sys.argv[2])))
