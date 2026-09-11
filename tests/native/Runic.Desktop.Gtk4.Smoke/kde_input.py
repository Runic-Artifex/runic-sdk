"""Guest-only KWin EIS keyboard sender; never uses host input devices."""
import ctypes as C
import os
import select
import time

from gi.repository import Gio, GLib


class KdeInput:
    def __init__(self):
        self.bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        self.cookie = None
        self.context = None
        self.devices = set()
        self.keyboard = None
        self.pointer = None
        self.lib = C.CDLL(os.environ['RUNIC_LIBEI'])
        ptr = C.c_void_p
        signatures = {
            'ei_new_sender': (ptr, [ptr]), 'ei_unref': (ptr, [ptr]),
            'ei_configure_name': (None, [ptr, C.c_char_p]),
            'ei_setup_backend_fd': (C.c_int, [ptr, C.c_int]),
            'ei_get_fd': (C.c_int, [ptr]), 'ei_dispatch': (None, [ptr]),
            'ei_get_event': (ptr, [ptr]), 'ei_event_unref': (ptr, [ptr]),
            'ei_event_get_type': (C.c_int, [ptr]), 'ei_event_get_seat': (ptr, [ptr]),
            'ei_event_get_device': (ptr, [ptr]), 'ei_seat_bind_capabilities': (None, [ptr]),
            'ei_device_ref': (ptr, [ptr]), 'ei_device_unref': (ptr, [ptr]),
            'ei_device_has_capability': (C.c_bool, [ptr, C.c_int]),
            'ei_device_start_emulating': (None, [ptr, C.c_uint32]),
            'ei_device_keyboard_key': (None, [ptr, C.c_uint32, C.c_bool]),
            'ei_device_frame': (None, [ptr, C.c_uint64]),
            'ei_device_pointer_motion_absolute': (None, [ptr, C.c_double, C.c_double]),
            'ei_device_button_button': (None, [ptr, C.c_uint32, C.c_bool]),
        }
        for name, (result, arguments) in signatures.items():
            function = getattr(self.lib, name)
            function.restype, function.argtypes = result, arguments
        try:
            response, fds = self.bus.call_with_unix_fd_list_sync(
                'org.kde.KWin', '/org/kde/KWin/EIS/RemoteDesktop', 'org.kde.KWin.EIS.RemoteDesktop',
                'connectToEIS', GLib.Variant('(i)', (3,)), None, 0, 5000, None, None)
            handle, self.cookie = response.unpack()
            self.context = self.lib.ei_new_sender(None)
            if not self.context:
                raise RuntimeError('Could not allocate libei sender')
            self.lib.ei_configure_name(self.context, b'Runic isolated keyboard test')
            if self.lib.ei_setup_backend_fd(self.context, fds.get(handle)) != 0:
                raise RuntimeError('Could not connect libei to KWin')
            deadline = time.monotonic() + 10
            while self.keyboard is None or self.pointer is None:
                self.dispatch()
                if time.monotonic() >= deadline:
                    raise TimeoutError('KWin did not resume an EIS keyboard')
                select.select([self.lib.ei_get_fd(self.context)], [], [], 0.05)
        except BaseException:
            self.close()
            raise

    def dispatch(self):
        self.lib.ei_dispatch(self.context)
        while event := self.lib.ei_get_event(self.context):
            try:
                kind = self.lib.ei_event_get_type(event)
                if kind == 2:
                    raise RuntimeError('KWin disconnected the EIS sender')
                if kind == 3:  # EI_EVENT_SEAT_ADDED; variadic list ends with NULL.
                    self.lib.ei_seat_bind_capabilities(self.lib.ei_event_get_seat(event), C.c_int(4), C.c_int(2), C.c_int(32), C.c_void_p())
                elif kind == 5:
                    device = self.lib.ei_event_get_device(event)
                    self.devices.add(self.lib.ei_device_ref(device))
                elif kind in (6, 7):
                    if self.lib.ei_event_get_device(event) == self.keyboard:
                        self.keyboard = None
                    if self.lib.ei_event_get_device(event) == self.pointer:
                        self.pointer = None
                elif kind == 8:
                    device = self.lib.ei_event_get_device(event)
                    if self.lib.ei_device_has_capability(device, 4):
                        self.lib.ei_device_start_emulating(device, 1)
                        self.keyboard = device
                    if self.lib.ei_device_has_capability(device, 2) and self.lib.ei_device_has_capability(device, 32):
                        self.lib.ei_device_start_emulating(device, 1)
                        self.pointer = device
            finally:
                self.lib.ei_event_unref(event)

    def key(self, code, pressed):
        self.dispatch()
        if self.keyboard is None:
            raise RuntimeError('KWin keyboard is not resumed')
        self.lib.ei_device_keyboard_key(self.keyboard, code, pressed)
        self.lib.ei_device_frame(self.keyboard, time.monotonic_ns() // 1000)
        self.lib.ei_dispatch(self.context)

    def click(self, x, y):
        self.dispatch()
        if self.pointer is None:
            raise RuntimeError('KWin absolute pointer is not resumed')
        self.lib.ei_device_pointer_motion_absolute(self.pointer, x, y)
        self.lib.ei_device_frame(self.pointer, time.monotonic_ns() // 1000)
        self.lib.ei_dispatch(self.context)
        time.sleep(0.1)
        for pressed in (True, False):
            self.lib.ei_device_button_button(self.pointer, 272, pressed)
            self.lib.ei_device_frame(self.pointer, time.monotonic_ns() // 1000)
            self.lib.ei_dispatch(self.context)
            time.sleep(0.05)

    def close(self):
        for device in self.devices:
            self.lib.ei_device_unref(device)
        self.devices.clear()
        if self.context:
            self.lib.ei_unref(self.context)
            self.context = None
        if self.cookie is not None:
            cookie, self.cookie = self.cookie, None
            self.bus.call_sync('org.kde.KWin', '/org/kde/KWin/EIS/RemoteDesktop',
                'org.kde.KWin.EIS.RemoteDesktop', 'disconnect', GLib.Variant('(i)', (cookie,)),
                None, 0, 5000, None)


def windows():
    """Read compositor geometry through a temporary, read-only KWin script."""
    import json
    import tempfile
    import uuid
    bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
    result = []
    interface = 'com.runic.tests.Geometry'
    node = Gio.DBusNodeInfo.new_for_xml(
        f'<node><interface name="{interface}"><method name="Result"><arg type="s" direction="in"/></method></interface></node>')
    def receive(connection, sender, path, name, method, parameters, invocation):
        result.append(json.loads(parameters.unpack()[0]))
        invocation.return_value(None)
    registration = bus.register_object('/com/runic/tests/Geometry', node.interfaces[0], receive, None, None)
    plugin = 'runic-geometry-' + uuid.uuid4().hex
    loaded = False
    def call(method, parameters):
        return bus.call_sync('org.kde.KWin', '/Scripting', 'org.kde.kwin.Scripting', method,
                             parameters, None, 0, 5000, None).unpack()[0]
    try:
        with tempfile.NamedTemporaryFile(mode='w', suffix='.js') as script:
            script.write('callDBus(' + json.dumps(bus.get_unique_name()) + ',"/com/runic/tests/Geometry",'
                         + json.dumps(interface) + ',"Result",JSON.stringify(workspace.windowList().map(w => '
                         '({pid:w.pid,caption:w.caption,resourceClass:String(w.resourceClass),'
                         'frame:w.frameGeometry,buffer:w.bufferGeometry,client:w.clientGeometry,active:w.active}))));')
            script.flush()
            identifier = call('loadScript', GLib.Variant('(ss)', (script.name, plugin)))
            if identifier < 0:
                raise RuntimeError('KWin rejected the geometry script')
            loaded = True
            bus.call_sync('org.kde.KWin', f'/Scripting/Script{identifier}', 'org.kde.kwin.Script',
                          'run', None, None, 0, 5000, None)
            deadline = time.monotonic() + 5
            while not result:
                while GLib.MainContext.default().pending():
                    GLib.MainContext.default().iteration(False)
                if time.monotonic() > deadline:
                    raise TimeoutError('KWin did not return window geometry')
                time.sleep(0.01)
            return result[0]
    finally:
        try:
            if loaded:
                call('unloadScript', GLib.Variant('(s)', (plugin,)))
        finally:
            bus.unregister_object(registration)
