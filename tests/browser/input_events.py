"""Physical browser input -> real Uno handlers -> RDP -> independent X input log."""
import asyncio
import re
import subprocess
import time

async def validate_extended_input(page, metrics, passed):
    offered = [m.get('InputCapabilities') for m in metrics['server_controls'] if m.get('Type') == 'input-capabilities']
    if not any(c and c.get('ExtendedMouse') and c.get('HorizontalWheel') for c in offered):
        raise AssertionError('The configured fixture did not advertise extended mouse and horizontal wheel input.')
    surface = await page.get_by_label('Remote desktop', exact=True).bounding_box()
    if not surface or surface['width'] < 100 or surface['height'] < 100:
        raise AssertionError('The actual remote surface has no usable hit-test area.')
    x, y = surface['x'] + surface['width'] * .55, surface['y'] + surface['height'] * .55
    cdp = await page.context.new_cdp_session(page)
    try:
        await cdp.send('Input.dispatchMouseEvent', {'type':'mouseMoved','x':x,'y':y})
        for button, buttons in [('back',8),('forward',16)]:
            await cdp.send('Input.dispatchMouseEvent', {'type':'mousePressed','x':x,'y':y,'button':button,'buttons':buttons,'clickCount':1})
            await cdp.send('Input.dispatchMouseEvent', {'type':'mouseReleased','x':x,'y':y,'button':button,'buttons':0,'clickCount':1})
        for delta in [120,-120]:
            await cdp.send('Input.dispatchMouseEvent', {'type':'mouseWheel','x':x,'y':y,'deltaX':delta,'deltaY':0})
            await asyncio.sleep(.15)
        deadline = time.monotonic() + 15
        while True:
            data = subprocess.run(['sudo','cat','/home/rdptest/input-events.log'],check=True,capture_output=True,text=True,timeout=3).stdout
            presses = set(re.findall(r'EVENT type 15 \(RawButtonPress\).*?detail: (\d+)', data, re.S))
            releases = set(re.findall(r'EVENT type 16 \(RawButtonRelease\).*?detail: (\d+)', data, re.S))
            if {'6','7','8','9'} <= presses and {'6','7','8','9'} <= releases:
                break
            if time.monotonic() >= deadline:
                raise AssertionError(f'Extended browser input did not reach Xorg: press={sorted(presses)}, release={sorted(releases)}')
            await asyncio.sleep(.2)
        passed('Browser back/forward buttons and both horizontal wheel directions reach independent remote X input events')
    finally:
        await cdp.detach()
