"""Consent-based Unicode clipboard round trips against the real XRDP/Xorg fixture."""
import asyncio
import subprocess
import time
from driver import command

XCLIP = ['sudo','-u','rdptest','env','DISPLAY=:10','XAUTHORITY=/home/rdptest/.Xauthority','xclip','-selection','clipboard']

async def round_trip(page, origin, passed):
    await page.context.grant_permissions(['clipboard-read','clipboard-write'], origin=origin)
    local_text = 'RemoteSpace browser → Żółć 日本語 🙂'
    await page.evaluate('(text) => navigator.clipboard.writeText(text)', local_text)
    await command(page, 'Send clipboard')
    until = time.monotonic() + 15
    while True:
        result = subprocess.run(XCLIP + ['-o'], capture_output=True, timeout=3)
        if result.returncode == 0 and result.stdout.decode('utf-8') == local_text:
            break
        if time.monotonic() >= until:
            raise AssertionError('Browser Unicode clipboard text did not arrive in the remote X selection.')
        await asyncio.sleep(.2)
    passed('Explicit browser clipboard command transfers exact Unicode text to the real remote clipboard')
    remote_text = 'RemoteSpace remote → Ελληνικά العربية 🙂'
    owner = subprocess.Popen(XCLIP + ['-i','-quiet'], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        owner.stdin.write(remote_text.encode('utf-8')); owner.stdin.close()
        until = time.monotonic() + 15
        while True:
            await command(page, 'Copy remote text')
            actual = await page.evaluate('() => navigator.clipboard.readText()')
            if actual == remote_text:
                break
            if time.monotonic() >= until:
                raise AssertionError('The real remote Unicode clipboard did not reach the browser copy command.')
            await asyncio.sleep(.2)
        passed('Explicit copy command transfers exact remote Unicode text into the browser clipboard')
    finally:
        owner.terminate()
        try: owner.wait(timeout=5)
        except subprocess.TimeoutExpired: owner.kill(); owner.wait()
