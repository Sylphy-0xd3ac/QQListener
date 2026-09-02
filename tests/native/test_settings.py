import json

from src.core.settings import LEGACY_SETTING_KEYS, Settings


def _fresh_settings(path):
    Settings._instance = None
    Settings._initialized = False
    return Settings(str(path))


def test_settings_drop_legacy_toast_and_path_keys_on_save(tmp_path):
    path = tmp_path / "setting.json"
    path.write_text(
        json.dumps(
            {
                "Tencent_Files_Path": "C:/old",
                "NotificationEngine": "auto",
                "QQ_Only": True,
                "BlackList": ["old"],
                "Important_Person_QQs": ["1001"],
            }
        ),
        encoding="utf-8",
    )

    settings = _fresh_settings(path)
    assert not LEGACY_SETTING_KEYS.intersection(settings.get_all())
    assert settings.important_person_qqs == ["1001"]
    assert settings.save() is True
    saved = json.loads(path.read_text(encoding="utf-8"))
    assert not LEGACY_SETTING_KEYS.intersection(saved)

    Settings._instance = None
    Settings._initialized = False


def test_settings_expose_exact_id_rule_lists(tmp_path):
    path = tmp_path / "setting.json"
    path.write_text(
        json.dumps(
            {
                "Whitelist_Enabled": True,
                "Blacklist_Enabled": True,
                "Whitelist_Groups": ["123"],
                "Blacklist_Groups": ["456"],
                "Whitelist_Person_QQs": ["1001"],
                "Blacklist_Person_QQs": ["1002"],
            }
        ),
        encoding="utf-8",
    )

    settings = _fresh_settings(path)
    assert settings.whitelist_enabled is True
    assert settings.blacklist_enabled is True
    assert settings.whitelist_groups == ["123"]
    assert settings.blacklist_groups == ["456"]
    assert settings.whitelist_person_qqs == ["1001"]
    assert settings.blacklist_person_qqs == ["1002"]

    Settings._instance = None
    Settings._initialized = False


def test_log_level_defaults_to_info_and_normalizes():
    from src.core.logging import LOG_LEVELS, normalize_level

    assert normalize_level("debug") == "DEBUG"
    assert normalize_level("  Warning ") == "WARNING"
    assert normalize_level("胡说") == "INFO"
    assert normalize_level(None) == "INFO"
    assert "TRACE" in LOG_LEVELS and "ERROR" in LOG_LEVELS


def test_log_file_lives_under_the_app_root_not_temp():
    from src.core.logging import log_file_path
    from src.core.resources import app_root

    path = log_file_path()
    assert path.parent == app_root() / "logs"
    assert path.name == "QQListener.log"


def test_read_log_tail_handles_a_missing_file(tmp_path, monkeypatch):
    from src.core import logging as log_module

    monkeypatch.setattr(log_module, "log_file_path", lambda: tmp_path / "nope.log")
    assert "还没有日志文件" in log_module.read_log_tail()


def test_read_log_tail_returns_only_the_last_lines(tmp_path, monkeypatch):
    from src.core import logging as log_module

    target = tmp_path / "QQListener.log"
    target.write_text("\n".join(f"line {i}" for i in range(100)), encoding="utf-8")
    monkeypatch.setattr(log_module, "log_file_path", lambda: target)

    tail = log_module.read_log_tail(5)

    assert tail.splitlines() == [f"line {i}" for i in range(95, 100)]


def test_clear_log_empties_the_file(tmp_path, monkeypatch):
    from src.core import logging as log_module

    target = tmp_path / "QQListener.log"
    target.write_text("旧日志", encoding="utf-8")
    monkeypatch.setattr(log_module, "log_file_path", lambda: target)

    assert log_module.clear_log() is True
    assert target.read_text(encoding="utf-8") == ""
