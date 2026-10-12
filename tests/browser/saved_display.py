"""Per-PC display settings through real editor, persistence and export commands."""
import json
from playwright.async_api import expect
from driver import boot, command, focus_command, navigate

async def settings_section(page, name):
    tab = page.get_by_role('tab', name=name, exact=True)
    await expect(tab).to_be_visible(timeout=15000)
    await tab.click(force=True)
    await page.evaluate('() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))')

async def fixed_local_zoom(page):
    await settings_section(page, 'Display')
    fit = page.get_by_role('checkbox', name='Fit session to window', exact=True)
    zoom = page.get_by_role('combobox', name='Local zoom', exact=True)
    await expect(fit).to_be_checked(timeout=15000)
    await expect(zoom).to_be_disabled()
    await focus_command(page, fit)
    await page.keyboard.press('Space')
    await expect(fit).not_to_be_checked()
    await expect(zoom).to_be_enabled()
    # Default profile choices are ordered 100%, 125%, 150%, ... . Keyboard
    # selection uses the same native ComboBox path as the app's other settings.
    await focus_command(page, zoom)
    await page.keyboard.press('Home')
    await page.keyboard.press('ArrowDown')
    await page.keyboard.press('ArrowDown')
    await page.keyboard.press('Tab')

async def validate_saved_display_settings(page, output, passed):
    name = 'Build server'
    async def edit_profile():
        await navigate(page, 'Devices')
        await command(page, 'Options for ' + name)
        item = page.get_by_role('menuitem', name='Edit', exact=True)
        await focus_command(page, item)
        await page.keyboard.press('Enter')
        await settings_section(page, 'Display')
        await expect(page.get_by_role('checkbox', name='Fit session to window', exact=True)).to_be_visible(timeout=15000)

    async def exported(filename):
        await navigate(page, 'Settings')
        async with page.expect_download(timeout=15000) as download:
            await command(page, 'Export saved devices')
        path = output / filename
        await (await download.value).save_as(str(path))
        return next(d['Profile'] for d in json.loads(path.read_text())['Devices'] if d['Profile']['Name'] == name)

    await edit_profile()
    await fixed_local_zoom(page)
    await page.screenshot(path=str(output / '10-per-pc-display.png'))
    await settings_section(page, 'General')
    await expect(page.get_by_role('checkbox', name='Fit session to window', exact=True)).to_have_count(0)
    await expect(page.get_by_label('Friendly name', exact=True)).to_have_value(name)
    await settings_section(page, 'Display')
    await expect(page.get_by_label('Friendly name', exact=True)).to_have_count(0)
    await command(page, 'Save')
    saved = await exported('display-workspace.json')
    if saved['FitSessionToWindow'] or saved['LocalZoom'] != 1.5 or (saved['Width'], saved['Height']) != (1280, 720):
        raise AssertionError('Saved local presentation differs from the explicitly chosen fit/zoom and fixed remote size.')
    passed('General and Display tabs detach inactive inputs, retain edits, and save 150% zoom independently of server resolution')
    await edit_profile()
    automatic = page.get_by_role('checkbox', name='Update the resolution on resize', exact=True)
    await focus_command(page, automatic); await page.keyboard.press('Space')
    await expect(automatic).to_be_checked()
    await expect(page.get_by_role('checkbox', name='Fit session to window', exact=True)).to_be_disabled()
    await expect(page.get_by_role('combobox', name='Local zoom', exact=True)).to_be_disabled()
    await command(page, 'Save')
    automatic_saved = await exported('display-automatic.json')
    if automatic_saved != dict(saved, UpdateResolutionOnResize=True):
        raise AssertionError('Automatic resolution changed unrelated saved fields or forgot fixed local zoom.')
    saved = automatic_saved
    await boot(page)
    await edit_profile()
    await expect(page.get_by_role('checkbox', name='Update the resolution on resize', exact=True)).to_be_checked()
    passed('Automatic resolution persists across reload without losing fixed display preferences')
    fit = page.get_by_role('checkbox', name='Fit session to window', exact=True)
    await expect(fit).not_to_be_checked()
    await expect(page.get_by_role('combobox', name='Local zoom', exact=True)).to_be_disabled()
    await command(page, 'Restore display defaults')
    await expect(fit).to_be_checked()
    await expect(page.get_by_role('combobox', name='Local zoom', exact=True)).to_be_disabled()
    await command(page, 'Cancel')
    if await exported('display-cancelled.json') != saved:
        raise AssertionError('Cancelling restored display defaults altered the saved PC.')
    passed('Saved display choices survive reload; cancelling edits does not change the profile')
    await edit_profile()
    await command(page, 'Restore display defaults')
    await command(page, 'Save')
    restored = await exported('display-defaults.json')
    expected = dict(saved, FitSessionToWindow=True, LocalZoom=1, Width=1280, Height=720, BitsPerPixel=24, UpdateResolutionOnResize=False)
    if restored != expected:
        raise AssertionError('Restore display defaults changed non-display settings or did not restore the display values.')
    passed('Restore display defaults updates only display fields, preserving identity and security settings')
    await navigate(page, 'Devices')
