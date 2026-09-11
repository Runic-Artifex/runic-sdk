"""XTEST input for the private Xorg test display; never connects to host input."""
import ctypes as C
import os
import time


class XorgInput:
    def __init__(self):
        self.x11 = C.CDLL(os.environ['RUNIC_LIBX11'])
        self.xtest = C.CDLL(os.environ['RUNIC_LIBXTST'])
        self.x11.XOpenDisplay.argtypes = [C.c_char_p]
        self.x11.XOpenDisplay.restype = C.c_void_p
        self.x11.XCloseDisplay.argtypes = [C.c_void_p]
        self.x11.XSync.argtypes = [C.c_void_p, C.c_int]
        signatures = {
            'XTestFakeKeyEvent': [C.c_void_p, C.c_uint, C.c_int, C.c_ulong],
            'XTestFakeButtonEvent': [C.c_void_p, C.c_uint, C.c_int, C.c_ulong],
            'XTestFakeMotionEvent': [C.c_void_p, C.c_int, C.c_int, C.c_int, C.c_ulong],
            'XTestQueryExtension': [C.c_void_p] + [C.POINTER(C.c_int)] * 4,
        }
        for name, arguments in signatures.items():
            getattr(self.xtest, name).argtypes = arguments
            getattr(self.xtest, name).restype = C.c_int
        self.display = self.x11.XOpenDisplay(None)
        self.pressed = set()
        if not self.display:
            raise RuntimeError('Cannot open the private Xorg display')
        values = [C.c_int() for _ in range(4)]
        if not self.xtest.XTestQueryExtension(self.display, *[C.byref(v) for v in values]):
            self.close()
            raise RuntimeError('Xorg does not expose XTEST')

    def key(self, evdev_code, pressed):
        # The private server uses the standard evdev XKB keycode map.
        if not self.xtest.XTestFakeKeyEvent(self.display, evdev_code + 8, pressed, 0):
            raise RuntimeError('XTEST rejected keyboard input')
        self.pressed.add(evdev_code) if pressed else self.pressed.discard(evdev_code)
        self.x11.XSync(self.display, False)

    def click(self, x, y):
        if not self.xtest.XTestFakeMotionEvent(self.display, -1, round(x), round(y), 0):
            raise RuntimeError('XTEST rejected pointer movement')
        for pressed in (True, False):
            if not self.xtest.XTestFakeButtonEvent(self.display, 1, pressed, 0):
                raise RuntimeError('XTEST rejected pointer button')
            self.x11.XSync(self.display, False)
            time.sleep(0.05)

    def close(self):
        if self.display:
            for code in list(self.pressed):
                self.key(code, False)
            self.x11.XCloseDisplay(self.display)
            self.display = None
