"""Shared real-browser helpers for the Uno Skia accessibility tree."""
import asyncio
import time
from playwright.async_api import expect

async def boot(page, url=None):
    ready = asyncio.Event()
    def console(message):
        if 'REMOTESPACE_READY' in message.text:
            ready.set()
    page.on('console', console)
    try:
        if url:
            await page.goto(url, wait_until='domcontentloaded')
        else:
            await page.reload(wait_until='domcontentloaded')
        await asyncio.wait_for(ready.wait(), 120)
        enabler = page.get_by_role('button', name='Enable accessibility', exact=True)
        if await enabler.count():
            await enabler.press('Enter')
    finally:
        page.remove_listener('console', console)

async def command(page, name):
    button = page.get_by_role('button', name=name, exact=True)
    await expect(button).to_be_attached(timeout=30000)
    await button.press('Enter')

async def edit(page, label, value):
    # Do not fill() the semantic HTML proxy: it bypasses Uno's native selection.
    # Re-resolve the peer for each real key and allow presentation/selection
    # updates to finish. A blind burst races peer updates under software Skia.
    # This is a correctness test, not a typing-latency benchmark.
    box = page.get_by_label(label, exact=True)
    async def checkpoint():
        await page.evaluate('() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))')
    async def acknowledged(expected):
        until = time.monotonic() + 10
        while await box.input_value() != expected:
            if time.monotonic() >= until:
                # Never put expected OR partially entered password/token values
                # into an assertion, traceback, or CI artifact.
                raise AssertionError(f'{label}: the control did not acknowledge text entry')
            await asyncio.sleep(.02)
    try:
        await expect(box).to_be_attached(timeout=30000)
        await box.focus()
        await checkpoint()
        await box.press('Control+a')
        await checkpoint()
        await box.press('Backspace')
        await checkpoint()
        await acknowledged('')
        prefix = ''
        for character in value:
            await box.press_sequentially(character, delay=20)
            await checkpoint()
            prefix += character
            await acknowledged(prefix)
    except Exception:
        raise AssertionError(f'{label}: real keyboard text entry failed; values intentionally omitted') from None

async def navigate(page, name):
    await page.get_by_role('option', name=name, exact=True).press('Enter')
