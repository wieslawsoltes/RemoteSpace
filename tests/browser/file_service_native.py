"""Run the exact file interop module with real Chromium File/FileReader/downloads.
This is a component integration test, not an Uno application or RDP parity test.
The actual Uno workspace/session suites remain separate merge/deploy gates.
"""
import asyncio
import json
import os
from pathlib import Path
import sys
from playwright.async_api import async_playwright

async def run(output):
    output.mkdir(parents=True, exist_ok=True)
    source = (Path(__file__).parents[2] / 'src/RemoteSpace.Controls/SystemFileAccess.cs').read_text()
    source = source.split('private const string Source = """\n', 1)[1].split('\n""";', 1)[0]
    checks, errors = [], []
    async with async_playwright() as playwright:
        options = {'headless': True}
        if os.environ.get('CHROMIUM'):
            options['executable_path'] = os.environ['CHROMIUM']
        browser = await playwright.chromium.launch(**options)
        page = await browser.new_page(accept_downloads=True)
        page.on('pageerror', lambda error: errors.append(str(error)))
        try:
            await page.set_content('<!doctype html><meta charset="utf-8"><title>File component integration</title><button id="pick">Pick fixture</button><button id="export">Export fixture</button>')
            await page.add_script_tag(type='module', content=source + '''
                globalThis.fileService = {pick, download};
                globalThis.stats = {reads:0, aborts:0};
                const read = FileReader.prototype.readAsArrayBuffer;
                const abort = FileReader.prototype.abort;
                FileReader.prototype.readAsArrayBuffer = function(blob) { stats.reads++; return read.call(this, blob); };
                FileReader.prototype.abort = function() { stats.aborts++; return abort.call(this); };
                document.querySelector('#pick').onclick = () => {
                    globalThis.result = null;
                    fileService.pick('.json', globalThis.limit ?? 4).then(value => {
                        result = value.cancelled ? {cancelled:true} : {name:value.name,bytes:Array.from(value.bytes)};
                    }, error => result = {error:error.message});
                };
                document.querySelector('#export').onclick = () => fileService.download('fixture.json', new Uint8Array([255,0,128,7]), 'application/json');
            ''')
            await page.wait_for_function('globalThis.fileService !== undefined')
            async def choose(name, data):
                async with page.expect_file_chooser() as chooser:
                    await page.click('#pick')
                await (await chooser.value).set_files({'name':name,'mimeType':'application/json','buffer':bytes(data)})
                await page.wait_for_function('globalThis.result !== null')
                result = await page.evaluate('globalThis.result')
                if await page.locator('input[type=file]').count():
                    raise AssertionError('File input remained attached after completion.')
                return result
            result = await choose('fixture.JSON', [255,0,128,7])
            assert result == {'name':'fixture.JSON','bytes':[255,0,128,7]}, result
            checks.append('Real FileReader preserves binary bytes through a native file chooser')
            reads = await page.evaluate('stats.reads')
            result = await choose('too-big.json', [1,2,3,4,5])
            assert 'byte limit' in result['error'] and await page.evaluate('stats.reads') == reads, result
            checks.append('Oversized actual File is rejected before starting a reader')
            result = await choose('wrong.rdp', [1])
            assert 'wrong extension' in result['error'] and await page.evaluate('stats.reads') == reads, result
            checks.append('Native selection of a different extension is rejected without reading')
            await page.evaluate('globalThis.limit = 0')
            assert await choose('empty.json', []) == {'name':'empty.json','bytes':[]}
            checks.append('Zero-byte limit accepts an actual empty file')
            await page.evaluate('''() => {
                globalThis.limit = 4;
                document.addEventListener('change', () => {
                    // A real FileReader has begun at the input's target handler;
                    // deliver a lifecycle exit before its queued load completion.
                    dispatchEvent(new PageTransitionEvent('pagehide', {persisted:true}));
                }, {once:true});
            }''')
            result = await choose('abort.json', [1,2,3,4])
            assert result == {'cancelled':True} and await page.evaluate('stats.aborts') == 1, result
            assert await choose('after-restore.json', [2]) == {'name':'after-restore.json','bytes':[2]}
            checks.append('Lifecycle exit aborts a real reader and releases selection for page restoration')
            async with page.expect_download() as download:
                await page.click('#export')
            download = await download.value
            assert download.suggested_filename == 'fixture.json'
            await download.save_as(str(output / 'fixture.json'))
            assert (output / 'fixture.json').read_bytes() == bytes([255,0,128,7])
            assert await page.locator('a[download]').count() == 0
            checks.append('Actual Blob download preserves bytes and removes its anchor')
            assert not errors, errors
            for check in checks:
                print('PASS ' + check, flush=True)
        finally:
            (output / 'checks.json').write_text(json.dumps(checks, indent=2))
            (output / 'errors.json').write_text(json.dumps(errors, indent=2))
            await browser.close()

if __name__ == '__main__':
    asyncio.run(run(Path(sys.argv[1])))
