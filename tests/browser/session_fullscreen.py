"""Actual Uno full-screen commands, public browser state and real remote pixels."""
import asyncio
import json
import subprocess
import time
from playwright.async_api import expect
from driver import command
from fullscreen_pixels import validate_fullscreen_pixels
from toolbar_collapse import validate_toolbar_collapse

async def validate_session_fullscreen(page, metrics, output, passed):
    sizes = list(metrics['sizes'])
    layouts = metrics['layout_requests']
    input_before = metrics['input_packets']
    # The previous scenario restores a narrow viewport immediately before this
    # test. Browser resize completion precedes Uno's responsive layout and paint;
    # do not preserve a mixed old-origin/new-width rectangle as the baseline.
    # Validate its actual pixels and inverse input before recording the settled
    # windowed geometry. This adds a real check instead of loosening restoration.
    await page.evaluate('() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))')
    input_before = await validate_fullscreen_pixels(page, metrics, output, '18-windowed-baseline', input_before)
    await expect(page.get_by_label('Workspace sections', exact=True).get_by_label('Remote desktop', exact=True)).to_be_visible()
    baseline = await page.get_by_label('Remote desktop', exact=True).bounding_box()
    async def entered(name):
        nonlocal input_before
        await command(page, 'Full screen')
        await page.wait_for_function('document.fullscreenElement === document.documentElement')
        await expect(page.get_by_role('button', name='Exit full screen', exact=True)).to_be_visible()
        await expect(page.get_by_role('button', name='About RemoteSpace', exact=True)).not_to_be_visible()
        host = page.get_by_label('Full-screen remote session', exact=True)
        await expect(host.get_by_label('Remote desktop', exact=True)).to_be_visible()
        await expect(page.get_by_role('option', name='Devices', exact=True)).not_to_be_visible()
        for label in ('Fit', 'Display settings', 'Remote resolution', 'Clipboard', 'Keyboard', 'Session actions', 'Exit full screen'):
            button = host.get_by_role('button', name=label, exact=True)
            await expect(button).to_be_visible()
            box = await button.bounding_box()
            width = await page.evaluate('innerWidth')
            if not box or box['x'] < 0 or box['x'] + box['width'] > width + 1:
                raise AssertionError('A fullscreen command has an out-of-window hit target: ' + label)
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
                    input_before = await validate_fullscreen_pixels(page, metrics, output, name, input_before)
                    return
            if time.monotonic() >= until:
                raise AssertionError('Full screen did not expand the real remote surface to the window beneath its pinned toolbar: '+str(bounds))
            await asyncio.sleep(.1)
    async def restored():
        await page.wait_for_function('document.fullscreenElement === null')
        await expect(page.get_by_role('button', name='Full screen', exact=True)).to_be_visible()
        await expect(page.get_by_role('button', name='Exit full screen', exact=True)).to_have_count(0)
        await expect(page.get_by_role('button', name='Collapse connection toolbar', exact=True)).to_have_count(0)
        await expect(page.get_by_role('button', name='Expand connection toolbar', exact=True)).to_have_count(0)
        await expect(page.get_by_role('button', name='About RemoteSpace', exact=True)).to_be_visible()
        await expect(page.get_by_role('tab', name='Loopback desktop', exact=True)).to_be_visible()
        await expect(page.get_by_role('button', name='Close Tab', exact=True)).to_be_visible()
        await expect(page.get_by_label('Active remote session', exact=True).get_by_label('Remote desktop', exact=True)).to_be_visible()
        await expect(page.get_by_label('Full-screen remote session', exact=True)).not_to_be_visible()
        deadline = time.monotonic() + 10
        while True:
            bounds = await page.get_by_label('Remote desktop', exact=True).bounding_box()
            if bounds and all(abs(bounds[key] - baseline[key]) <= 1 for key in ('x', 'y', 'width', 'height')):
                break
            if time.monotonic() >= deadline:
                raise AssertionError('Exiting fullscreen did not restore the original windowed view geometry: ' +
                                     json.dumps({'before': baseline, 'after': bounds}))
            await asyncio.sleep(.1)
        if metrics['sizes'] != sizes or metrics['layout_requests'] != layouts:
            raise AssertionError('A local fullscreen transition changed server resolution or reconnected the session.')

    await entered('18-fullscreen-session.png')
    input_before = await validate_toolbar_collapse(page, metrics, output, passed, input_before)
    await command(page, 'Exit full screen')
    await restored()
    passed('Full screen displays the real remote desktop beneath the pinned toolbar and restores the workspace without resizing the server')
    await entered('19-fullscreen-external-exit.png')
    await command(page, 'Collapse connection toolbar')
    await expect(page.get_by_role('button', name='Expand connection toolbar', exact=True)).to_be_visible()
    # Exercise a real host-level exit, not an application command or synthetic
    # fullscreenchange. Chromium's headless Escape accelerator differs by host;
    # the public platform exit guarantees a real event for this lifecycle test.
    await page.evaluate('document.exitFullscreen()')
    await restored()
    if metrics['input_packets'] != input_before:
        raise AssertionError('Fullscreen commands leaked local activation into remote keyboard input.')
    await validate_fullscreen_pixels(page, metrics, output, '20-fullscreen-restored', input_before)
    await page.screenshot(path=str(output/'20-fullscreen-restored.png'))
    passed('A platform fullscreen exit restores the session controls, with no duplicate peers or remote activation keys')
    passed('Full-screen capture samples and inverse pointer coordinates agree at both entries and restoration without changing server pixels')
