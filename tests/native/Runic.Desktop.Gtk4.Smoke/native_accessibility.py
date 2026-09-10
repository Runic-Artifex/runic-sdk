"""Shared native AT-SPI lookup and action helpers for disposable desktops."""
import pyatspi
from gi.repository import GLib


def tree(root, depth=0):
    if depth > 40:
        return
    yield root
    for child in root:
        if child is not None:
            yield from tree(child, depth + 1)


def applications():
    return [a for a in pyatspi.Registry.getDesktop(0) if a is not None]


def unique(nodes, predicate):
    matches = [n for n in nodes if predicate(n)]
    if len(matches) != 1:
        raise RuntimeError(f"Expected one accessible match, found {len(matches)}")
    return matches[0]


def action_names(node):
    try:
        actions = node.queryAction()
        return [actions.getName(i) for i in range(actions.nActions)]
    except (NotImplementedError, GLib.Error):
        return []


def invoke(node, name):
    names = action_names(node)
    if name not in names or not node.queryAction().doAction(names.index(name)):
        raise RuntimeError(f"Native action rejected: {node.name}: {name}")
