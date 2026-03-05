using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using Firebase.Extensions;
using Firebase.Firestore;

/// <summary>
/// HostWatchdog — handles host disconnect detection and room cleanup.
///
/// Strategy (minimal Firestore reads/writes):
///   - Host writes "hostLastSeen" timestamp every 15s via a heartbeat coroutine.
///   - Host also writes it immediately on ApplicationFocus/Pause/Quit.
///   - Non-host clients check "hostLastSeen" every 20s (one read).
///     If it's older than 40s → host is considered disconnected.
///   - On disconnect: non-host clients end the game locally.
///     Host is marked Bhabhi, all others are winners.
///     If there are real (non-bot) players, they each write the result
///     using a simple "first writer wins" approach (check phase != finished first).
///   - Room document is deleted after game ends (winners + bhabhi confirmed).
///
/// Attach to DontDestroyOnLoad GO alongside GameManager.
/// </summary>
public class HostWatchdog : MonoBehaviour
{
    public static HostWatchdog Instance { get; private set; }

    // ── Constants ─────────────────────────────────────────────────────────────

    private const string RoomsCollection    = "rooms";
    private const float  HeartbeatInterval  = 15f;   // host writes every 15s
    private const float  CheckInterval      = 20f;   // non-host checks every 20s
    private const float  DisconnectTimeout  = 40f;   // considered disconnected after 40s

    // ── State ─────────────────────────────────────────────────────────────────

    private RoomData  _room;
    private bool      _isHost;
    private bool      _active;
    private Coroutine _heartbeatCoroutine;
    private Coroutine _watchCoroutine;

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void OnEnable()
    {
        EventManager.OnGameReady          += HandleGameReady;
        EventManager.OnGameFinished       += HandleGameFinished;
        EventManager.OnLeaveGameRequested += HandleLeave;
    }

    private void OnDisable()
    {
        EventManager.OnGameReady          -= HandleGameReady;
        EventManager.OnGameFinished       -= HandleGameFinished;
        EventManager.OnLeaveGameRequested -= HandleLeave;
        Stop();
    }

    // Host loses focus / pauses (mobile background) / quits
    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus && _isHost && _active)
            WriteHeartbeat();   // last known timestamp before going background
    }

    private void OnApplicationPause(bool isPaused)
    {
        if (isPaused && _isHost && _active)
            WriteHeartbeat();
    }

    private void OnApplicationQuit()
    {
        if (_isHost && _active)
            WriteHeartbeat();   // best-effort on quit
    }

    // ── Init ──────────────────────────────────────────────────────────────────

    private void HandleGameReady(List<CardData> localHand, List<SlotData> seatedPlayers)
    {
        _room   = InGameManager.Instance?.CurrentRoom;
        _isHost = _room != null && _room.hostId == PlayerDataManager.PlayFabId;
        _active = true;

        if (_isHost)
        {
            // Write first heartbeat immediately
            WriteHeartbeat();
            _heartbeatCoroutine = StartCoroutine(HeartbeatRoutine());
        }
        else
        {
            // Only start watching if there are real non-bot players as host
            // (if host is bot-only we don't need to watch)
            bool hostIsReal = false;
            foreach (var p in _room.players)
                if (p.id == _room.hostId && !p.isBot) { hostIsReal = true; break; }

            if (hostIsReal)
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
        var update = new Dictionary<string, object>
        {
            { "hostLastSeen", now }
        };

        FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(_room.roomId)
            .UpdateAsync(update)
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted)
                    Debug.LogWarning($"[HostWatchdog] Heartbeat write failed: {task.Exception?.Message}");
            });
    }

    // ── Non-Host: Watch ───────────────────────────────────────────────────────

    private IEnumerator WatchRoutine()
    {
        // Wait one full interval before first check
        // (give host time to write first heartbeat)
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
                    Debug.LogWarning($"[HostWatchdog] Check failed: {task.Exception?.Message}");
                    return;
                }

                var snap = task.Result;
                if (!snap.Exists) return;

                var dict = snap.ToDictionary();
                if (!dict.ContainsKey("hostLastSeen")) return;

                long lastSeen = Convert.ToInt64(dict["hostLastSeen"]);
                long now      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                float elapsed = (now - lastSeen) / 1000f;

                if (elapsed > DisconnectTimeout)
                {
                    Debug.LogWarning($"[HostWatchdog] Host disconnected ({elapsed:F0}s ago). Ending game.");
                    HandleHostDisconnected();
                }
            });
    }

    private void HandleHostDisconnected()
    {
        if (!_active) return;
        _active = false;
        Stop();

        // Host is Bhabhi — all others win
        // Only act if game is still in playing state
        var gs = GameManager.Instance?.CurrentGs;
        if (gs == null || gs.phase == GameState.PhaseFinished) return;

        string hostId  = _room.hostId;
        var    winners = new List<string>();

        foreach (var p in _room.players)
            if (p.id != hostId) winners.Add(p.id);

        // Write result to Firestore (best effort, first writer wins)
        // Check current phase first to avoid double-write
        FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(_room.roomId)
            .GetSnapshotAsync()
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted) return;

                var snap = task.Result;
                if (!snap.Exists) return;

                var dict = snap.ToDictionary();
                if (dict.ContainsKey("gameState") &&
                    dict["gameState"] is Dictionary<string, object> gsDict)
                {
                    string phase = gsDict.ContainsKey("phase")
                                   ? gsDict["phase"].ToString() : "";
                    if (phase == GameState.PhaseFinished) return; // already finished
                }

                // Write finished state
                var finishedGs = new Dictionary<string, object>
                {
                    { "phase",   GameState.PhaseFinished },
                    { "bhabhi",  hostId                  },
                    { "winners", new List<object>(winners.ConvertAll(w => (object)w)) }
                };

                var update = new Dictionary<string, object>
                {
                    { "gameState.phase",   GameState.PhaseFinished },
                    { "gameState.bhabhi",  hostId                  },
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
                            Debug.LogWarning("[HostWatchdog] Could not write disconnect result.");
                            return;
                        }

                        // Fire locally
                        EventManager.FireGameFinished(winners, hostId);

                        // Award coins to local player if they are a winner
                        if (winners.Contains(PlayerDataManager.PlayFabId))
                        {
                            int payout = Mathf.RoundToInt(_room.entryFee * 1.25f);
                            EventManager.FireAwardGameCoinsRequested(winners, payout);
                        }
                    });
            });
    }

    // ── Game Finished: Room Cleanup (Bug 6) ───────────────────────────────────

    private void HandleGameFinished(List<string> winners, string bhabhi)
    {
        _active = false;
        Stop();

        // Only host deletes the room to avoid race conditions
        // If host disconnected, the last non-host real player cleans up
        bool shouldCleanup = _isHost;

        if (!shouldCleanup)
        {
            // Check if host is the bhabhi (disconnected) — then we clean up
            shouldCleanup = (bhabhi == _room?.hostId);
        }

        if (shouldCleanup && _room != null)
            StartCoroutine(DeleteRoomAfterDelay(3f));
    }

    private IEnumerator DeleteRoomAfterDelay(float delay)
    {
        // Wait a bit so all clients have time to receive the finished state
        yield return new WaitForSeconds(delay);

        if (_room == null) yield break;

        FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(_room.roomId)
            .DeleteAsync()
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted)
                    Debug.LogWarning($"[HostWatchdog] Room delete failed: {task.Exception?.Message}");
                else
                    Debug.Log($"[HostWatchdog] Room {_room.roomId} deleted.");
            });
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void Stop()
    {
        if (_heartbeatCoroutine != null) { StopCoroutine(_heartbeatCoroutine); _heartbeatCoroutine = null; }
        if (_watchCoroutine     != null) { StopCoroutine(_watchCoroutine);     _watchCoroutine     = null; }
    }

    private void HandleLeave()
    {
        _active = false;
        Stop();
        _room = null;
    }
}
