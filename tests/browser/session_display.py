"""Actual Uno menus, local scrollbars and original remote-raster comparisons.
The reference is downloaded with the user-facing Capture command from a real
RDP session. No test frame or private managed entry point is used.
"""
import asyncio
from io import BytesIO
import json
import math
import subprocess
import time
from PIL import Image
from playwright.async_api import expect
from driver import command, navigate

async def validate_session_display(page, metrics, output, passed):
    sizes_before = list(metrics['sizes'])
    # The viewport's owning layout row must survive semantic-tree pruning.
    # Otherwise a later ancestor offset leaves stale absolute child bounds.
    host = page.get_by_label('Active remote session', exact=True)
    await expect(host).to_be_visible(timeout=10000)
    await expect(host.get_by_label('Remote desktop', exact=True)).to_be_visible()

    async with page.expect_download(timeout=15000) as captured:
        await command(page, 'Capture')
    reference_path = output / 'remote-reference.png'
    await (await captured.value).save_as(str(reference_path))
    reference = Image.open(reference_path).convert('RGB')
    if list(reference.size) != metrics['sizes'][-1]:
        raise AssertionError('Capture dimensions do not match the last actual server desktop.')
    passed('Capture downloads the original remote-resolution raster rather than a zoomed screenshot')
    await command(page, '200%')
    horizontal = page.get_by_label('Horizontal desktop position', exact=True)
    vertical = page.get_by_label('Vertical desktop position', exact=True)
    await expect(horizontal).to_be_visible(timeout=10000)
    await expect(vertical).to_be_visible(timeout=10000)

    async def compare_view(name, bottom_right):
        until = time.monotonic() + 10
        while True:
            surface = await page.get_by_label('Remote desktop', exact=True).bounding_box()
            hbar, vbar = await horizontal.bounding_box(), await vertical.bounding_box()
            if not surface or not hbar or not vbar:
                raise AssertionError('The actual zoomed view or local scrollbars have no layout bounds.')
            width = vbar['x'] - surface['x']
            height = hbar['y'] - surface['y']
            if width <= 0 or height <= 0:
                raise AssertionError('Local scrollbar geometry does not leave a usable remote viewport.')
            ox = max(0, reference.width - width / 2) if bottom_right else 0
            oy = max(0, reference.height - height / 2) if bottom_right else 0
            png = await page.screenshot()
            actual = Image.open(BytesIO(png)).convert('RGB')
            checked = matched = 0
            # Exclude high-frequency text/caret pixels. Flat reference regions
            # still distinguish the remote root and terminal at both pan limits.
            for sy in range(24, int(height) - 24, 47):
                for sx in range(24, int(width) - 24, 43):
                    rx, ry = math.floor(ox + sx / 2), math.floor(oy + sy / 2)
                    if not (1 <= rx < reference.width - 1 and 1 <= ry < reference.height - 1):
                        continue
                    target = reference.getpixel((rx, ry))
                    if any(reference.getpixel((rx + dx, ry + dy)) != target for dx in (-1, 0, 1) for dy in (-1, 0, 1)):
                        continue
                    pixel = actual.getpixel((round(surface['x'] + sx), round(surface['y'] + sy)))
                    checked += 1
                    if max(abs(a - b) for a, b in zip(target, pixel)) <= 3:
                        matched += 1
            report = {'samples': checked, 'matching': matched, 'offset_x': ox, 'offset_y': oy,
                      'viewport_width': width, 'viewport_height': height, 'scale': 2}
            if checked >= 20 and matched >= checked * .97:
                (output / name).write_bytes(png)
                (output / (name + '.json')).write_text(json.dumps(report, indent=2))
                return surface, width, height, ox, oy
            if time.monotonic() >= until:
                raise AssertionError('Zoomed presentation disagrees with the actual remote capture: ' + str(report))
            await asyncio.sleep(.15)

    await command(page, 'Top left')
    await compare_view('05-zoom-top-left.png', False)
    # The same keyboard keys must operate the local bars, not remote applications.
    await asyncio.sleep(.2)
    input_before = metrics['input_packets']
    await horizontal.focus(); await page.keyboard.press('End')
    await vertical.focus(); await page.keyboard.press('End')
    surface, width, height, ox, oy = await compare_view('06-zoom-bottom-right.png', True)
    await asyncio.sleep(.2)
    if metrics['input_packets'] != input_before:
        raise AssertionError('Local scrollbar keyboard input escaped into the remote session.')
    passed('Local zoom and keyboard scrollbars expose exact remote pixels at both pan limits without sending remote keystrokes')
    # Observe an actual pointer packet through the bridge after panning. The
    # existing independent Xorg input tests separately check remote input effects.
    x, y = min(width - 20, 73), min(height - 20, 89)
    marker = metrics['input_packets']
    await page.mouse.move(surface['x'] + x, surface['y'] + y)
    expected_x, expected_y = int(ox + x / 2), int(oy + y / 2)
    until = time.monotonic() + 5
    while True:
        pointer = metrics.get('last_pointer')
        if metrics['input_packets'] > marker and pointer and abs(pointer['X'] - expected_x) <= 1 and abs(pointer['Y'] - expected_y) <= 1:
            break
        if time.monotonic() >= until:
            raise AssertionError('Pointer packet did not use the visible zoomed/panned remote coordinates.')
        await asyncio.sleep(.05)
    if metrics['sizes'] != sizes_before:
        raise AssertionError('A local-only zoom or pan changed the server desktop resolution.')
    passed('Zoomed pointer coordinates match the displayed remote pixels without renegotiating server resolution')
    await command(page, 'Fit')
    await expect(horizontal).not_to_be_visible(timeout=10000)
    await expect(vertical).not_to_be_visible(timeout=10000)
    viewport = page.viewport_size
    try:
        await page.set_viewport_size({'width': 640, 'height': 900})
        await page.wait_for_timeout(300)
        for name in ('Fit', 'Display settings', 'Remote resolution', 'Clipboard', 'Session actions', 'Full screen'):
            button = page.get_by_role('button', name=name, exact=True)
            await expect(button).to_be_visible()
            bounds = await button.bounding_box()
            if not bounds or bounds['x'] < 0 or bounds['x'] + bounds['width'] > 640:
                raise AssertionError('A primary connection command is clipped at narrow width: ' + name)
        await page.screenshot(path=str(output / '07-compact-connection-bar.png'))
    finally:
        await page.set_viewport_size(viewport)
    passed('All six primary connection commands remain visible in a narrow window')
    # An independent server change while the view is hidden must be retained and
    # shown on return. Presentation suppression is not network/RDP suppression.
    async def background(color):
        subprocess.run(['sudo', '-u', 'rdptest', 'env', 'DISPLAY=:10', 'XAUTHORITY=/home/rdptest/.Xauthority',
                        'xsetroot', '-solid', color], check=True, timeout=5)
    await navigate(page, 'Devices')
    try:
        before = metrics['surface_packets']
        await background('#345267')
        until = time.monotonic() + 10
        while metrics['surface_packets'] <= before:
            if time.monotonic() >= until:
                raise AssertionError('Hidden session stopped retaining actual remote desktop updates.')
            await asyncio.sleep(.1)
        await navigate(page, 'Active sessions')
        await expect(page.get_by_role('tab', name='Loopback desktop', exact=True)).to_be_visible()
        await expect(page.get_by_role('button', name='Close Tab', exact=True)).to_be_visible()
        until = time.monotonic() + 10
        while True:
            await page.screenshot(path=str(output / '08-hidden-session-catchup.png'))
            check = subprocess.run(['python3', 'tests/browser/assert_pixels.py', str(output / '08-hidden-session-catchup.png'), '345267'], capture_output=True, text=True)
            if check.returncode == 0:
                break
            if time.monotonic() >= until:
                raise AssertionError('Reactivated session did not visibly present the retained remote change.')
            await asyncio.sleep(.15)
    finally:
        await background('#14283c')
        await command(page, 'Fit')
    passed('A hidden session retains server updates and presents them when selected again')
