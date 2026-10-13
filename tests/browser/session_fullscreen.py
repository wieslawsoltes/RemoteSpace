"""Actual Uno full-screen commands, public browser state and real remote pixels."""
import asyncio
import json
import subprocess
import time
from playwright.async_api import expect
from driver import command

async def validate_session_fullscreen(page, metrics, output, passed):
    sizes = list(metrics['sizes'])
    layouts = metrics['layout_requests']
    input_before = metrics['input_packets']
    baseline = await page.get_by_label('Remote desktop', exact=True).bounding_box()
    async def entered(name):
        await command(page, 'Full screen')
        await page.wait_for_function('document.fullscreenElement === document.documentElement')
        await expect(page.get_by_role('button', name='Exit full screen', exact=True)).to_be_visible()
        await expect(page.get_by_role('button', name='About RemoteSpace', exact=True)).not_to_be_visible()
        until = time.monotonic() + 15
        while True:
            size = await page.evaluate('({width:innerWidth,height:innerHeight})')
            bounds = await page.get_by_label('Remote desktop', exact=True).bounding_box()
            if bounds and bounds['x'] <= 2 and bounds['y'] <= 70 and bounds['width'] >= size['width'] - 4 and bounds['height'] >= size['height'] - 80:
                picture = output / name
                await page.screenshot(path=str(picture))
                check = subprocess.run(['python3','tests/browser/assert_pixels.py',str(picture)],capture_output=True,text=True)
                if check.returncode == 0:
                    (output/(name+'.geometry.json')).write_text(json.dumps({'host':size,'remote_view':bounds,'windowed_view':baseline},indent=2))
                    return
            if time.monotonic() >= until:
                raise AssertionError('Full screen did not expand the real remote surface to the window beneath its pinned toolbar: '+str(bounds))
            await asyncio.sleep(.1)
    async def restored():
        await page.wait_for_function('document.fullscreenElement === null')
        await expect(page.get_by_role('button', name='Full screen', exact=True)).to_be_visible()
        await expect(page.get_by_role('button', name='Exit full screen', exact=True)).to_have_count(0)
        await expect(page.get_by_role('button', name='About RemoteSpace', exact=True)).to_be_visible()
        await expect(page.get_by_role('tab', name='Loopback desktop', exact=True)).to_be_visible()
        await expect(page.get_by_role('button', name='Close Tab', exact=True)).to_be_visible()
        await expect(page.get_by_label('Remote desktop', exact=True)).to_be_visible()
        if metrics['sizes'] != sizes or metrics['layout_requests'] != layouts:
            raise AssertionError('A local fullscreen transition changed server resolution or reconnected the session.')

    await entered('18-fullscreen-session.png')
    await command(page, 'Exit full screen')
    await restored()
    passed('Full screen displays the real remote desktop beneath the pinned toolbar and restores the workspace without resizing the server')
    await entered('19-fullscreen-external-exit.png')
    # Exercise a real host-level exit, not an application command or synthetic
    # fullscreenchange. Chromium's headless Escape accelerator differs by host;
    # the public platform exit guarantees a real event for this lifecycle test.
    await page.evaluate('document.exitFullscreen()')
    await restored()
    if metrics['input_packets'] != input_before:
        raise AssertionError('Fullscreen commands leaked local activation into remote keyboard input.')
    await page.screenshot(path=str(output/'20-fullscreen-restored.png'))
    passed('A platform fullscreen exit restores the session controls, with no duplicate peers or remote activation keys')
