"""Real automatic-resolution requests, confirmed server geometry and painted pixels."""
import asyncio
import json
import math
import subprocess
import time
from playwright.async_api import expect
from driver import command, focus_command, navigate

async def validate_automatic_resolution(page, metrics, output, passed):
    async def pixels_and_geometry(name, before_packets):
        until = time.monotonic() + 30
        while True:
            bounds = await page.get_by_label('Remote desktop', exact=True).bounding_box()
            expected = [math.floor(bounds['width']) & ~1, math.floor(bounds['height'])] if bounds else None
            if expected and metrics['sizes'][-1] == expected and metrics['surface_packets'] > before_packets:
                picture = output / name
                await page.screenshot(path=str(picture))
                check = subprocess.run(['python3', 'tests/browser/assert_pixels.py', str(picture)], capture_output=True, text=True)
                if check.returncode == 0:
                    await asyncio.sleep(.6)
                    (output / (name + '.geometry.json')).write_text(json.dumps({'viewport':bounds,'server':expected,'requests':metrics['layout_requests']},indent=2))
                    return expected
            if time.monotonic() >= until:
                raise AssertionError('Automatic resolution did not converge to the actual view with confirmed server pixels.')
            await asyncio.sleep(.1)

    original = page.viewport_size
    before = metrics['layout_requests']
    before_packets = metrics['surface_packets']
    await command(page, 'Update resolution on resize')
    await pixels_and_geometry('11-auto-initial.png', before_packets)
    if metrics['layout_requests'] != before + 1:
        raise AssertionError('Enabling automatic resolution did not send exactly one layout.')
    passed('Automatic resolution matches the actual session view with one server-confirmed layout and visible pixels')
    before = metrics['layout_requests']; before_packets = metrics['surface_packets']
    started = time.monotonic()
    for width in (1510, 1460, 1540, 1430, 1400):
        await page.set_viewport_size({'width':width, 'height':980})
    duration = time.monotonic() - started
    await pixels_and_geometry('12-auto-latest.png', before_packets)
    requests = metrics['layout_requests'] - before
    if requests < 1 or requests > math.ceil(duration / .25) + 1:
        raise AssertionError('Window resizing flooded the server with layout requests.')
    if duration < .2 and requests != 1:
        raise AssertionError('A sub-settle-window resize burst was not coalesced into one latest target.')
    stable = metrics['layout_requests']; await asyncio.sleep(.8)
    if metrics['layout_requests'] != stable:
        raise AssertionError('Unchanged server geometry kept scheduling resize requests.')
    passed(f'Window resize burst converges to the latest viewport ({requests} layout requests); unchanged geometry stays idle')

    await navigate(page, 'Devices')
    before = metrics['layout_requests']; before_packets = metrics['surface_packets']
    await page.set_viewport_size({'width':1320,'height':940})
    await asyncio.sleep(.8)
    if metrics['layout_requests'] != before:
        raise AssertionError('A hidden session sent automatic display requests.')
    await navigate(page, 'Active sessions')
    await pixels_and_geometry('13-auto-visible.png', before_packets)
    if metrics['layout_requests'] != before + 1:
        raise AssertionError('Revealing the session did not apply exactly one latest viewport.')
    passed('Hidden sessions defer automatic resizing and apply the latest size once when selected again')

    await command(page, 'Pause updates')
    until = time.monotonic() + 10
    while metrics.get('output_enabled', True):
        if time.monotonic() >= until: raise AssertionError('Graphics pause was not acknowledged.')
        await asyncio.sleep(.05)
    before = metrics['layout_requests']; before_packets = metrics['surface_packets']
    await page.set_viewport_size({'width':1280,'height':960})
    await asyncio.sleep(.8)
    if metrics['layout_requests'] != before:
        raise AssertionError('A graphics-paused session sent an automatic resize.')
    await command(page, 'Resume updates')
    await pixels_and_geometry('14-auto-resumed.png', before_packets)
    if metrics['layout_requests'] != before + 1:
        raise AssertionError('Resuming graphics did not send one latest layout.')
    passed('Graphics pause suspends automatic resolution; resume applies the latest viewport once')

    await command(page, 'Remote resolution')
    item = page.get_by_role('menuitem', name='1024 × 768', exact=True)
    await focus_command(page, item); await page.keyboard.press('Enter')
    until = time.monotonic() + 30
    while metrics['sizes'][-1] != [1024, 768]:
        if time.monotonic() >= until: raise AssertionError('Manual resolution did not supersede automatic mode.')
        await asyncio.sleep(.1)
    await asyncio.sleep(.6)
    before = metrics['layout_requests']
    await page.set_viewport_size(original)
    await asyncio.sleep(.8)
    if metrics['layout_requests'] != before or metrics['sizes'][-1] != [1024,768]:
        raise AssertionError('Manual resolution left automatic resize active.')
    passed('A manual resolution overrides automatic mode and later window resizing sends nothing')
