def pytest_configure(config):
    config.addinivalue_line("markers", "fake(**kwargs): configure the FakeGenerator used by the client fixture")
