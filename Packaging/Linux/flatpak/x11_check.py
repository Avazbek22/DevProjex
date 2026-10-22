"""Small X11 driver for the installed application's desktop checks."""
from pathlib import Path
import select
import struct
import time
import zlib
from Xlib import X, XK, display, error
from Xlib.ext import xtest
from Xlib.protocol import event


class Desktop:
    def __init__(self):
        self.display = display.Display()
        self.root = self.display.screen().root

    def windows(self):
        found = []
        pending = list(self.root.query_tree().children)
        visited = 0
        while pending and visited < 512:
            window = pending.pop(0)
            visited += 1
            try:
                if window.get_attributes().map_state == X.IsViewable:
                    found.append((window, window.get_wm_name() or '', window.get_wm_class()))
                    pending.extend(window.query_tree().children)
            except error.XError:
                continue
        return found

    def click(self, window, x, y):
        position = self.root.translate_coords(window, 0, 0)
        xtest.fake_input(self.display, X.MotionNotify, x=position.x + x, y=position.y + y)
        self.display.sync()
        time.sleep(.05)
        xtest.fake_input(self.display, X.ButtonPress, 1)
        self.display.sync()
        time.sleep(.05)
        xtest.fake_input(self.display, X.ButtonRelease, 1)
        self.display.sync()

    def key(self, key, modifiers=()):
        codes = [self.display.keysym_to_keycode(XK.string_to_keysym(name)) for name in (*modifiers, key)]
        for code in codes:
            xtest.fake_input(self.display, X.KeyPress, code)
        for code in reversed(codes):
            xtest.fake_input(self.display, X.KeyRelease, code)
        self.display.sync()

    def focus(self, window):
        window.set_input_focus(X.RevertToParent, X.CurrentTime)
        self.display.sync()

    def type(self, text):
        for character in text:
            symbol = ord(character)
            code = self.display.keysym_to_keycode(symbol)
            if not code:
                raise ValueError('Character has no X11 keycode: ' + repr(character))
            mapping = self.display.get_keyboard_mapping(code, 1)[0]
            shifted = mapping.index(symbol) % 2 == 1
            shift = self.display.keysym_to_keycode(XK.XK_Shift_L)
            if shifted:
                xtest.fake_input(self.display, X.KeyPress, shift)
            xtest.fake_input(self.display, X.KeyPress, code)
            xtest.fake_input(self.display, X.KeyRelease, code)
            if shifted:
                xtest.fake_input(self.display, X.KeyRelease, shift)
        self.display.sync()

    def dismiss(self, window):
        window.send_event(event.ClientMessage(window=window, client_type=self.display.intern_atom('WM_PROTOCOLS'),
                          data=(32, [self.display.intern_atom('WM_DELETE_WINDOW'), X.CurrentTime, 0, 0, 0])))
        self.display.sync()

    def clipboard(self, timeout=5):
        window = self.root.create_window(-10, -10, 1, 1, 0, X.CopyFromParent, X.InputOutput,
                                         X.CopyFromParent, event_mask=X.PropertyChangeMask)
        target = self.display.intern_atom('UTF8_STRING')
        prop = self.display.intern_atom('DEVPROJEX_SMOKE_CLIPBOARD')
        window.convert_selection(self.display.intern_atom('CLIPBOARD'), target, prop, X.CurrentTime)
        self.display.flush()
        try:
            deadline = time.monotonic() + timeout
            while time.monotonic() < deadline:
                while self.display.pending_events():
                    event = self.display.next_event()
                    if event.type == X.SelectionNotify:
                        value = window.get_full_property(prop, X.AnyPropertyType)
                        return bytes(value.value).decode('utf-8') if value else None
                select.select([self.display.fileno()], [], [], max(0, deadline - time.monotonic()))
            raise TimeoutError('Clipboard owner did not answer')
        finally:
            window.destroy()
            self.display.flush()

    def screenshot(self, window, destination):
        geometry = window.get_geometry()
        width, height = geometry.width, geometry.height
        image = window.get_image(0, 0, width, height, X.ZPixmap, 0xffffffff)
        stride = len(image.data) // height
        pixel_bytes = stride // width
        if pixel_bytes not in (3, 4):
            raise ValueError('Unsupported X11 screenshot pixel layout')
        rows = bytearray()
        for y in range(height):
            rows.append(0)
            row = image.data[y * stride:(y + 1) * stride]
            for x in range(width):
                offset = x * pixel_bytes
                rows.extend((row[offset + 2], row[offset + 1], row[offset]))

        def chunk(kind, content):
            return struct.pack('>I', len(content)) + kind + content + struct.pack('>I', zlib.crc32(kind + content))

        header = struct.pack('>2I5B', width, height, 8, 2, 0, 0, 0)
        Path(destination).write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', header) +
                                      chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b''))

    def close(self):
        self.display.close()


if __name__ == '__main__':
    desktop = Desktop()
    try:
        for window, name, wmclass in desktop.windows():
            geometry = window.get_geometry()
            print(window.id, repr(name), wmclass, geometry.width, geometry.height)
    finally:
        desktop.close()
