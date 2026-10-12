"""Real user-command tests for local-PC UX, import, themes, virtualization and conflicts."""
import asyncio
import json
import re
import uuid
from playwright.async_api import expect
from driver import boot, command, edit, navigate, focus_command
from saved_display import validate_saved_display_settings

async def validate_workspace_ui(page, output, passed):
    async def action(name, item):
        await expect(page.get_by_role('menuitem')).to_have_count(0)
        await command(page, 'Options for ' + name)
        target = page.get_by_role('menuitem', name=item, exact=True)
        await expect(target).to_be_visible()
        await focus_command(page, target)
        # This command can remove its own semantic node on keydown. A locator
        # retry risks replaying the command; keep one real keydown/keyup sequence.
        await page.keyboard.press('Enter', delay=80)
        await expect(page.get_by_role('menuitem')).to_have_count(0)
    await navigate(page, 'Devices')
    await command(page, 'Add PC')
    await edit(page, 'PC address', 'quick.test.invalid')
    await command(page, 'Add & connect')
    await expect(page.get_by_role('button', name='Connect to quick.test.invalid', exact=True)).to_be_attached(timeout=30000)
    passed('Minimal Add & connect persists the PC address as its name before diagnosing a missing bridge')
    await action('quick.test.invalid', 'View details')
    await expect(page.get_by_label('Device details', exact=True)).to_be_attached()
    await expect(page.get_by_label(re.compile('PC address: quick.test.invalid'))).to_be_attached()
    await page.screenshot(path=str(output/'03-device-details.png'))
    await command(page, 'Close Device details')
    await action('quick.test.invalid', 'Duplicate')
    await expect(page.get_by_role('button', name='Connect to quick.test.invalid (copy)', exact=True)).to_be_attached(timeout=30000)
    passed('Device details and duplicate commands work on actual saved profiles')
    async with page.expect_download(timeout=15000) as exported:
        await action('quick.test.invalid', 'Export .rdp')
    download = await exported.value
    await download.save_as(str(output/'exported-pc.rdp'))
    if 'full address:s:quick.test.invalid' not in (output/'exported-pc.rdp').read_text():
        raise AssertionError('Exported RDP file does not describe the chosen saved device')
    passed('Export command downloads an actual credential-free RDP profile')
    await command(page, 'List view')
    await expect(page.get_by_role('button', name='List view', exact=True)).to_have_attribute('aria-pressed', 'true')
    await page.evaluate('() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))')
    # Hold the real activation key through multiple presentation turns. The
    # newly opened editor must not consume its launching gesture as Save.
    await command(page, 'Options for quick.test.invalid (copy)')
    edit_item = page.get_by_role('menuitem', name='Edit', exact=True)
    await focus_command(page, edit_item)
    await page.keyboard.down('Enter')
    await page.wait_for_timeout(240)
    await page.keyboard.up('Enter')
    await expect(page.get_by_role('menuitem')).to_have_count(0)
    await expect(page.get_by_label('Friendly name', exact=True)).to_be_visible(timeout=15000)
    await expect(page.get_by_role('button', name='Save', exact=True)).to_be_attached()
    await edit(page, 'Friendly name', 'Build server')
    await edit(page, 'Group', 'Lab')
    save = page.get_by_role('button', name='Save', exact=True)
    await focus_command(page, save)
    await page.keyboard.down('Enter')
    await page.wait_for_timeout(240)
    await expect(save).to_be_visible()
    await page.keyboard.up('Enter')
    await expect(page.get_by_role('button', name='Connect to Build server', exact=True)).to_be_attached(timeout=30000)
    await expect(page.get_by_role('menuitem')).to_have_count(0)
    passed('Opening and saving Edit each consume their own held activation gesture without reopening a menu')
    await action('Build server', 'Delete')
    await command(page, 'Cancel')
    await expect(page.get_by_role('button', name='Connect to Build server', exact=True)).to_be_attached()
    passed('List view retains edit and delete-confirmation actions instead of being connect-only')
    groups = page.get_by_role('combobox', name='Filter by group', exact=True)
    await groups.press('Home'); await groups.press('ArrowDown'); await groups.press('Tab')
    await expect(page.get_by_role('button', name='Connect to Build server', exact=True)).to_be_attached()
    await expect(page.get_by_role('button', name='Connect to Test workstation', exact=True)).to_have_count(0)
    await edit(page, 'Search devices', 'no device matches')
    await expect(page.get_by_label('No matching devices', exact=True)).to_be_attached()
    await command(page, 'Clear filters')
    await expect(page.get_by_role('button', name='Connect to Test workstation', exact=True)).to_be_attached()
    passed('Group and text filters compose, with an actionable no-results state')

    # A second real application instance loads a stale revision, not a fake store.
    other = await page.context.new_page()
    try:
        await boot(other, page.url)
        await page.bring_to_front()
        await action('Build server', 'Duplicate')
        await expect(page.get_by_role('button', name='Connect to Build server (copy)', exact=True)).to_be_attached(timeout=30000)
        await other.bring_to_front()
        await command(other, 'Toggle favorite Test workstation')
        await expect(other.get_by_text(re.compile('Saved devices changed in another window'))).to_be_attached(timeout=15000)
        await command(other, 'Refresh devices')
        await expect(other.get_by_role('button', name='Connect to Build server (copy)', exact=True)).to_be_attached(timeout=30000)
        passed('Cross-window conflicts preserve saved data and Refresh recovers without a page reload')
    finally:
        await other.close(); await page.bring_to_front()
    await navigate(page, 'Settings')
    theme = page.get_by_role('combobox', name='App theme', exact=True)
    await theme.press('Home'); await theme.press('ArrowDown'); await theme.press('ArrowDown'); await theme.press('Tab')
    await navigate(page, 'Devices'); await command(page, 'Grid view')
    await page.screenshot(path=str(output/'04-dark-devices.png'))
    await boot(page)
    await navigate(page, 'Settings')
    theme = page.get_by_role('combobox', name='App theme', exact=True)
    # Verify the persisted preference through the real export command. This host
    # exposes the ComboBox name but not SelectedItem as inner DOM text.
    async with page.expect_download(timeout=15000) as saved_theme:
        await command(page, 'Export saved devices')
    await (await saved_theme.value).save_as(str(output/'theme-workspace.json'))
    if json.loads((output/'theme-workspace.json').read_text())['Settings']['Theme'] != 2:
        raise AssertionError('Dark theme did not survive reload in the exported workspace')
    await theme.press('Home'); await theme.press('ArrowDown'); await theme.press('Tab')
    await navigate(page, 'Devices')
    await page.screenshot(path=str(output/'05-light-devices.png'))
    await page.set_viewport_size({'width':640,'height':900})
    await page.wait_for_timeout(300)
    await page.screenshot(path=str(output/'06-narrow-devices.png'))
    await expect(page.get_by_role('button', name='Add PC', exact=True)).to_be_attached()
    await page.set_viewport_size({'width':1440,'height':1000})
    passed('Live light/dark preferences survive reload and the workspace adapts to a narrow window')

    await validate_saved_display_settings(page, output, passed)

    # Import a real user-selected workspace file to exercise the bounded bulk path.
    devices = [{'Profile': {'Id':str(uuid.uuid4()), 'Name':f'Benchmark {i:04}', 'Host':f'pc-{i}.test.invalid', 'Group':'Load'}} for i in range(1000)]
    imported = json.dumps({'SchemaVersion':1,'Devices':devices}).encode()
    await navigate(page,'Settings')
    async with page.expect_file_chooser(timeout=15000) as choice:
        await command(page,'Import saved devices')
    await (await choice.value).set_files({'name':'load-workspace.json','mimeType':'application/json','buffer':imported})
    await command(page,'Import')
    await navigate(page,'Devices')
    await command(page,'Grid view')
    await edit(page,'Search devices','Benchmark')
    await expect(page.get_by_label('Saved devices (1000)',exact=True)).to_be_attached(timeout=30000)
    buttons = page.get_by_role('button', name=re.compile('^Connect to Benchmark '))
    await expect(buttons.first).to_be_attached(timeout=30000)
    realized = await buttons.count()
    if not 0 < realized < 100: raise AssertionError(f'Expected bounded grid realization, saw {realized} connect controls for 1000 devices')
    await edit(page,'Search devices','Benchmark 0999')
    await expect(page.get_by_role('button',name='Connect to Benchmark 0999',exact=True)).to_be_attached(timeout=30000)
    passed(f'Actual file import adds 1000 profiles; grid realizes {realized} connect controls and searches the last device')
    await command(page,'List view')
    await edit(page,'Search devices','Benchmark')
    await expect(buttons.first).to_be_attached(timeout=30000)
    realized = await buttons.count()
    if not 0 < realized < 100: raise AssertionError('The list view did not virtualize the imported catalog')
    await command(page,'Reverse sort order')
    await expect(page.get_by_role('button',name='Connect to Benchmark 0999',exact=True)).to_be_attached(timeout=30000)
    await page.screenshot(path=str(output/'07-large-catalog.png'))
    passed(f'List virtualization and reverse sorting work with 1000 profiles ({realized} realized controls)')
