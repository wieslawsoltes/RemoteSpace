"""Per-PC display settings through real editor, persistence and export commands."""
import json
from playwright.async_api import expect
from driver import boot, command, focus_command, navigate

async def settings_section(page, name):
    # Editing a lower General field can scroll the native form past its tabs.
    # Scroll the real painted editor to the start with a user wheel gesture;
    # scrolling its invisible DOM proxy does not move Uno's ScrollViewer.
    surface = page.get_by_label('PC settings dialog surface', exact=True)
    editor = surface.get_by_label('PC profile editor layout', exact=True)
    await expect(editor).to_be_visible()
    area = await editor.bounding_box()
    await page.mouse.move(area['x'] + area['width'] / 2, area['y'] + area['height'] / 2)
    await page.mouse.wheel(0, -2000)
    await page.evaluate('() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))')
    strip = page.get_by_role('tablist', name='PC settings sections', exact=True)
    await expect(strip).to_be_visible()
    tab = strip.get_by_role('tab', name=name, exact=True)
    await expect(tab).to_be_visible(timeout=15000)
    await page.evaluate('() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))')
    bounds = await tab.bounding_box()
    viewport = page.viewport_size
    if not bounds or bounds['x'] < 0 or bounds['y'] < 0 or bounds['x'] + bounds['width'] > viewport['width'] or bounds['y'] + bounds['height'] > viewport['height']:
        raise AssertionError('A visibly painted settings tab has an out-of-window semantic hit target.')
    area = await editor.bounding_box()
    if bounds['y'] < area['y'] or bounds['y'] + bounds['height'] > area['y'] + area['height']:
        raise AssertionError('The settings tab is clipped outside the native editor viewport.')
    await tab.click(force=True)
    await expect(tab).to_have_attribute('aria-selected', 'true')
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
    # Native tab keyboard navigation must retain a single selected section and
    # remove inactive editors, not just move a visible accent underline.
    display_tab = page.get_by_role('tab', name='Display', exact=True)
    await display_tab.press('Home')
    await expect(page.get_by_role('tab', name='General', exact=True)).to_have_attribute('aria-selected', 'true')
    await expect(page.get_by_role('checkbox', name='Fit session to window', exact=True)).to_have_count(0)
    await page.get_by_role('tab', name='General', exact=True).press('End')
    await expect(display_tab).to_have_attribute('aria-selected', 'true')
    await display_tab.press('ArrowRight')
    await expect(page.get_by_role('tab', name='General', exact=True)).to_have_attribute('aria-selected', 'true')
    await page.get_by_role('tab', name='General', exact=True).press('ArrowLeft')
    await expect(display_tab).to_have_attribute('aria-selected', 'true')
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
