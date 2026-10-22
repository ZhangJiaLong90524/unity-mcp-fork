"""Instances from several machines sharing one HTTP server: host label, project guid and path."""

import asyncio
import logging
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from core.config import config
from transport.models import RegisterMessage, SessionDetails
from transport.plugin_hub import PluginHub
from transport.plugin_registry import PluginRegistry


@pytest.fixture(autouse=True)
def _reset_plugin_hub():
    old_registry = PluginHub._registry
    old_connections = PluginHub._connections.copy()
    old_pending = PluginHub._pending.copy()
    old_lock = PluginHub._lock
    old_loop = PluginHub._loop
    old_mcp = PluginHub._mcp
    old_ping_tasks = PluginHub._ping_tasks.copy()
    old_last_pong = PluginHub._last_pong.copy()

    yield

    for task in PluginHub._ping_tasks.values():
        if task not in old_ping_tasks.values():
            task.cancel()
    PluginHub._registry = old_registry
    PluginHub._connections = old_connections
    PluginHub._pending = old_pending
    PluginHub._lock = old_lock
    PluginHub._loop = old_loop
    PluginHub._mcp = old_mcp
    PluginHub._ping_tasks = old_ping_tasks
    PluginHub._last_pong = old_last_pong


def _fake_websocket():
    ws = SimpleNamespace()
    ws.send_json = AsyncMock()
    ws.close = AsyncMock()
    ws.state = SimpleNamespace()
    return ws


def _hub() -> PluginHub:
    # _handle_register only touches class state; skip the Starlette endpoint constructor.
    return PluginHub.__new__(PluginHub)


async def _configure_hub() -> PluginRegistry:
    registry = PluginRegistry()
    PluginHub.configure(registry, asyncio.get_running_loop())
    return registry


async def _register(project_hash, host_label=None, project_guid=None,
                    project_path="C:/work/Game", project_name="Game"):
    payload = RegisterMessage(
        project_name=project_name,
        project_hash=project_hash,
        unity_version="2022.3",
        project_path=project_path,
        host_label=host_label,
        project_guid=project_guid,
    )
    ws = _fake_websocket()
    await _hub()._handle_register(ws, payload)
    return ws


class TestRegistration:
    @pytest.mark.asyncio
    async def test_register_keeps_host_label_project_guid_and_path(self):
        await _configure_hub()

        await _register("key-pc1", host_label="Lab PC 1", project_guid="g" * 32)

        sessions = (await PluginHub.get_sessions()).sessions
        (details,) = sessions.values()
        assert details.host_label == "Lab PC 1"
        assert details.project_guid == "g" * 32
        assert details.project_path == "C:/work/Game"

    @pytest.mark.asyncio
    async def test_register_from_older_plugin_without_new_fields_is_accepted(self):
        await _configure_hub()
        payload = RegisterMessage.model_validate({
            "type": "register",
            "project_name": "Game",
            "project_hash": "abc123",
            "unity_version": "2022.3",
        })

        await _hub()._handle_register(_fake_websocket(), payload)

        (details,) = (await PluginHub.get_sessions()).sessions.values()
        assert details.host_label is None
        assert details.project_guid is None

    @pytest.mark.asyncio
    async def test_distinct_routing_keys_both_stay_connected(self):
        """The server keys sessions by routing key only; the plugin mixes machine identity into it."""
        await _configure_hub()

        await _register("key-pc1", host_label="Lab PC 1", project_guid="g" * 32)
        await _register("key-pc2", host_label="Lab PC 2", project_guid="g" * 32)

        sessions = (await PluginHub.get_sessions()).sessions
        assert {d.host_label for d in sessions.values()} == {"Lab PC 1", "Lab PC 2"}

    @pytest.mark.asyncio
    async def test_reconnect_with_same_routing_key_replaces_old_session(self):
        await _configure_hub()

        old_ws = await _register("key-pc1", host_label="Lab PC 1", project_path="C:/old")
        await _register("key-pc1", host_label="Lab PC 1", project_path="C:/new")

        (details,) = (await PluginHub.get_sessions()).sessions.values()
        assert details.project_path == "C:/new"
        old_ws.close.assert_awaited_once()


class TestEvictionLog:
    @pytest.mark.asyncio
    async def test_reconnect_from_same_host_logs_info(self, caplog):
        await _configure_hub()
        await _register("key-pc1", host_label="Lab PC 1")

        with caplog.at_level(logging.INFO, logger="transport.plugin_hub"):
            await _register("key-pc1", host_label="Lab PC 1")

        replaced = [r for r in caplog.records if "replaced" in r.getMessage()]
        assert len(replaced) == 1
        assert replaced[0].levelno == logging.INFO
        assert not [r for r in caplog.records if r.levelno >= logging.WARNING]

    @pytest.mark.asyncio
    async def test_eviction_by_another_host_warns_with_both_hosts(self, caplog):
        await _configure_hub()
        await _register("shared-key", host_label="Lab PC 1")

        with caplog.at_level(logging.INFO, logger="transport.plugin_hub"):
            await _register("shared-key", host_label="Lab PC 2")

        warnings = [r for r in caplog.records if r.levelno == logging.WARNING]
        assert len(warnings) == 1
        message = warnings[0].getMessage()
        assert "Lab PC 1" in message and "Lab PC 2" in message and "shared-key" in message

    @pytest.mark.asyncio
    async def test_reconnect_of_older_plugin_without_host_logs_info(self, caplog):
        await _configure_hub()
        await _register("shared-key", host_label=None)

        with caplog.at_level(logging.INFO, logger="transport.plugin_hub"):
            await _register("shared-key", host_label=None)

        assert [r for r in caplog.records if "replaced" in r.getMessage()]
        assert not [r for r in caplog.records if r.levelno >= logging.WARNING]

    @pytest.mark.asyncio
    async def test_new_host_taking_key_from_older_plugin_warns(self, caplog):
        await _configure_hub()
        await _register("shared-key", host_label=None)

        with caplog.at_level(logging.INFO, logger="transport.plugin_hub"):
            await _register("shared-key", host_label="Lab PC 2")

        assert [r for r in caplog.records if r.levelno == logging.WARNING]


async def _list_instances(monkeypatch):
    monkeypatch.setattr(config, "http_remote_hosted", False)
    monkeypatch.setattr(config, "transport_mode", "http")
    from services.resources.unity_instances import unity_instances
    from tests.integration.test_helpers import DummyContext

    return await unity_instances(DummyContext())


class TestInstancesResource:
    @pytest.mark.asyncio
    async def test_lists_host_path_and_groups_same_project(self, monkeypatch):
        await _configure_hub()
        await _register("key-pc1", host_label="Lab PC 1", project_guid="g" * 32, project_path="C:/a/Game")
        await _register("key-pc2", host_label="Lab PC 2", project_guid="g" * 32, project_path="D:/b/Game")
        await _register("key-other", host_label="Lab PC 1", project_guid="h" * 32,
                        project_path="C:/work/Other", project_name="Other")

        result = await _list_instances(monkeypatch)

        by_id = {i["id"]: i for i in result["instances"]}
        assert by_id["Game@key-pc1"]["host"] == "Lab PC 1"
        assert by_id["Game@key-pc2"]["host"] == "Lab PC 2"
        assert by_id["Game@key-pc2"]["path"] == "D:/b/Game"
        assert by_id["Other@key-other"]["project_guid"] == "h" * 32
        assert result["same_project"] == [{
            "project_guid": "g" * 32,
            "instances": [
                {"id": "Game@key-pc1", "name": "Game", "host": "Lab PC 1", "path": "C:/a/Game"},
                {"id": "Game@key-pc2", "name": "Game", "host": "Lab PC 2", "path": "D:/b/Game"},
            ],
        }]
        # The shared name is fully explained by same_project, so no separate warning.
        assert "warning" not in result

    @pytest.mark.asyncio
    async def test_same_name_different_projects_warns_without_grouping(self, monkeypatch):
        await _configure_hub()
        await _register("key-a", host_label="Lab PC 1", project_guid="g" * 32)
        await _register("key-b", host_label="Lab PC 2", project_guid="h" * 32)

        result = await _list_instances(monkeypatch)

        assert "same_project" not in result
        assert "Game" in result["warning"]

    @pytest.mark.asyncio
    async def test_older_plugins_without_guid_are_not_grouped(self, monkeypatch):
        await _configure_hub()
        await _register("key-a", host_label=None, project_guid=None)
        await _register("key-b", host_label=None, project_guid=None)

        result = await _list_instances(monkeypatch)

        assert "same_project" not in result
        assert "Game" in result["warning"]

    @pytest.mark.asyncio
    async def test_distinct_projects_have_no_grouping_or_warning(self, monkeypatch):
        await _configure_hub()
        await _register("key-a", host_label="Lab PC 1", project_guid="g" * 32)
        await _register("key-b", host_label="Lab PC 1", project_guid="h" * 32,
                        project_path="C:/work/Other", project_name="Other")

        result = await _list_instances(monkeypatch)

        assert result["instance_count"] == 2
        assert "same_project" not in result
        assert "warning" not in result


def _session(project, hash_, host):
    return SessionDetails(project=project, hash=hash_, unity_version="2022.3",
                          connected_at="2026-01-01T00:00:00", host_label=host)


class TestCliInstanceMatching:
    SESSIONS = {
        "s1": _session("Game", "key-pc1", "Lab PC 1"),
        "s2": _session("Game", "key-pc2", "Lab PC 2"),
        "s3": _session("Other", "key-other", "Lab PC 1"),
    }

    def _match(self, token):
        from main import _match_cli_session

        return _match_cli_session(self.SESSIONS, token)

    def test_full_id_routes_to_its_own_host_not_the_first_same_name(self):
        sid, details, error = self._match("Game@key-pc2")

        assert (sid, details.host_label, error) == ("s2", "Lab PC 2", None)

    def test_full_id_with_unknown_hash_does_not_fall_back_to_name(self):
        assert self._match("Game@missing") == (None, None, None)

    def test_bare_hash_matches(self):
        sid, _, error = self._match("key-other")

        assert (sid, error) == ("s3", None)

    def test_bare_name_shared_by_two_hosts_is_ambiguous(self):
        sid, _, error = self._match("Game")

        assert sid is None
        assert "Game@key-pc1" in error and "Game@key-pc2" in error

    def test_bare_unique_name_matches(self):
        sid, _, error = self._match("Other")

        assert (sid, error) == ("s3", None)
