# Copyright (c) 2026 The Typhon contributors.
# Licensed under the MIT License. See LICENSE file in the project root for full license information.

Enum = None


class DescribingType(type):
    @property
    def marker(cls):
        return cls.__name__ + ':source'

    def __iter__(cls):
        return iter([cls.__name__])

    def __getitem__(cls, name):
        return cls.__name__, name


class Enum(metaclass=DescribingType):
    def __init__(self, value: int):
        self.value = value
