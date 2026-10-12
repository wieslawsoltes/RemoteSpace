"""Shared real-browser helpers for the Uno Skia accessibility tree."""
import asyncio
import time
from io import BytesIO
from PIL import Image
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
            # This sr-only HTML bootstrap is not an application command. Enter
            # enables the tree on keydown, then its keyup can invoke the first
            # native button (About). Click the public bootstrap element directly;
            # all app commands below still use real keyboard or canvas input.
            await enabler.evaluate('(button) => button.click()')
        # The managed Loaded event and semantic peers can precede Skia's first
        # presentation. Require painted content, not the uniform loader surface,
        # before reading mouse bounds or taking evidence screenshots.
        until = time.monotonic() + 60
        while True:
            image = Image.open(BytesIO(await page.screenshot())).convert('RGB')
            content = image.crop((110, 40, min(image.width, 900), min(image.height, 450)))
            colors = content.getcolors(content.width * content.height)
            if len(colors) > 64 and sum(n for n, _ in colors) - max(n for n, _ in colors) > 800:
                break
            if time.monotonic() >= until:
                raise AssertionError('The Uno workspace did not visibly paint after managed startup.')
            await asyncio.sleep(.15)
        await page.evaluate('() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))')
        await expect(page.get_by_role('button', name='Close', exact=True)).to_have_count(0)
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
    stage = 'resolve input'
    try:
        bounds = await box.bounding_box() if await box.count() else None
        if not bounds or bounds["width"] <= 0 or bounds["height"] <= 0:
            expansion = page.locator('button[xamlautomationid="ExpanderToggleButton"]')
            if await expansion.count() and await expansion.get_attribute('aria-pressed') != 'true':
                stage = 'expand additional settings'
                await expansion.press('Space')
                await expect(expansion).to_have_attribute('aria-pressed', 'true')
        # A semantic peer is not usable until it has positive layout bounds.
        stage = 'wait for input layout'
        await expect(box).to_be_visible(timeout=30000)
        # ContentDialog's opening animation can still assign initial focus after
        # the semantic field gets layout bounds. Let that transition finish,
        # then use real focus/selection/key events without changing input values.
        if await page.get_by_role('dialog').count():
            await page.wait_for_timeout(250)
        stage = 'focus input'
        await box.focus()
        await checkpoint()
        stage = 'select existing text'
        await box.press('Control+a')
        await checkpoint()
        await box.press('Backspace')
        await checkpoint()
        stage = 'acknowledge cleared input'
        await acknowledged('')
        prefix = ''
        for character in value:
            stage = 'type next character'
            await box.press_sequentially(character, delay=20)
            await checkpoint()
            prefix += character
            stage = 'acknowledge typed character'
            await acknowledged(prefix)
    except Exception as error:
        raise AssertionError(f'{label}: real keyboard text entry failed at {stage} ({type(error).__name__}); values intentionally omitted') from None

async def navigate(page, name):
    await page.get_by_role('option', name=name, exact=True).press('Enter')
