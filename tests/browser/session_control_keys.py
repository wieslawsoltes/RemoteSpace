"""Actual Uno control shortcuts: owned keys, live fullscreen, retained remote pixels."""
import asyncio
import json
import re
import subprocess
import time
from playwright.async_api import expect
from driver import command, focus_command
from fullscreen_pixels import validate_fullscreen_pixels

async def validate_session_control_keys(page, metrics, output, passed, reference):
    desktop = page.get_by_label('Remote desktop', exact=True)
    original = await desktop.bounding_box()
    sizes, layouts = list(metrics['sizes']), metrics['layout_requests']
    expected = metrics['input_packets']

    def input_log():
        return subprocess.run(['sudo', 'cat', '/home/rdptest/input-events.log'],
                              check=True, capture_output=True, text=True, timeout=3).stdout

    async def drained(count, start, name):
        deadline = time.monotonic() + 10
        while metrics['input_packets'] < count:
            if time.monotonic() > deadline:
                raise AssertionError('Local shortcut did not balance its already-forwarded modifier keys.')
            await asyncio.sleep(.05)
        if metrics['input_packets'] != count:
            raise AssertionError('Local Home/Pause or repeated shortcut keys leaked to RDP: ' + str(metrics['input_packets']-count))
        # Check the independent remote input consumer too: four packets alone
        # could be the wrong keys. This fixture's Xorg Ctrl_L/Alt_L are 37/64.
        # Release ordering is not prescribed, but each make must have its own
        # later break and no Home/Pause or duplicate key may appear.
        deadline = time.monotonic() + 10
        while True:
            events = [(int(kind), int(code)) for kind, code in re.findall(
                r'EVENT type (13|14) \(RawKey(?:Press|Release)\).*?detail:\s*(\d+)', input_log()[start:], re.S)]
            if len(events) >= 4:
                if events[:2] != [(13, 37), (13, 64)] or sorted(events[2:]) != [(14, 37), (14, 64)]:
                    raise AssertionError('Unexpected remote shortcut keys: ' + str(events))
                break
            if time.monotonic() >= deadline:
                raise AssertionError('Remote Xorg did not observe balanced shortcut modifiers: ' + str(events))
            await asyncio.sleep(.05)
        (output / (name + '.modifiers.json')).write_text(json.dumps(events, indent=2))


    # The Ctrl/Alt makes precede the shortcut. Its local action queues their
    # matching releases. Home/Pause itself must never enter the remote queue.
    await focus_command(page, desktop)
    remote_start = len(input_log())
    await page.keyboard.press('Control+Alt+Pause')
    await page.wait_for_function('document.fullscreenElement === document.documentElement')
    await expect(page.get_by_role('button', name='Exit full screen', exact=True)).to_be_visible()
    expected += 4
    await drained(expected, remote_start, '23-shortcut-fullscreen')
    expected = await validate_fullscreen_pixels(page, metrics, output, '23-shortcut-fullscreen', expected, reference_path=reference)
    passed('Ctrl+Alt+Pause uses the real fullscreen API and balances remote modifiers without forwarding Pause')

    await command(page, 'Collapse connection toolbar')
    await expect(page.get_by_role('button', name='Expand connection toolbar', exact=True)).to_be_visible()
    await focus_command(page, desktop)
    remote_start = len(input_log())
    await page.keyboard.down('Control'); await page.keyboard.down('Alt'); await page.keyboard.down('Home')
    fit = page.get_by_role('button', name='Fit', exact=True)
    await expect(fit).to_be_visible()
    await expect(fit).to_be_focused()
    await page.keyboard.up('Alt'); await page.keyboard.up('Control')
    for _ in range(3): await page.keyboard.down('Home')
    await page.keyboard.up('Home')
    await expect(fit).to_be_focused()
    await expect(page.get_by_role('button', name='Expand connection toolbar', exact=True)).to_have_count(0)
    expected += 4
    await drained(expected, remote_start, '24-shortcut-connection-bar')
    await page.screenshot(path=str(output/'24-shortcut-connection-bar.png'))
    passed('Ctrl+Alt+Home reveals collapsed commands and focuses the first local button; held repeats never enter the remote session')

    await focus_command(page, desktop)
    remote_start = len(input_log())
    await page.keyboard.press('Control+Alt+Pause')
    await page.wait_for_function('document.fullscreenElement === null')
    await expect(page.get_by_role('button', name='Full screen', exact=True)).to_be_visible()
    expected += 4
    await drained(expected, remote_start, '25-shortcut-windowed')
    deadline = time.monotonic() + 10
    while True:
        bounds = await desktop.bounding_box()
        if bounds and all(abs(bounds[key]-original[key]) <= 1 for key in original): break
        if time.monotonic() > deadline:
            raise AssertionError('Shortcut exit changed windowed geometry: '+json.dumps({'before':original,'after':bounds}))
        await asyncio.sleep(.05)
    await validate_fullscreen_pixels(page, metrics, output, '25-shortcut-windowed', expected, reference_path=reference)
    if metrics['sizes'] != sizes or metrics['layout_requests'] != layouts:
        raise AssertionError('A local session shortcut reconnected or resized the remote server.')
    passed('Shortcut exit restores exact windowed pixels and pointer mapping without reconnecting or requesting server layouts')
