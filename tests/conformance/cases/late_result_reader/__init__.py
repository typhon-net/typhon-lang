class Document:
    def __init__(self, source):
        self.source = source

    def get_content(self, *args, **kwargs):
        return self.source


def parse(source, *args, **kwargs):
    return kwargs['factory'](source)
