"""Real Uno fullscreen disconnect confirmation, cancellation and disposal."""
from playwright.async_api import expect
from driver import command, focus_command

async def validate_fullscreen_close(page, metrics, output, passed):
    sizes = list(metrics['sizes'])
    layouts = metrics['layout_requests']
    inputs = metrics['input_packets']
    await command(page, 'Full screen')
    await page.wait_for_function('document.fullscreenElement === document.documentElement')
    await expect(page.get_by_role('button', name='Exit full screen', exact=True)).to_be_visible()

    async def disconnect_dialog():
        await command(page, 'Session actions')
        item = page.get_by_role('menuitem', name='Disconnect', exact=True)
        await focus_command(page, item)
        await page.keyboard.press('Enter')
        await expect(page.get_by_role('menuitem')).to_have_count(0)
        await expect(page.get_by_role('dialog')).to_be_visible()
        await expect(page.get_by_role('button', name='Disconnect', exact=True)).to_be_visible()

    await disconnect_dialog()
    await command(page, 'Cancel')
    await expect(page.get_by_role('dialog')).to_have_count(0)
    await page.wait_for_function('document.fullscreenElement === document.documentElement')
    host = page.get_by_label('Full-screen remote session', exact=True)
    await expect(host.get_by_label('Remote desktop', exact=True)).to_be_visible()
    await expect(host.get_by_label('Active · 1280 × 720 · 150%', exact=True)).to_be_visible()
    await page.screenshot(path=str(output/'21-fullscreen-disconnect-cancelled.png'))
    if metrics['sizes'] != sizes or metrics['layout_requests'] != layouts or metrics['input_packets'] != inputs:
        raise AssertionError('Cancelling disconnect changed the remote layout, reconnected, or leaked local activation input.')
    passed('Cancelling fullscreen disconnect preserves the same active session without remote input or layout changes')

    await disconnect_dialog()
    await command(page, 'Disconnect')
    await expect(page.get_by_role('dialog')).to_have_count(0)
    await page.wait_for_function('document.fullscreenElement === null')
    await expect(page.get_by_role('tab', name='Loopback desktop', exact=True)).to_have_count(0, timeout=15000)
    await expect(page.get_by_label('Remote desktop', exact=True)).to_have_count(0)
    await expect(page.get_by_role('button', name='Exit full screen', exact=True)).to_have_count(0)
    await expect(page.get_by_role('button', name='About RemoteSpace', exact=True)).to_be_visible()
    await expect(page.get_by_role('option', name='Devices', exact=True)).to_be_visible()
    await page.screenshot(path=str(output/'22-fullscreen-disconnected-workspace.png'))
    passed('Confirmed fullscreen disconnect drains the session, exits platform fullscreen and restores the workspace with no retained desktop peer')
