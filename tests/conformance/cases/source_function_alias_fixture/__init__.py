def creator(label: str = 'default', *, suffix: str = 'value') -> str:
    return label + ' ' + suffix


alias = creator


def call_inside() -> str:
    return alias('inside')
