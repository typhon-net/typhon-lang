from collections import namedtuple
from enum import Enum as EnumBase


Record = namedtuple('Record', 'code label')


class Category(Record, EnumBase):
    PRIMARY = (29, 'primary')
