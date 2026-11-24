entries = {}


def register(key, callback, constructor=None):
    if not callable(callback):
        raise TypeError('callback must be callable')
    entries[key] = callback
    if constructor is not None and not callable(constructor):
        raise TypeError('constructor must be callable')


def parse(value):
    return value + 1


def format_value(value):
    return str(value)


register(int, parse, int)
register(str, format_value)
