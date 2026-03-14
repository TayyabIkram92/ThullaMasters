using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using Firebase.Extensions;
using Firebase.Firestore;
using UnityEngine.SceneManagement;

/// <summary>
/// HostWatchdog — in-game disconnect detection (after OnGameReady fires).
/// MatchmakingManager handles pre-game disconnect; this handles in-game.
///
/// ALL CLIENTS:
///   - Write playerLastSeen/{myId} every HeartbeatInterval (15s).
///   - OnApplicationPause / OnApplicationFocus(false) → immediate self-disconnect.
///
/// HOST:
///   - Also writes hostLastSeen every 15s.
///   - Polls playerLastSeen for all non-host players every CheckInterval (20s).
///   - On non-host dropout → GameManager.ForceRemovePlayer (game continues).
///
/// NON-HOST:
///   - Polls hostLastSeen every CheckInterval (20s).
///   - On host dropout → writes finished state then shows WinView.
///
/// SELF-DISCONNECT (pause/background/lock/force-quit during in-game):
///   - Coins already deducted. We lose. Zero heartbeat, reload scene.
///   - Remaining players detect our drop and show WinView on their devices.
/// </summary>
public class HostWatchdog : MonoBehaviour
{
    public static HostWatchdog Instance { get; private set; }

    private const string RoomsCollection = "rooms";
    private const float HeartbeatInterval = 8f; // write every 8s

    private const float
        CheckInterval = 10f; // poll peers every 10s in-game (slower than matchmaking is fine — game is already running)

    private const float DisconnectTimeout = 25f; // 25s stale = disconnected

    private RoomData _room;
    private bool _isHost;
    private bool _active;
    private bool _resultFired;
    private Coroutine _heartbeatCoroutine;
    private Coroutine _watchCoroutine;

    // ── Unity Lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    private void OnEnable()
    {
        EventManager.OnGameReady += HandleGameReady;
        EventManager.OnGameFinished += HandleGameFinished;
        EventManager.OnLeaveGameRequested += HandleLeaveRequested;
    }

    private void OnDisable()
    {
        EventManager.OnGameReady -= HandleGameReady;
        EventManager.OnGameFinished -= HandleGameFinished;
        EventManager.OnLeaveGameRequested -= HandleLeaveRequested;
        StopAllCoroutines();
    }

    // ── Platform Hooks — immediate disconnect ─────────────────────────────────

    private void OnApplicationPause(bool isPaused)
    {
        if (!isPaused || !_active) return;
        Debug.Log("[HostWatchdog] App paused — immediate self-disconnect.");
        ZeroMyHeartbeat();
        HandleSelfDisconnect();
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus || !_active) return;
        Debug.Log("[HostWatchdog] App lost focus — immediate self-disconnect.");
        ZeroMyHeartbeat();
        HandleSelfDisconnect();
    }

    // ── Game Ready ────────────────────────────────────────────────────────────

    private void HandleGameReady(List<CardData> localHand, List<SlotData> seatedPlayers)
    {
        _room = InGameManager.Instance?.CurrentRoom;
        _isHost = _room != null && _room.hostId == PlayerDataManager.PlayFabId;
        _active = true;
        _resultFired = false;

        WriteMyHeartbeat();
        _heartbeatCoroutine = StartCoroutine(HeartbeatRoutine());

        bool shouldWatch = _isHost; // host always watches its non-host players
        if (!_isHost && _room != null)
        {
            // non-host watches only if host is a real player (not a bot)
            foreach (var p in _room.players)
                if (p.id == _room.hostId && !p.isBot)
                {
                    shouldWatch = true;
                    break;
                }
        }

        if (shouldWatch)
            _watchCoroutine = StartCoroutine(WatchRoutine());
    }

    // ── Heartbeat ─────────────────────────────────────────────────────────────

    private IEnumerator HeartbeatRoutine()
    {
        while (_active)
        {
            yield return new WaitForSeconds(HeartbeatInterval);
            if (!_active) yield break;
            WriteMyHeartbeat();
        }
    }

    private void WriteMyHeartbeat()
    {
        if (_room == null || FirebaseManager.DB == null) return;
        string myId = PlayerDataManager.PlayFabId;
        if (string.IsNullOrEmpty(myId)) return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var update = new Dictionary<string, object> { { $"playerLastSeen.{myId}", now } };
        if (_isHost) update["hostLastSeen"] = now;

        FirebaseManager.DB.Collection(RoomsCollection).Document(_room.roomId)
            .UpdateAsync(update)
            .ContinueWithOnMainThread(t =>
            {
                if (t.IsFaulted)
                    Debug.LogWarning("[HostWatchdog] Heartbeat failed: " + t.Exception?.Message);
            });
    }

    private void ZeroMyHeartbeat()
    {
        if (_room == null || FirebaseManager.DB == null) return;
        string myId = PlayerDataManager.PlayFabId;
        if (string.IsNullOrEmpty(myId)) return;

        var update = new Dictionary<string, object> { { $"playerLastSeen.{myId}", 0L } };
        if (_isHost) update["hostLastSeen"] = 0L;

        // Fire-and-forget — we're about to reload
        FirebaseManager.DB.Collection(RoomsCollection).Document(_room.roomId).UpdateAsync(update);
    }

    // ── Watch Routine ─────────────────────────────────────────────────────────

    private IEnumerator WatchRoutine()
    {
        yield return new WaitForSeconds(CheckInterval);
        while (_active)
        {
            if (_isHost) CheckAllNonHostPlayers();
            else CheckHost();
            yield return new WaitForSeconds(CheckInterval);
        }
    }

    // ── Host: Watch Non-Host Players ──────────────────────────────────────────

    private void CheckAllNonHostPlayers()
    {
        if (_room == null || !_active) return;

        FirebaseManager.DB.Collection(RoomsCollection).Document(_room.roomId)
            .GetSnapshotAsync()
            .ContinueWithOnMainThread(task =>
            {
                if (!_active || task.IsFaulted) return;
                var snap = task.Result;
                if (!snap.Exists) return;

                var dict = snap.ToDictionary();
                if (IsFinished(dict))
                {
                    _active = false;
                    return;
                }

                if (!dict.ContainsKey("playerLastSeen") ||
                    !(dict["playerLastSeen"] is Dictionary<string, object> playerTs)) return;

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                foreach (var p in _room.players)
                {
                    if (p.isBot || p.id == PlayerDataManager.PlayFabId) continue;
                    if (!playerTs.ContainsKey(p.id)) continue;

                    long ts = Convert.ToInt64(playerTs[p.id]);
                    float elapsed = (now - ts) / 1000f;

                    if (ts == 0L || elapsed > DisconnectTimeout)
                    {
                        // Verify player still in active game before forcing
                        var gs = GameManager.Instance?.CurrentGs;
                        if (gs == null || gs.phase == GameState.PhaseFinished) continue;
                        if (!gs.activePlayers.Contains(p.id)) continue;

                        Debug.LogWarning($"[HostWatchdog] Player {p.id} dropped out ({elapsed:F0}s). Force removing.");
                        GameManager.Instance.ForceRemovePlayer(p.id);
                    }
                }
            });
    }

    // ── Non-Host: Watch Host ──────────────────────────────────────────────────

    private void CheckHost()
    {
        if (_room == null || !_active) return;

        FirebaseManager.DB.Collection(RoomsCollection).Document(_room.roomId)
            .GetSnapshotAsync()
            .ContinueWithOnMainThread(task =>
            {
                if (!_active || task.IsFaulted) return;
                var snap = task.Result;
                if (!snap.Exists) return;

                var dict = snap.ToDictionary();
                if (IsFinished(dict))
                {
                    _active = false;
                    return;
                }

                if (!dict.ContainsKey("hostLastSeen")) return;

                long ts = Convert.ToInt64(dict["hostLastSeen"]);
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                float elapsed = (now - ts) / 1000f;

                if (ts == 0L || elapsed > DisconnectTimeout)
                {
                    Debug.LogWarning($"[HostWatchdog] Host dropped out ({elapsed:F0}s).");
                    HandleHostDisconnected();
                }
            });
    }

    // ── Host Disconnected (non-host writes result) ────────────────────────────

    private void HandleHostDisconnected()
    {
        if (!_active || _resultFired) return;
        _active = false;
        _resultFired = true;
        StopAllCoroutines();

        var gs = GameManager.Instance?.CurrentGs;
        if (gs != null && gs.phase == GameState.PhaseFinished) return;

        string hostId = _room.hostId;
        var winners = new List<string>();
        if (_room.players != null)
            foreach (var p in _room.players)
                if (p.id != hostId)
                    winners.Add(p.id);

        // Guard read before write
        FirebaseManager.DB.Collection(RoomsCollection).Document(_room.roomId)
            .GetSnapshotAsync()
            .ContinueWithOnMainThread(readTask =>
            {
                if (readTask.IsFaulted || !readTask.Result.Exists)
                {
                    DeliverResult(winners, hostId);
                    return;
                }

                if (IsFinished(readTask.Result.ToDictionary()))
                {
                    DeliverResult(winners, hostId);
                    return;
                } // already written, just show result

                var update = new Dictionary<string, object>
                {
                    { "gameState.phase", GameState.PhaseFinished },
                    { "gameState.bhabhi", hostId },
                    { "gameState.winners", new List<object>(winners.ConvertAll(w => (object)w)) }
                };

                FirebaseManager.DB.Collection(RoomsCollection).Document(_room.roomId)
                    .UpdateAsync(update)
                    .ContinueWithOnMainThread(_ => DeliverResult(winners, hostId));
            });
    }

    // ── Self Disconnect ───────────────────────────────────────────────────────

    // AFTER:
    private void HandleSelfDisconnect()
    {
        if (!_active) return;
        _active = false;
        StopAllCoroutines();

        if (_room != null)
        {
            string myId = PlayerDataManager.PlayFabId;
            var winners = new List<object>();
            if (_room.players != null)
                foreach (var p in _room.players)
                    if (p.id != myId)
                        winners.Add((object)p.id);

            FirebaseManager.DB.Collection(RoomsCollection).Document(_room.roomId)
                .UpdateAsync(new Dictionary<string, object>
                {
                    { "gameState.phase", GameState.PhaseFinished },
                    { "gameState.bhabhi", myId },
                    { "gameState.winners", winners }
                });
        }

        _room = null;
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    // ── Voluntary Leave ───────────────────────────────────────────────────────

    // AFTER:
    private void HandleLeaveRequested()
    {
        if (!_active)
        {
            _room = null;
            return;
        }

        _active = false;
        StopAllCoroutines();

        ZeroMyHeartbeat();

        if (_room != null)
        {
            string myId = PlayerDataManager.PlayFabId;
            var winners = new List<object>();
            if (_room.players != null)
                foreach (var p in _room.players)
                    if (p.id != myId)
                        winners.Add((object)p.id);

            FirebaseManager.DB.Collection(RoomsCollection).Document(_room.roomId)
                .UpdateAsync(new Dictionary<string, object>
                {
                    { "gameState.phase", GameState.PhaseFinished },
                    { "gameState.bhabhi", myId },
                    { "gameState.winners", winners }
                });
        }

        _room = null;
    }

    // ── Normal Game Finished ──────────────────────────────────────────────────

    private void HandleGameFinished(List<string> winners, string bhabhi)
    {
        if (_resultFired) return;
        _resultFired = true;
        _active = false;
        StopAllCoroutines();

        bool shouldDelete = _isHost || bhabhi == _room?.hostId;
        if (shouldDelete && _room != null)
            StartCoroutine(DeleteRoomAfterDelay(3f));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void DeliverResult(List<string> winners, string bhabhi)
    {
        if (_resultFired) return;
        _resultFired = true;
        EventManager.FireGameFinished(winners, bhabhi);
        StartCoroutine(DeleteRoomAfterDelay(3f));
    }

    private IEnumerator DeleteRoomAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        if (_room == null) yield break;
        string roomId = _room.roomId;
        _room = null;
        if (FirebaseManager.DB == null) yield break;
        FirebaseManager.DB.Collection(RoomsCollection).Document(roomId)
            .DeleteAsync()
            .ContinueWithOnMainThread(t =>
            {
                if (t.IsFaulted) Debug.LogWarning("[HostWatchdog] Delete failed: " + t.Exception?.Message);
                else Debug.Log("[HostWatchdog] Room " + roomId + " deleted.");
            });
    }

    private static bool IsFinished(Dictionary<string, object> dict)
    {
        if (!dict.ContainsKey("gameState")) return false;
        if (!(dict["gameState"] is Dictionary<string, object> gs)) return false;
        return gs.ContainsKey("phase") && gs["phase"].ToString() == GameState.PhaseFinished;
    }
}