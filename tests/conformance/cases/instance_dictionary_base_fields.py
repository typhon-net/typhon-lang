class Message:
    def __init__(self):
        self._headers = []

    def __getitem__(self, key):
        for name, value in self._headers:
            if name == key:
                return value
        return None
