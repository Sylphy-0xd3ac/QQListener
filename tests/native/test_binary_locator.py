from src.native.binary_locator import HOOK_DLL_NAME, find_hook_binary, missing_binary_help


def test_find_hook_binary_locates_in_dir(tmp_path):
    (tmp_path / HOOK_DLL_NAME).write_bytes(b"MZ")
    found = find_hook_binary([str(tmp_path)])
    assert found is not None and found.endswith(HOOK_DLL_NAME)


def test_find_hook_binary_missing_returns_none(tmp_path):
    assert find_hook_binary([str(tmp_path)]) is None


def test_missing_binary_help_mentions_name():
    assert HOOK_DLL_NAME in missing_binary_help()
