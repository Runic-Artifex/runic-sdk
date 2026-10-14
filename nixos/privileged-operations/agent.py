# A Polkit authentication agent for one process. With AGENT_MODE=dismiss it dismisses every request, as a user
# closing the dialog would; with AGENT_MODE=hold it keeps the request open, as a dialog the user has not answered.
# It starts the command in its arguments with standard input on a pipe, registers for that process, then writes
# "go" so the command starts its privileged action.
import asyncio
import os
import subprocess
import sys

from dbus_next import BusType, DBusError, Variant
from dbus_next.aio import MessageBus
from dbus_next.service import ServiceInterface, method


class Agent(ServiceInterface):
    def __init__(self):
        super().__init__("org.freedesktop.PolicyKit1.AuthenticationAgent")

    @method()
    async def BeginAuthentication(self, action_id: "s", message: "s", icon_name: "s", details: "a{ss}", cookie: "s", identities: "a(sa{sv})"):
        if os.environ.get("AGENT_MODE") == "hold":
            print(f"agent: holding {action_id}", flush=True)
            await asyncio.Event().wait()
        print(f"agent: dismissing {action_id}: {message}", flush=True)
        raise DBusError("org.freedesktop.PolicyKit1.Error.Cancelled", "Dismissed by the test agent")

    @method()
    def CancelAuthentication(self, cookie: "s"):
        print("agent: polkitd cancelled the request", flush=True)


async def main():
    child = subprocess.Popen(sys.argv[1:], stdin=subprocess.PIPE, text=True)
    # Field 22 of /proc/PID/stat is the start time; the fields after the command name start at field 3.
    with open(f"/proc/{child.pid}/stat") as stat:
        start_time = int(stat.read().rsplit(")", 1)[1].split()[19])
    bus = await MessageBus(bus_type=BusType.SYSTEM).connect()
    bus.export("/dev/runic/TestAgent", Agent())
    introspection = await bus.introspect("org.freedesktop.PolicyKit1", "/org/freedesktop/PolicyKit1/Authority")
    authority = bus.get_proxy_object("org.freedesktop.PolicyKit1", "/org/freedesktop/PolicyKit1/Authority", introspection) \
        .get_interface("org.freedesktop.PolicyKit1.Authority")
    subject = ["unix-process", {"pid": Variant("u", child.pid), "start-time": Variant("t", start_time), "uid": Variant("i", os.getuid())}]
    await authority.call_register_authentication_agent(subject, "C", "/dev/runic/TestAgent")
    child.stdin.write("go\n")
    child.stdin.flush()
    while child.poll() is None:
        await asyncio.sleep(0.1)
    sys.exit(child.returncode)


asyncio.run(main())
