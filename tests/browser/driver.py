"""Shared real-browser helpers for the Uno Skia accessibility tree."""
import asyncio
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
    # fill() directly rewrites the semantic HTML input, bypassing Uno's native
    # selection state. Exercise the same select-all/delete/type path as a user.
    box = page.get_by_label(label, exact=True)
    await expect(box).to_be_attached(timeout=30000)
    await box.press('Control+a')
    await box.press('Backspace')
    await box.press_sequentially(value, delay=15)
    await expect(box).to_have_value(value, timeout=10000)

async def navigate(page, name):
    await page.get_by_role('option', name=name, exact=True).press('Enter')
