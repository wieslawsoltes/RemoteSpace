"""Compare the real session capture with fullscreen presentation and inverse input."""
import asyncio
from io import BytesIO
import json
import math
import time
from PIL import Image
from driver import command

async def validate_fullscreen_pixels(page, metrics, output, name, expected_inputs):
    async with page.expect_download(timeout=15000) as captured:
        await command(page, 'Capture')
    reference_path = output / (name + '.remote.png')
    await (await captured.value).save_as(str(reference_path))
    reference = Image.open(reference_path).convert('RGB')
    if list(reference.size) != metrics['sizes'][-1]:
        raise AssertionError('Fullscreen capture must retain the actual server resolution.')
    deadline = time.monotonic() + 15
    while True:
        surface = await page.get_by_label('Remote desktop', exact=True).bounding_box()
        if not surface:
            raise AssertionError('Fullscreen desktop has no accessible layout bounds.')
        scale = min(surface['width'] / reference.width, surface['height'] / reference.height)
        left = surface['x'] + (surface['width'] - reference.width * scale) / 2
        top = surface['y'] + (surface['height'] - reference.height * scale) / 2
        png = await page.screenshot()
        actual = Image.open(BytesIO(png)).convert('RGB')
        checked = matched = 0
        for ry in range(15, reference.height - 15, 37):
            for rx in range(15, reference.width - 15, 41):
                pixel = reference.getpixel((rx, ry))
                if any(reference.getpixel((rx + dx, ry + dy)) != pixel for dx in (-1, 0, 1) for dy in (-1, 0, 1)):
                    continue
                observed = actual.getpixel((round(left + (rx + .5) * scale), round(top + (ry + .5) * scale)))
                checked += 1
                if max(abs(a - b) for a, b in zip(pixel, observed)) <= 3:
                    matched += 1
        report = {'samples': checked, 'matching': matched, 'scale': scale, 'image_x': left, 'image_y': top}
        if checked >= 50 and matched >= checked * .97:
            (output / (name + '.pixels.png')).write_bytes(png)
            (output / (name + '.pixels.json')).write_text(json.dumps(report, indent=2))
            break
        if time.monotonic() >= deadline:
            raise AssertionError('Fullscreen semantic geometry disagrees with the actual remote pixels: ' + str(report))
        await asyncio.sleep(.1)
    if metrics['input_packets'] != expected_inputs:
        raise AssertionError('Fullscreen or Capture activation leaked keys into remote input.')

    # Observe the actual original-engine bridge pointer packet. Independent Xorg
    # effects are exercised separately by the main input suite.
    rx, ry = 127, 153
    before = metrics['input_packets']
    await page.mouse.move(left + (rx + .5) * scale, top + (ry + .5) * scale)
    deadline = time.monotonic() + 5
    while True:
        pointer = metrics.get('last_pointer')
        if metrics['input_packets'] > before and pointer and abs(pointer['X'] - rx) <= 1 and abs(pointer['Y'] - ry) <= 1:
            break
        if time.monotonic() >= deadline:
            raise AssertionError('Fullscreen pointer input does not correspond to the displayed remote pixel.')
        await asyncio.sleep(.05)
    # Leave the pointer on local toolbar chrome before resizing the window again;
    # that avoids counting layout-induced motion as an activation keystroke.
    await page.mouse.move(4, 4)
    await asyncio.sleep(.15)
    return metrics['input_packets']
