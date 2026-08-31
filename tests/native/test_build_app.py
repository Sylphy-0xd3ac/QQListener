from scripts import build_app


def test_windows_build_collects_delayed_pefile_import(monkeypatch):
    monkeypatch.setattr(build_app.sys, "platform", "win32")

    command = build_app.build_command("QQListener-Test", "pyside6")

    pairs = list(zip(command, command[1:], strict=False))
    assert ("--hidden-import", "pefile") in pairs
