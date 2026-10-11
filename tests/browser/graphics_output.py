"""Exercise actual Uno commands and observe the independent remote server output."""
import asyncio
import subprocess
import time
from driver import command
from playwright.async_api import expect

async def validate_output_controls(page, metrics, output, passed):
    async def diagnostic(message):
        until = time.monotonic() + 15
        while not any(c.get('Stage') == 'GraphicsOutput' and c.get('Message') == message for c in metrics['server_controls']):
            if time.monotonic() >= until:
                raise AssertionError('The RDP engine did not confirm sending the graphics-output request.')
            await asyncio.sleep(.1)
    def background(color):
        subprocess.run(['sudo','-u','rdptest','env','DISPLAY=:10','XAUTHORITY=/home/rdptest/.Xauthority',
                        'xsetroot','-solid',color], check=True, timeout=5)
    await expect(page.get_by_role('button', name='Pause updates', exact=True)).to_be_enabled(timeout=10000)
    await command(page, 'Pause updates')
    await diagnostic('Graphics output pause requested.')
    # Drain already-in-flight loopback output before changing the independent server.
    quiet = time.monotonic(); until = quiet + 10; previous = metrics['surface_packets']
    while time.monotonic() - quiet < .6:
        await asyncio.sleep(.1)
        if metrics['surface_packets'] != previous:
            previous = metrics['surface_packets']; quiet = time.monotonic()
        if time.monotonic() > until:
            raise AssertionError('Server output did not become quiescent after suppression.')
    before = metrics['surface_packets']
    try:
        background('#284c65')
        await asyncio.sleep(.6)
        if metrics['surface_packets'] != before:
            raise AssertionError('Suppressed server still sent bitmap updates for a remote desktop change.')
        passed('Pause updates suppresses bitmap traffic for an independently changed remote desktop')
        await command(page, 'Resume updates')
        await diagnostic('Graphics output resume requested.')
        until = time.monotonic() + 15
        while True:
            await page.screenshot(path=str(output/'03-resumed-desktop.png'))
            check = subprocess.run(['python3','tests/browser/assert_pixels.py',str(output/'03-resumed-desktop.png'),'284c65'],capture_output=True,text=True)
            if check.returncode == 0 and metrics['surface_packets'] > before: break
            if time.monotonic() > until:
                raise AssertionError('Resume did not visibly present the independently changed server background.')
            await asyncio.sleep(.2)
        passed('Resume updates presents server-side changes made while output was suppressed')
        await command(page, 'Refresh desktop')
        await diagnostic('Desktop refresh requested.')
        passed('Refresh desktop sends the negotiated native RDP refresh request')
    finally:
        background('#14283c')
