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
