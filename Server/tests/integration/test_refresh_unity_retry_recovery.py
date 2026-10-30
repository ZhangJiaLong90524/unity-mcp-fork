import pytest

from models import MCPResponse
from services.state.external_changes_scanner import external_changes_scanner
from services.state.external_changes_scanner import ExternalChangesState

from .test_helpers import DummyContext


@pytest.mark.asyncio
async def test_refresh_unity_recovers_from_retry_disconnect(monkeypatch):
    """
    Option A: if Unity disconnects and the transport returns hint=retry, refresh_unity(wait_for_ready=true)
    should poll readiness and then return success + clear external dirty.
    """
    from services.tools.refresh_unity import refresh_unity

    ctx = DummyContext()
    await ctx.set_state("unity_instance", "UnityMCPTests@cc8756d4cce0805a")

    # Seed dirty state
    inst = "UnityMCPTests@cc8756d4cce0805a"
    external_changes_scanner._states[inst] = ExternalChangesState(dirty=True, dirty_since_unix_ms=1)

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        if command_type == "refresh_unity":
            return {"success": False, "error": "disconnected", "hint": "retry"}
        elif command_type == "get_editor_state":
            return {"success": True, "data": {"advice": {"ready_for_tools": True}}}
        raise ValueError(f"Unexpected command: {command_type}")

    import services.tools.refresh_unity as refresh_mod
    monkeypatch.setattr(refresh_mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await refresh_unity(ctx, wait_for_ready=True)
    payload = resp.model_dump() if hasattr(resp, "model_dump") else resp
    assert payload["success"] is True
    assert payload.get("data", {}).get("recovered_from_disconnect") is True

    # Dirty should be cleared
    assert external_changes_scanner._states[inst].dirty is False


def _no_session(reason="no_unity_session"):
    return {
        "success": False,
        "error": "Unity session not available; please retry",
        "hint": "retry",
        "data": {"reason": reason, "retry_after_ms": 250},
    }


@pytest.mark.asyncio
@pytest.mark.parametrize("reason", ["no_unity_session", "stale_connection"])
async def test_refresh_unity_resends_with_reconnect_wait_when_never_sent(monkeypatch, reason):
    """A target between sessions is re-sent once with the hub's reconnect wait,
    instead of falling into the readiness poll as if Unity had received it."""
    from services.tools.refresh_unity import refresh_unity
    import services.tools.refresh_unity as refresh_mod

    ctx = DummyContext()
    await ctx.set_state("unity_instance", "UnityMCPTests@cc8756d4cce0805a")

    calls = []

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        calls.append(kwargs.get("retry_on_reload"))
        if len(calls) == 1:
            return _no_session(reason)
        return {"success": True, "message": "Refresh requested.", "data": {"refresh_triggered": True}}

    monkeypatch.setattr(refresh_mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await refresh_unity(ctx, wait_for_ready=True)
    payload = resp.model_dump() if hasattr(resp, "model_dump") else resp
    assert payload["success"] is True
    assert payload.get("data", {}).get("refresh_triggered") is True
    assert calls == [False, True]


@pytest.mark.asyncio
async def test_refresh_unity_reports_unconnected_instance(monkeypatch):
    """When the target still has no session after the reconnect wait, the refresh was never
    sent: report that with the connected instances rather than a readiness timeout."""
    from services.tools.refresh_unity import refresh_unity
    import services.tools.refresh_unity as refresh_mod

    ctx = DummyContext()
    await ctx.set_state("unity_instance", "Gone@0000000000000000")

    calls = []

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        calls.append(kwargs.get("retry_on_reload"))
        return _no_session()

    class _Session:
        def __init__(self, project, hash_):
            self.project = project
            self.hash = hash_

    class _SessionList:
        sessions = {"s1": _Session("UnityMCPTests", "cc8756d4cce0805a")}

    async def fake_get_sessions(user_id=None):
        return _SessionList()

    monkeypatch.setattr(refresh_mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)
    monkeypatch.setattr(refresh_mod.PluginHub, "get_sessions", fake_get_sessions)

    resp = await refresh_unity(ctx, wait_for_ready=True)
    payload = resp.model_dump() if hasattr(resp, "model_dump") else resp
    assert payload["success"] is False
    assert payload["hint"] == "select_instance"
    assert payload["data"]["reason"] == "instance_not_connected"
    assert payload["data"]["unity_instance"] == "Gone@0000000000000000"
    assert payload["data"]["available_instances"] == ["UnityMCPTests@cc8756d4cce0805a"]
    assert calls == [False, True]


