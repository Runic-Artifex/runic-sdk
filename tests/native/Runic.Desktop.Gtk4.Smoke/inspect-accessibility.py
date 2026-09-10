#!/usr/bin/env python3
"""Read the real AT-SPI tree in a disposable Runic test desktop; never synthesize DOM results."""
import json
import argparse
import pyatspi

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--click", help="Invoke one uniquely named button in the disposable Runic fixture")
args = parser.parse_args()
nodes = []
buttons = []

def walk(node, depth=0):
    if depth > 16 or len(nodes) >= 800:
        return
    try:
        item = {"depth": depth, "role": node.getRoleName(), "name": node.name,
                "focused": node.getState().contains(pyatspi.STATE_FOCUSED)}
        try:
            text = node.queryText()
            item["text"] = text.getText(0, min(text.characterCount, 256))
        except NotImplementedError:
            pass
        nodes.append(item)
        if args.click and item["role"] == "button" and item["name"] == args.click:
            buttons.append(node)
        for child in node:
            if child is not None:
                walk(child, depth + 1)
    except Exception as error:
        nodes.append({"depth": depth, "error": str(error)})

for application in pyatspi.Registry.getDesktop(0):
    # This runs only in the isolated VM, not against the maintainer's desktop.
    if application is not None and "runic" in application.name.lower():
        walk(application)
print(json.dumps(nodes, ensure_ascii=False, indent=2))
required = {"Your name", "Composition text", "Open file", "Finish session"}
seen = {node.get("name") for node in nodes}
if not required <= seen:
    raise SystemExit("FAIL missing accessible control names: " + repr(required - seen))
print("PASS native AT-SPI exposes the named form and action controls; screen-reader interaction remains a separate check.")
if args.click:
    if len(buttons) != 1:
        raise SystemExit("FAIL expected one matching fixture button")
    action = buttons[0].queryAction()
    if action.nActions != 1 or not action.doAction(0):
        raise SystemExit("FAIL native button action was rejected")
    print("Invoked native button: " + args.click)
