"""Full Uno toolbar collapse: real commands, retained pixels and inverse input."""
import asyncio
import json
import time
from playwright.async_api import expect
from driver import command, focus_command
from fullscreen_pixels import validate_fullscreen_pixels

async def validate_toolbar_collapse(page, metrics, output, passed, expected_inputs, reference):
    sizes = list(metrics['sizes'])
    layouts = metrics['layout_requests']
    desktop = page.get_by_label('Remote desktop', exact=True)
    identity = await desktop.get_attribute('id')
    if not identity:
        raise AssertionError('The live desktop must have a semantic identity before collapse.')
    original = await desktop.bounding_box()
    # A real Capture download made just before this test is the pixel oracle.
    # Repeated local toolbar changes must not consume more retained export URLs.

    async def geometry(collapsed):
        deadline = time.monotonic() + 10
        while True:
            bounds = await desktop.bounding_box()
            size = await page.evaluate('({width:innerWidth,height:innerHeight})')
            good = bounds and (all(abs(bounds[key] - original[key]) <= 1 for key in original) if not collapsed else
                abs(bounds['x']) <= 1 and abs(bounds['y']) <= 1 and
                abs(bounds['width'] - size['width']) <= 1 and abs(bounds['height'] - size['height']) <= 1)
            if good:
                return bounds
            if time.monotonic() > deadline:
                raise AssertionError('Toolbar transition has incorrect retained desktop geometry: '+json.dumps({'collapsed':collapsed,'before':original,'after':bounds,'window':size}))
            await asyncio.sleep(.05)

    for iteration in range(2):
        collapse = page.get_by_role('button', name='Collapse connection toolbar', exact=True)
        await focus_command(page, collapse)
        if iteration == 0:
            # Hold the native gesture while the command waits to remove its
            # own focused subtree. It must neither collapse early nor leak keys.
            await page.keyboard.down('Enter')
            await page.wait_for_timeout(240)
            await expect(collapse).to_be_visible()
            await expect(page.get_by_role('button', name='Expand connection toolbar', exact=True)).to_have_count(0)
            await page.keyboard.up('Enter')
        else:
            await page.keyboard.press('Space')
        expand = page.get_by_role('button', name='Expand connection toolbar', exact=True)
        await expect(expand).to_be_visible()
        await expect(page.get_by_label('Connection toolbar buttons', exact=True)).to_have_count(0)
        for name in ('Fit', 'Display settings', 'Remote resolution', 'Clipboard', 'Keyboard', 'Session actions', 'Exit full screen'):
            await expect(page.get_by_role('button', name=name, exact=True)).to_have_count(0)
        await geometry(True)
        if await desktop.get_attribute('id') != identity:
            raise AssertionError('Collapsing the toolbar recreated the live desktop semantic owner.')
        reveal = await expand.bounding_box()
        size = await page.evaluate('({width:innerWidth,height:innerHeight})')
        if not reveal or abs(reveal['x'] + reveal['width']/2 - size['width']/2) > 1 or reveal['y'] < 0 or reveal['y'] > 1 or reveal['width'] > 80 or reveal['height'] > 48:
            raise AssertionError('Collapsed toolbar must have a compact top-centred, in-window reveal target: '+str(reveal))
        expected_inputs = await validate_fullscreen_pixels(page, metrics, output, 'toolbar-collapsed-'+str(iteration), expected_inputs,
            reference_path=reference, park_pointer=(reveal['x']+reveal['width']/2, reveal['y']+reveal['height']/2))
        await focus_command(page, expand)
        await page.keyboard.press('Space' if iteration == 0 else 'Enter')
        await expect(collapse).to_be_visible()
        await expect(expand).to_have_count(0)
        await geometry(False)
        if await desktop.get_attribute('id') != identity:
            raise AssertionError('Expanding the toolbar recreated the live desktop semantic owner.')
        # Validate the real restored raster and pointer mapping, not only bounds.
        expected_inputs = await validate_fullscreen_pixels(page, metrics, output, 'toolbar-expanded-'+str(iteration), expected_inputs, reference_path=reference)
    if metrics['sizes'] != sizes or metrics['layout_requests'] != layouts:
        raise AssertionError('Toolbar-only changes must not reconnect or change server resolution.')
    passed('Fullscreen toolbar collapse fills the window with the same remote desktop and restores exact geometry and pointer mapping')
    passed('Held Enter and Space toolbar commands remain local; collapsed commands have no invisible peers and reveal is keyboard accessible')
    return expected_inputs
