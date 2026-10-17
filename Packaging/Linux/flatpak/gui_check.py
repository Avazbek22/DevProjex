"""Drive real native controls; captures are unmodified X11 window pixels."""
import json
from pathlib import Path
import shlex
import subprocess
import time
from x11_check import Desktop

APP = 'io.github.Avazbek22.DevProjex'


def check_path_window(checks, root, loaded):
    desktop = Desktop()
    previous = {window.id for window, _, _ in desktop.windows()}
    process = None
    name = 'outside-gui-' + ('allowed' if loaded else 'denied')
    try:
        with (checks.evidence / (name + '.log')).open('w') as output:
            process = subprocess.Popen(['flatpak', 'run', APP, 'open', str(root), '--new-window', '--preview', '--language', 'en'],
                                       stdout=output, stderr=output)
            deadline = time.monotonic() + 30
            main = None
            error = None
            while time.monotonic() < deadline:
                for window, title, wmclass in desktop.windows():
                    if window.id in previous or not wmclass or wmclass[1] != APP:
                        continue
                    if title == 'Error':
                        error = window
                    if window.get_geometry().width >= 900:
                        main = window
                if main is not None and (loaded and str(root) in (main.get_wm_name() or '') or not loaded and error is not None):
                    break
                time.sleep(.1)
            assert main is not None, 'The folder check did not open a desktop window'
            if loaded:
                state = checks.app(['ui', 'status', '--project', root], name + '-status')
                state = json.loads(state.stdout)['state']
                assert state['projectLoaded'] and state['projectPath'] == str(root), state
                time.sleep(.5)
                desktop.screenshot(main, checks.evidence / (name + '.png'))
            else:
                assert error is not None, 'An inaccessible root did not show the desktop error dialog'
                desktop.screenshot(error, checks.evidence / (name + '.png'))
                desktop.dismiss(error)
            desktop.dismiss(main)
            process.wait(timeout=20)
            assert process.returncode == 0, process.returncode
    finally:
        if process is not None and process.poll() is None:
            for window, _, wmclass in desktop.windows():
                if window.id not in previous and wmclass and wmclass[1] == APP:
                    desktop.dismiss(window)
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.terminate()
                process.wait(timeout=10)
        desktop.close()


def check_gui(checks):
    desktop = Desktop()
    process = None

    def find(predicate, timeout=15):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            for window, title, wmclass in desktop.windows():
                size = window.get_geometry()
                if predicate(window, title, wmclass, size):
                    return window
            time.sleep(.1)
        windows = []
        for window, title, wmclass in desktop.windows():
            size = window.get_geometry()
            windows.append((window.id, title, wmclass, size.width, size.height))
            if wmclass and wmclass[1] == APP:
                desktop.screenshot(window, checks.evidence / ('unexpected-window-' + str(window.id) + '.png'))
        (checks.evidence / 'unexpected-windows.json').write_text(json.dumps(windows))
        raise AssertionError('Expected native window did not appear')

    def popup(width, height):
        return find(lambda window, title, wmclass, size: abs(size.width - width) < 5 and abs(size.height - height) < 5)

    def pause():
        time.sleep(.35)

    try:
        with (checks.evidence / 'gui-process.log').open('w') as output:
            process = subprocess.Popen(['flatpak', 'run', APP, 'open', str(checks.fixture), '--new-window', '--preview', '--language', 'en'],
                                       stdout=output, stderr=output)
            main = find(lambda window, title, wmclass, size: wmclass and wmclass[1] == APP and str(checks.fixture) in title,
                        timeout=45)
            desktop.focus(main)
            time.sleep(2)
            desktop.screenshot(main, checks.evidence / 'gui-home.png')
            status = checks.app(['ui', 'status', '--project', checks.fixture], 'desktop-status')
            assert str(checks.fixture) in status.stdout, status.stdout
            assert main.get_wm_class()[1] == APP, main.get_wm_class()
            def saved_selection():
                try:
                    return json.loads(checks.profiles.read_text())['profiles'][str(checks.fixture)]['selectedPaths']
                except (OSError, KeyError, json.JSONDecodeError):
                    return None
            if saved_selection() != ['selected.cs']:
                desktop.click(main, 68, 230)
                deadline = time.monotonic() + 10
                while saved_selection() != ['selected.cs'] and time.monotonic() < deadline:
                    time.sleep(.1)
                assert saved_selection() == ['selected.cs'], saved_selection()
            desktop.click(main, 454, 60)
            pause()
            preview = desktop.clipboard()
            assert preview and 'selected.cs' in preview and 'FlatpakSearchMarker' in preview, preview
            (checks.evidence / 'gui-exported-context.txt').write_text(preview)
            desktop.click(main, 254, 16)
            pause()
            view_windows = [(window, window.get_geometry()) for window, title, wmclass in desktop.windows()
                            if not title and window.get_geometry().width > 180 and window.get_geometry().height > 200]
            if view_windows:
                desktop.screenshot(view_windows[-1][0], checks.evidence / 'view-menu.png')
                preference = checks.state / 'agent-activity-view.json'
                enabled = preference.exists() and json.loads(preference.read_text()).get('Enabled') is True
                if not enabled:
                    desktop.click(view_windows[-1][0], 95, 294)
                else:
                    desktop.key('Escape')
            else:
                raise AssertionError('View menu did not open')
            pause()
            live_folder = checks.evidence / 'gui-live'
            live_folder.mkdir(exist_ok=True)
            with (checks.evidence / 'gui-live-client.log').open('w') as live_output:
                live = subprocess.Popen([*checks.client_command, str(checks.fixture), str(live_folder), '--live', '--hold'],
                                        stdout=live_output, stderr=live_output)
                try:
                    deadline = time.monotonic() + 30
                    while not (live_folder / 'ready').exists() and time.monotonic() < deadline and live.poll() is None:
                        time.sleep(.1)
                    assert (live_folder / 'ready').exists(), 'GUI live MCP check did not become ready'
                    time.sleep(6)
                    desktop.screenshot(main, checks.evidence / 'gui-returned-spotlight.png')
                finally:
                    (live_folder / 'release').touch()
                    live.wait(timeout=20)
                    assert live.returncode == 0, live.returncode
            desktop.click(main, 650, 16)
            help_menu = popup(211, 177)
            desktop.screenshot(help_menu, checks.evidence / 'help-no-update.png')
            desktop.click(help_menu, 45, 82)
            about = popup(600, 456)
            desktop.screenshot(about, checks.evidence / 'about-top.png')
            desktop.click(about, 510, 320)
            for _ in range(6):
                from Xlib import X
                from Xlib.ext import xtest
                xtest.fake_input(desktop.display, X.ButtonPress, 5)
                xtest.fake_input(desktop.display, X.ButtonRelease, 5)
            desktop.display.sync()
            pause()
            desktop.screenshot(about, checks.evidence / 'about-flatpak.png')
            desktop.click(about, 495, 425)
            pause()
            assert process.poll() is None, 'Opening the external link closed the application'
            desktop.screenshot(about, checks.evidence / 'after-external-link.png')
            desktop.dismiss(about)
            pause()
            for client, row in [('claude', 23), ('codex', 52), ('code', 113)]:
                desktop.focus(main)
                desktop.key('Escape')
                pause()
                desktop.click(main, 82, 16)
                pause()
                if not any(abs(window.get_geometry().width - 188) < 5 for window, _, _ in desktop.windows()):
                    desktop.click(main, 82, 16)
                first = popup(188, 138)
                desktop.click(first, 102, 52)
                second = popup(264, 177)
                desktop.click(second, 110, row)
                if client != 'code':
                    dialog = find(lambda window, title, wmclass, size: title == 'Manual MCP configuration')
                else:
                    dialog = popup(420, 95)
                pause()
                value = desktop.clipboard()
                expected = 'code ' + shlex.quote(str(checks.fixture)) if client == 'code' else 'cd ' + shlex.quote(str(checks.fixture)) + ' && ' + client
                assert value == expected, (client, value, expected)
                (checks.evidence / ('clipboard-' + client + '.txt')).write_text(value)
                desktop.screenshot(dialog, checks.evidence / ('clipboard-' + client + '.png'))
                desktop.dismiss(dialog)
                pause()
            desktop.focus(main)
            pause()
            desktop.click(main, 650, 16)
            help_menu = popup(211, 177)
            desktop.click(help_menu, 80, 52)
            setup = find(lambda window, title, wmclass, size: size.width > 400 and 'terminal' in title.lower())
            desktop.screenshot(setup, checks.evidence / 'terminal-setup.png')
            desktop.click(setup, 341, 231)
            pause()
            if any(window.id == setup.id for window, _, _ in desktop.windows()):
                desktop.dismiss(setup)
            shim = Path.home() / '.local/bin/devprojex'
            assert 'exec flatpak run ' + APP + ' "$@"' in shim.read_text(), shim.read_text()
            checks.run(['bash', '-lc', 'devprojex --version; devprojex mcp --help'], 'host-shim')
            before = shim.read_bytes()
            desktop.click(main, 650, 16)
            desktop.click(popup(211, 177), 80, 52)
            setup = find(lambda window, title, wmclass, size: size.width > 400 and 'terminal' in title.lower())
            desktop.screenshot(setup, checks.evidence / 'terminal-setup-repeated.png')
            desktop.dismiss(setup)
            assert shim.read_bytes() == before, 'Repeated setup changed the managed shim'
            desktop.screenshot(main, checks.evidence / 'gui-final.png')
            desktop.dismiss(main)
            process.wait(timeout=20)
            assert process.returncode == 0, process.returncode
    finally:
        if process is not None and process.poll() is None:
            for window, title, wmclass in desktop.windows():
                if wmclass and wmclass[1] == APP:
                    desktop.dismiss(window)
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.terminate()
                process.wait(timeout=10)
        desktop.close()
