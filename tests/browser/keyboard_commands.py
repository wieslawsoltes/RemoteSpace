"""User-facing key commands -> original RDP engine -> independent Xorg input log.
No DOM value/provider writes, runtime test mode or alternate RDP client is used.
"""
import asyncio
import json
import re
import subprocess
import time
from playwright.async_api import expect
from driver import command, focus_command

COMMANDS = ['Windows Security', 'Task Manager', 'Start menu', 'Next application',
            'Previous application', 'Window menu', 'Print Screen', 'Active window screenshot', 'Escape']

async def validate_keyboard_commands(page, metrics, output, passed):
    async def open_menu():
        await command(page, 'Keyboard')
        await expect(page.get_by_role('menuitem')).to_have_count(9)
        for label in COMMANDS:
            await expect(page.get_by_role('menuitem', name=label, exact=True)).to_be_visible()

    async def execute(label):
        await open_menu()
        item = page.get_by_role('menuitem', name=label, exact=True)
        await focus_command(page, item)
        await page.keyboard.press('Enter')
        await expect(page.get_by_role('menuitem')).to_have_count(0)

    def input_log():
        return subprocess.run(['sudo', 'cat', '/home/rdptest/input-events.log'],
                              check=True, capture_output=True, text=True, timeout=3).stdout

    await open_menu()
    await page.screenshot(path=str(output / '15-keyboard-menu.png'))
    await page.keyboard.press('Escape')
    await expect(page.get_by_role('menuitem')).to_have_count(0)
    passed('Keyboard toolbar menu exposes nine named remote commands with shortcut hints')

    # Use ordinary switching chords against the Xorg test desktop. Windows-only
    # actions are codec-tested, not claimed to be Windows-qualified by this fixture.
    for label, expected in [
        ('Next application', [(13, 64), (13, 23), (14, 23), (14, 64)]),
        ('Previous application', [(13, 64), (13, 50), (13, 23), (14, 23), (14, 50), (14, 64)])]:
        before = len(input_log()); input_before = metrics['input_packets']
        await execute(label)
        until = time.monotonic() + 10
        while True:
            events = [(int(kind), int(code)) for kind, code in re.findall(
                r'EVENT type (13|14) \(RawKey(?:Press|Release)\).*?detail:\s*(\d+)', input_log()[before:], re.S)]
            if expected in [events[i:i + len(expected)] for i in range(len(events) - len(expected) + 1)]:
                break
            if time.monotonic() >= until:
                raise AssertionError(f'{label}: complete ordered make/break chord not observed by remote Xorg: {events}')
            await asyncio.sleep(.1)
        await asyncio.sleep(.15)
        if metrics['input_packets'] - input_before != len(expected):
            raise AssertionError('The menu activation escaped into remote input or the command was sent more than once.')
        (output / (label.replace(' ', '-') + '.events.json')).write_text(json.dumps(events, indent=2))
    passed('Keyboard commands reach remote Xorg in exact balanced order with no duplicate activation keys')

    await command(page, 'Pause updates')
    # Wait for the UI acknowledgement, rather than assuming a sent request changed state.
    await expect(page.get_by_label('Remote desktop', exact=True)).to_be_disabled(timeout=10000)
    await open_menu()
    for label in COMMANDS:
        await expect(page.get_by_role('menuitem', name=label, exact=True)).to_be_disabled()
    await page.screenshot(path=str(output / '16-keyboard-paused.png'))
    await page.keyboard.press('Escape')
    await command(page, 'Resume updates')
    await expect(page.get_by_label('Remote desktop', exact=True)).to_be_enabled(timeout=10000)
    passed('All keyboard commands are unavailable while remote graphics are paused and recover on resume')

    old_size = page.viewport_size
    try:
        await page.set_viewport_size({'width': 640, 'height': 900})
        await page.wait_for_timeout(300)
        bounds = []
        for label in ['Fit', 'Display settings', 'Remote resolution', 'Clipboard', 'Keyboard', 'Session actions', 'Full screen']:
            button = page.get_by_role('button', name=label, exact=True)
            await expect(button).to_be_visible()
            box = await button.bounding_box()
            if not box or box['x'] < 0 or box['x'] + box['width'] > 640:
                raise AssertionError('A session toolbar command is clipped at narrow width: ' + label)
            bounds.append(box)
        if any(a['x'] + a['width'] > b['x'] for a, b in zip(bounds, bounds[1:])):
            raise AssertionError('Session toolbar hit targets overlap.')
        await open_menu()
        await page.screenshot(path=str(output / '17-compact-keyboard-menu.png'))
        await page.keyboard.press('Escape')
    finally:
        await page.set_viewport_size(old_size)
    passed('The keyboard menu and all seven distinct toolbar commands remain usable in a narrow session window')
