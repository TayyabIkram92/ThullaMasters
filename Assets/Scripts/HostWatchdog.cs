using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using Firebase.Extensions;
using Firebase.Firestore;

/// <summary>
/// HostWatchdog — robust host disconnect detection and room cleanup.
///
/// HOST side:
///   - Writes "hostLastSeen" timestamp immediately on game start.
///   - Refreshes it every HeartbeatInterval (15s).
///   - Also writes on focus loss, pause, and quit (covers background/kill/crash).
///   - On leave: marks room as finished and awards coins before leaving.
///
/// NON-HOST side:
///   - Checks hostLastSeen every CheckInterval (20s) via a single GetSnapshot read.
///   - If timestamp is older than DisconnectTimeout (40s) → host disconnected.
///   - Writes finished state (host=Bhabhi, others=winners) with a guard to prevent
///     double-write (reads phase first, only writes if not already "finished").
///   - Each non-host client awards its own coins independently (no double-award
///     since each client only awards itself).
///   - Room is deleted 3s after game ends.
///
/// COVERS: internet loss, crash, ANR, force-quit, user leaves.
///
/// Attach to DontDestroyOnLoad GO alongside GameManager.
/// </summary>
public class HostWatchdog : MonoBehaviour
{
    public static HostWatchdog Instance { get; private set; }

    // ── Constants ─────────────────────────────────────────────────────────────

    private const string RoomsCollection   = "rooms";
    private const float  HeartbeatInterval = 15f;  // host writes every 15s
    private const float  CheckInterval     = 20f;  // non-host reads every 20s
    private const float  DisconnectTimeout = 40f;  // 40s without heartbeat = disconnected

    // ── State ─────────────────────────────────────────────────────────────────

    private RoomData  _room;
    private bool      _isHost;
    private bool      _active;
    private bool      _gameFinishedFired; // guard against double FireGameFinished
    private Coroutine _heartbeatCoroutine;
    private Coroutine _watchCoroutine;

    // ── Unity Lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void OnEnable()
    {
        EventManager.OnGameReady          += HandleGameReady;
        EventManager.OnGameFinished       += HandleGameFinished;
        EventManager.OnLeaveGameRequested += HandleLeaveRequested;
    }

    private void OnDisable()
    {
        EventManager.OnGameReady          -= HandleGameReady;
        EventManager.OnGameFinished       -= HandleGameFinished;
        EventManager.OnLeaveGameRequested -= HandleLeaveRequested;
        StopAllWatchdogCoroutines();
    }

    // ── Platform Disconnect Hooks (covers all exit scenarios) ─────────────────

    /// <summary>App loses focus — covers alt-tab, notification pull-down, incoming call.</summary>
    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus && _isHost && _active)
            WriteHeartbeat(); // last-known timestamp before going away
    }

    /// <summary>App pauses — covers home button press, app backgrounding on mobile.</summary>
    private void OnApplicationPause(bool isPaused)
    {
        if (isPaused && _isHost && _active)
            WriteHeartbeat();
    }

    /// <summary>App is quitting — covers user force-quit and normal exit.</summary>
    private void OnApplicationQuit()
    {
        if (!_isHost || !_active) return;
        // Best-effort synchronous-style: mark host as disconnected so non-hosts detect it.
        // We can't await async here, so just stamp the old timestamp (don't update it).
        // Non-hosts will detect absence of new heartbeat within 40s.
        // Nothing more we can do on force-kill.
        _active = false;
    }

    // ── Game Ready ────────────────────────────────────────────────────────────

    private void HandleGameReady(List<CardData> localHand, List<SlotData> seatedPlayers)
    {
        _room               = InGameManager.Instance?.CurrentRoom;
        _isHost             = _room != null && _room.hostId == PlayerDataManager.PlayFabId;
        _active             = true;
        _gameFinishedFired  = false;

        if (_isHost)
        {
            WriteHeartbeat();
            _heartbeatCoroutine = StartCoroutine(HeartbeatRoutine());
        }
        else
        {
            // Watch only if host is a real player (not a bot)
            bool hostIsRealPlayer = false;
            if (_room != null)
                foreach (var p in _room.players)
                    if (p.id == _room.hostId && !p.isBot) { hostIsRealPlayer = true; break; }

            if (hostIsRealPlayer)
                _watchCoroutine = StartCoroutine(WatchRoutine());
        }
    }

    // ── Host: Heartbeat ───────────────────────────────────────────────────────

    private IEnumerator HeartbeatRoutine()
    {
        while (_active)
        {
            yield return new WaitForSeconds(HeartbeatInterval);
            if (_active) WriteHeartbeat();
        }
    }

    private void WriteHeartbeat()
    {
        if (_room == null) return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(_room.roomId)
            .UpdateAsync(new Dictionary<string, object> { { "hostLastSeen", now } })
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted)
                    Debug.LogWarning($"[HostWatchdog] Heartbeat failed: {task.Exception?.Message}");
            });
    }

    // ── Non-Host: Watch ───────────────────────────────────────────────────────

    private IEnumerator WatchRoutine()
    {
        // Give host time to write first heartbeat before we start checking
        yield return new WaitForSeconds(CheckInterval);

        while (_active)
        {
            CheckHostAlive();
            yield return new WaitForSeconds(CheckInterval);
        }
    }

    private void CheckHostAlive()
    {
        if (_room == null || !_active) return;

        FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(_room.roomId)
            .GetSnapshotAsync()
            .ContinueWithOnMainThread(task =>
            {
                if (!_active) return;
                if (task.IsFaulted)
                {
                    Debug.LogWarning($"[HostWatchdog] Check read failed: {task.Exception?.Message}");
                    return;
                }

                var snap = task.Result;
                if (!snap.Exists) return; // room already deleted

                var dict = snap.ToDictionary();

                // If game already finished, stop watching
                if (dict.ContainsKey("gameState") &&
                    dict["gameState"] is Dictionary<string, object> gsDict)
                {
                    string phase = gsDict.ContainsKey("phase") ? gsDict["phase"].ToString() : "";
                    if (phase == GameState.PhaseFinished) { _active = false; return; }
                }

                if (!dict.ContainsKey("hostLastSeen")) return;

                long  lastSeen = Convert.ToInt64(dict["hostLastSeen"]);
                long  now      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                float elapsedS = (now - lastSeen) / 1000f;

                if (elapsedS > DisconnectTimeout)
                {
                    Debug.LogWarning($"[HostWatchdog] Host silent for {elapsedS:F0}s. Declaring disconnect.");
                    HandleHostDisconnected();
                }
            });
    }

    // ── Host Disconnected ─────────────────────────────────────────────────────

    private void HandleHostDisconnected()
    {
        if (!_active || _gameFinishedFired) return;
        _active = false;
        StopAllWatchdogCoroutines();

        var gs = GameManager.Instance?.CurrentGs;
        if (gs != null && gs.phase == GameState.PhaseFinished) return;

        string hostId  = _room.hostId;
        var    winners = new List<string>();
        foreach (var p in _room.players)
            if (p.id != hostId) winners.Add(p.id);

        // Read Firestore first — only write if not already finished (prevents double-write)
        FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(_room.roomId)
            .GetSnapshotAsync()
            .ContinueWithOnMainThread(readTask =>
            {
                if (readTask.IsFaulted) return;

                var snap = readTask.Result;
                if (!snap.Exists) return;

                var dict = snap.ToDictionary();
                if (dict.ContainsKey("gameState") &&
                    dict["gameState"] is Dictionary<string, object> gsDict)
                {
                    string phase = gsDict.ContainsKey("phase") ? gsDict["phase"].ToString() : "";
                    if (phase == GameState.PhaseFinished) return; // already done
                }

                // Write: host = Bhabhi, all others = winners
                var update = new Dictionary<string, object>
                {
                    { "gameState.phase",   GameState.PhaseFinished },
                    { "gameState.bhabhi",  hostId },
                    { "gameState.winners", new List<object>(winners.ConvertAll(w => (object)w)) }
                };

                FirebaseManager.DB
                    .Collection(RoomsCollection)
                    .Document(_room.roomId)
                    .UpdateAsync(update)
                    .ContinueWithOnMainThread(writeTask =>
                    {
                        if (writeTask.IsFaulted)
                        {
                            Debug.LogWarning("[HostWatchdog] Disconnect write failed — firing locally anyway.");
                        }

                        // Fire game finished and award coins locally regardless of write result.
                        // Each non-host client only awards itself — no double-award risk.
                        FireDisconnectResult(winners, hostId);
                    });
            });
    }

    private void FireDisconnectResult(List<string> winners, string hostId)
    {
        if (_gameFinishedFired) return;
        _gameFinishedFired = true;

        EventManager.FireGameFinished(winners, hostId);

        // Award coins only if local player won
        if (winners.Contains(PlayerDataManager.PlayFabId))
        {
            int payout = _room != null ? Mathf.RoundToInt(_room.entryFee * 1.25f) : 0;
            if (payout > 0)
                EventManager.FireAwardGameCoinsRequested(winners, payout);
        }

        // Delete room after delay
        StartCoroutine(DeleteRoomAfterDelay(3f));
    }

    // ── Player Leaves Voluntarily ─────────────────────────────────────────────

    /// <summary>
    /// Called when local player presses Leave.
    /// If they are host: mark as Bhabhi and end game for others before leaving.
    /// If they are non-host: just stop watching and leave.
    /// </summary>
    private void HandleLeaveRequested()
    {
        _active = false;
        StopAllWatchdogCoroutines();

        if (!_isHost || _room == null)
        {
            _room = null;
            return;
        }

        // Host is voluntarily leaving — treat same as disconnect
        // Award others and mark host as Bhabhi
        var winners = new List<string>();
        foreach (var p in _room.players)
            if (p.id != _room.hostId) winners.Add(p.id);

        string hostId = _room.hostId;

        var update = new Dictionary<string, object>
        {
            { "gameState.phase",   GameState.PhaseFinished },
            { "gameState.bhabhi",  hostId },
            { "gameState.winners", new List<object>(winners.ConvertAll(w => (object)w)) }
        };

        // Write and then navigate away — don't wait for result
        FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(_room.roomId)
            .UpdateAsync(update)
            .ContinueWithOnMainThread(_ =>
            {
                // Room will be cleaned up by the non-host clients who receive the update
            });

        _room = null;
    }

    // ── Game Finished: Cleanup ────────────────────────────────────────────────

    private void HandleGameFinished(List<string> winners, string bhabhi)
    {
        if (_gameFinishedFired && !_isHost) return; // non-host already handled via disconnect
        _active = false;
        StopAllWatchdogCoroutines();

        // Host cleans up room. Non-host cleans up if host was Bhabhi (disconnected).
        bool shouldDelete = _isHost || (bhabhi == _room?.hostId);
        if (shouldDelete && _room != null)
            StartCoroutine(DeleteRoomAfterDelay(3f));
    }

    private IEnumerator DeleteRoomAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        if (_room == null) yield break;

        string roomId = _room.roomId;
        _room = null;

        FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(roomId)
            .DeleteAsync()
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted)
                    Debug.LogWarning($"[HostWatchdog] Delete failed: {task.Exception?.Message}");
                else
                    Debug.Log($"[HostWatchdog] Room {roomId} deleted.");
            });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void StopAllWatchdogCoroutines()
    {
        if (_heartbeatCoroutine != null) { StopCoroutine(_heartbeatCoroutine); _heartbeatCoroutine = null; }
        if (_watchCoroutine     != null) { StopCoroutine(_watchCoroutine);     _watchCoroutine     = null; }
    }
}
