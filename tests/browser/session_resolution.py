"""Custom-resolution dialog validation and real server-confirmed pixels."""
import asyncio
import subprocess
import time
from playwright.async_api import expect
from driver import command, edit, focus_command

async def validate_custom_resolution(page, metrics, output, passed):
    async def open_editor():
        await command(page, 'Remote resolution')
        item = page.get_by_role('menuitem', name='Custom resolution…', exact=True)
        await focus_command(page, item)
        await page.keyboard.press('Enter')
        await expect(page.get_by_label('Remote width', exact=True)).to_be_visible(timeout=10000)

    sizes_before = list(metrics['sizes'])
    requests_before = metrics['layout_requests']
    await open_editor()
    await expect(page.get_by_label('Remote width', exact=True)).to_have_value(str(sizes_before[-1][0]))
    await expect(page.get_by_label('Remote height', exact=True)).to_have_value(str(sizes_before[-1][1]))
    await edit(page, 'Remote width', '1153')
    await edit(page, 'Remote height', '864')
    await command(page, 'Apply')
    await expect(page.get_by_label('Width must be an even number of pixels.', exact=True)).to_be_visible()
    await expect(page.get_by_label('Remote width', exact=True)).to_have_value('1153')
    if metrics['sizes'] != sizes_before or metrics['layout_requests'] != requests_before:
        raise AssertionError('An invalid custom resolution changed the remote desktop.')
    await edit(page, 'Remote width', '1152')
    before = metrics['surface_packets']
    await command(page, 'Apply')
    until = time.monotonic() + 30
    while metrics['sizes'][-1] != [1152, 864] or metrics['surface_packets'] <= before:
        if time.monotonic() >= until:
            raise AssertionError('Valid custom resolution was not confirmed by real server geometry and pixels.')
        await asyncio.sleep(.1)
    until = time.monotonic() + 15
    while True:
        picture = output / '09-custom-resolution.png'
        await page.screenshot(path=str(picture))
        check = subprocess.run(['python3', 'tests/browser/assert_pixels.py', str(picture)], capture_output=True, text=True)
        if check.returncode == 0:
            break
        if time.monotonic() >= until:
            raise AssertionError('The custom-sized remote desktop was not visibly presented.')
        await asyncio.sleep(.15)
    if metrics['layout_requests'] != requests_before + 1:
        raise AssertionError('Apply must send exactly one custom-resolution request.')
    passed('Custom resolution rejects odd widths, preserves editable input and presents server-confirmed 1152x864 pixels')
    await open_editor()
    await expect(page.get_by_label('Remote width', exact=True)).to_have_value('1152')
    await expect(page.get_by_label('Remote height', exact=True)).to_have_value('864')
    await edit(page, 'Remote width', '1280')
    unchanged = list(metrics['sizes'])
    requests_before = metrics['layout_requests']
    await command(page, 'Cancel')
    await expect(page.get_by_label('Remote width', exact=True)).not_to_be_visible()
    await asyncio.sleep(.3)
    if metrics['sizes'] != unchanged or metrics['layout_requests'] != requests_before:
        raise AssertionError('Cancelling the custom-resolution dialog sent a server resize.')
    passed('Custom resolution opens with actual dimensions and cancelling leaves the server unchanged')
