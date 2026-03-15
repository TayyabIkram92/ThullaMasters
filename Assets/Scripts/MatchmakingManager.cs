using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Firebase.Extensions;
using Firebase.Firestore;
using UnityEngine;
using UnityEngine.SceneManagement;
using Random = UnityEngine.Random;

/// <summary>
/// Handles Firestore matchmaking and ALL disconnect scenarios during matchmaking
/// and dealing phases (before OnGameReady fires — HostWatchdog is not yet running).
///
/// HEARTBEAT SYSTEM (matchmaking phase):
/// ─────────────────────────────────────
/// Every player writes playerLastSeen/{myId} to the room the moment they join.
/// Refreshed every HeartbeatInterval (8s).
/// OnApplicationPause / OnApplicationFocus(false) immediately zeros the heartbeat
/// AND calls HandleSelfDisconnect for immediate local cleanup + scene reload.
///
/// DISCONNECT RULES:
/// ─────────────────
/// Before coins deducted (room.players.Count < 4):
///   → Delete room entirely. All players reload scene.
///
/// After coins deducted (room.players.Count >= 4, status = "starting"/"dealing"):
///   → Leaver is Bhabhi (loses). All remaining players are winners → WinView.
///   → Room deleted after WinView claims.
///
/// ALL CLIENTS watch ALL other players every CheckInterval (5s).
/// Every client independently detects every dropout — not just host watching non-hosts.
/// _disconnectHandled guard prevents double-fire from concurrent detections.
/// AllPlayersAlive() validated before FireMatchFound to close the dropout-vs-start race.
/// </summary>
public class MatchmakingManager : MonoBehaviour
{
    private const string RoomsCollection = "rooms";
    private const int MaxPlayers = 4;
    private const float BotFillDelayMin = 5f;
    private const float BotFillDelayMax = 10f;
    private const float HeartbeatInterval = 8f; // write every 8s
    private const float CheckInterval = 5f; // poll peers every 5s — fast enough to catch dropout before match starts
    private const float DisconnectTimeout = 20f; // 20s stale = disconnected (verified by 4 missed checks)

    // ── Bot name pool ─────────────────────────────────────────────────────────
    private string[] _botNames;
    private int _botCounter = 0;
    private int _botNameIndex = 0;

    // ── Room state ────────────────────────────────────────────────────────────
    private string _currentRoomId;
    private RoomData _currentRoom;
    private bool _isHost;

    // ── Matchmaking phase disconnect tracking ─────────────────────────────────
    // Active from the moment we join/create a room until OnMatchFound + OnGameReady fires.
    private bool _watchdogActive; // true while we are in a room pre-game
    private bool _coinsDeducted; // mirror of MatchmakingView._hasDeductedCoins
    private bool _disconnectHandled; // guard against double handling
    private Coroutine _heartbeatCoroutine;
    private Coroutine _watchCoroutine;
    private ListenerRegistration _roomListener;
    private Coroutine _botFillCoroutine;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void OnEnable()
    {
        EventManager.OnMatchmakingStartRequested += HandleMatchmakingStart;
        EventManager.OnMatchmakingCancelRequested += HandleMatchmakingCancel;
        EventManager.OnAcceptInviteRequested += HandleAcceptInvite;
        EventManager.OnLeaveRoomRequested += HandleLeaveRoom;
        EventManager.OnFirebaseReady += OnFirebaseReady;
        EventManager.OnDeductCoinsRequested += HandleCoinsDeducted;
        EventManager.OnMatchFound += HandleMatchFound;
    }

    private void OnDisable()
    {
        EventManager.OnMatchmakingStartRequested -= HandleMatchmakingStart;
        EventManager.OnMatchmakingCancelRequested -= HandleMatchmakingCancel;
        EventManager.OnAcceptInviteRequested -= HandleAcceptInvite;
        EventManager.OnLeaveRoomRequested -= HandleLeaveRoom;
        EventManager.OnFirebaseReady -= OnFirebaseReady;
        EventManager.OnDeductCoinsRequested -= HandleCoinsDeducted;
        EventManager.OnMatchFound -= HandleMatchFound;

        CleanupRoom();
    }

    // ── Platform hooks — IMMEDIATE disconnect on pause (backgrounded/locked) ──

    /// <summary>
    /// OnApplicationPause fires when the app is backgrounded, the lock button
    /// is pressed, or the task-switcher is opened. Per spec: treat as immediate
    /// disconnect if we are in an active room.
    /// </summary>
    private void OnApplicationPause(bool isPaused)
    {
        if (!isPaused || !_watchdogActive) return;

        Debug.Log("[MatchmakingManager] App paused — declaring immediate disconnect.");

        // Zero our heartbeat so peers detect the drop on their next poll
        ZeroMyHeartbeat();

        // Handle as if we just disconnected
        HandleSelfDisconnect();
    }

    /// <summary>
    /// OnApplicationFocus(false) fires on some platforms where Pause doesn't
    /// (e.g. PC alt-tab). Treat identically to pause.
    /// </summary>
    private void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus || !_watchdogActive) return;

        Debug.Log("[MatchmakingManager] App lost focus — declaring immediate disconnect.");
        ZeroMyHeartbeat();
        HandleSelfDisconnect();
    }

    // ── Firebase ready ────────────────────────────────────────────────────────

    private void OnFirebaseReady() => LoadBotNames();

    // ── Coin deduction tracking ───────────────────────────────────────────────

    private void HandleCoinsDeducted(int amount)
    {
        _coinsDeducted = true;
        Debug.Log("[MatchmakingManager] Coins deducted — disconnect rules changed to win/lose.");
    }

    // ── Match found — stop matchmaking watchdog, hand off to HostWatchdog ─────

    private void HandleMatchFound(RoomData room)
    {
        // Once the game starts, HostWatchdog takes over disconnect detection.
        // Stop our matchmaking-phase watchdog coroutines but keep _currentRoom
        // alive so CleanupRoom can still delete it if needed.
        StopWatchdogCoroutines();
        _watchdogActive = false;
        // Do NOT zero _coinsDeducted or _currentRoom here — HostWatchdog needs them.
    }

    // ─── Start Matchmaking ────────────────────────────────────────────────────

    private void HandleMatchmakingStart(GameModeData mode)
    {
        var db = FirebaseManager.DB;
        if (db == null)
        {
            EventManager.FireMatchmakingError("Firebase not ready.");
            return;
        }

        _coinsDeducted = false;
        _disconnectHandled = false;

        string existingRoomId = InviteManager.CurrentRoomId;
        if (!string.IsNullOrEmpty(existingRoomId))
        {
            _isHost = true;
            _currentRoomId = existingRoomId;
            _currentRoom = new RoomData
            {
                roomId = existingRoomId,
                hostId = PlayerDataManager.PlayFabId,
                entryFee = mode != null ? mode.EntryFee : 0,
                status = "waiting",
                players = new List<SlotData> { BuildLocalSlot() }
            };
            StartMatchmakingWatchdog();
            StartListening(existingRoomId);
            _botFillCoroutine = StartCoroutine(BotFillRoutine(existingRoomId));
            return;
        }

        db.Collection(RoomsCollection)
            .WhereEqualTo("status", "waiting")
            .WhereEqualTo("entryFee", mode.EntryFee)
            .Limit(1)
            .GetSnapshotAsync()
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    EventManager.FireMatchmakingError("Search failed.");
                    return;
                }

                QuerySnapshot snap = task.Result;
                DocumentSnapshot firstDoc = null;
                if (snap.Count > 0)
                    foreach (var doc in snap.Documents)
                    {
                        firstDoc = doc;
                        break;
                    }

                if (firstDoc != null) JoinRoom(firstDoc, mode);
                else CreateRoom(mode);
            });
    }

    // ─── Accept Invite ────────────────────────────────────────────────────────

    private void HandleAcceptInvite(string roomId)
    {
        var db = FirebaseManager.DB;
        if (db == null) return;

        _coinsDeducted = false;
        _disconnectHandled = false;

        db.Collection(RoomsCollection)
            .Document(roomId)
            .GetSnapshotAsync()
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted || task.IsCanceled || !task.Result.Exists)
                {
                    if (GameModeManager.SelectedMode != null)
                        HandleMatchmakingStart(GameModeManager.SelectedMode);
                    return;
                }

                JoinRoom(task.Result, GameModeManager.SelectedMode);
            });
    }

    // ─── Create Room ──────────────────────────────────────────────────────────

    private void CreateRoom(GameModeData mode)
    {
        _isHost = true;
        string roomId = Guid.NewGuid().ToString("N");
        var localPlayer = BuildLocalSlot();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var roomData = new Dictionary<string, object>
        {
            { "hostId", PlayerDataManager.PlayFabId },
            { "entryFee", mode != null ? mode.EntryFee : 0 },
            { "status", "waiting" },
            { "players", new List<object> { SlotToDict(localPlayer) } },
            { "hostLastSeen", now },
            {
                "playerLastSeen", new Dictionary<string, object>
                    { { PlayerDataManager.PlayFabId, now } }
            }
        };

        FirebaseManager.DB.Collection(RoomsCollection)
            .Document(roomId)
            .SetAsync(roomData)
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted)
                {
                    EventManager.FireMatchmakingError("Room creation failed.");
                    return;
                }

                _currentRoomId = roomId;
                _currentRoom = new RoomData
                {
                    roomId = roomId,
                    hostId = PlayerDataManager.PlayFabId,
                    entryFee = mode != null ? mode.EntryFee : 0,
                    status = "waiting",
                    players = new List<SlotData> { localPlayer }
                };

                InviteManager.SetCurrentRoomId(roomId);
                StartMatchmakingWatchdog();
                StartListening(roomId);
                _botFillCoroutine = StartCoroutine(BotFillRoutine(roomId));
            });
    }

    // ─── Join Room ────────────────────────────────────────────────────────────

    private void JoinRoom(DocumentSnapshot doc, GameModeData mode)
    {
        _isHost = false;
        string roomId = doc.Id;
        var localPlayer = BuildLocalSlot();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        FirebaseManager.DB.Collection(RoomsCollection)
            .Document(roomId)
            .UpdateAsync(new Dictionary<string, object>
            {
                { "players", FieldValue.ArrayUnion(SlotToDict(localPlayer)) },
                { $"playerLastSeen.{PlayerDataManager.PlayFabId}", now }
            })
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted)
                {
                    EventManager.FireMatchmakingError("Join failed.");
                    return;
                }

                _currentRoomId = roomId;
                _currentRoom = ParseRoomDoc(doc);
                if (_currentRoom.players == null) _currentRoom.players = new List<SlotData>();
                _currentRoom.players.Add(localPlayer);

                InviteManager.SetCurrentRoomId(roomId);
                StartMatchmakingWatchdog();
                StartListening(roomId);
            });
    }

    // ─── Room Listener ────────────────────────────────────────────────────────

    private void StartListening(string roomId)
    {
        _roomListener?.Stop();
        _roomListener = FirebaseManager.DB.Collection(RoomsCollection)
            .Document(roomId)
            .Listen(snapshot =>
            {
                if (!snapshot.Exists)
                {
                    // Room was deleted externally (another client cleaned it up)
                    if (_watchdogActive)
                    {
                        Debug.Log("[MatchmakingManager] Room deleted externally — reloading scene.");
                        HandleRoomDeletedExternally();
                    }

                    return;
                }

                var room = ParseRoomDoc(snapshot);

                // Host preserves locally-added bots
                if (_isHost && _currentRoom?.players != null)
                    foreach (var p in _currentRoom.players)
                        if (p.isBot && (room.players == null || !room.players.Exists(r => r.id == p.id)))
                        {
                            if (room.players == null) room.players = new List<SlotData>();
                            room.players.Add(p);
                        }

                _currentRoom = room;
                EventManager.FireRoomUpdated(room);

                if (_isHost && room.players != null && room.players.Count >= MaxPlayers
                    && room.status == "waiting")
                {
                    // Validate all human players have fresh heartbeats before starting.
                    // This prevents starting the game with a player who just dropped out
                    // but whose dropout hasn't been detected by CheckPeers yet.
                    if (!AllPlayersAlive(snapshot))
                    {
                        Debug.LogWarning(
                            "[MatchmakingManager] Room full but a player heartbeat is stale — not starting yet.");
                        return;
                    }

                    SetRoomStatus(roomId, "starting");
                    StopBotFill();
                    EventManager.FireMatchFound(room);
                }
                else if (!_isHost && room.status == "starting")
                {
                    EventManager.FireMatchFound(room);
                }
            });
    }

    // ── Matchmaking Phase Watchdog ─────────────────────────────────────────────

    private void StartMatchmakingWatchdog()
    {
        _watchdogActive = true;
        StopWatchdogCoroutines();
        _heartbeatCoroutine = StartCoroutine(HeartbeatRoutine());
        _watchCoroutine = StartCoroutine(PeerWatchRoutine());
    }

    // Every client refreshes its own heartbeat every 15s
    private IEnumerator HeartbeatRoutine()
    {
        while (_watchdogActive)
        {
            yield return new WaitForSeconds(HeartbeatInterval);
            if (!_watchdogActive) yield break;
            WriteMyHeartbeat();
        }
    }

    private void WriteMyHeartbeat()
    {
        if (string.IsNullOrEmpty(_currentRoomId) || FirebaseManager.DB == null) return;
        string myId = PlayerDataManager.PlayFabId;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var update = new Dictionary<string, object>
        {
            { $"playerLastSeen.{myId}", now }
        };
        if (_isHost) update["hostLastSeen"] = now;

        FirebaseManager.DB.Collection(RoomsCollection).Document(_currentRoomId)
            .UpdateAsync(update);
    }

    private void ZeroMyHeartbeat()
    {
        if (string.IsNullOrEmpty(_currentRoomId) || FirebaseManager.DB == null) return;
        string myId = PlayerDataManager.PlayFabId;

        var update = new Dictionary<string, object>
        {
            { $"playerLastSeen.{myId}", 0L }
        };
        if (_isHost) update["hostLastSeen"] = 0L;

        FirebaseManager.DB.Collection(RoomsCollection).Document(_currentRoomId)
            .UpdateAsync(update);
    }

    // Polls peers: host checks all non-host players, non-host checks host
    private IEnumerator PeerWatchRoutine()
    {
        yield return new WaitForSeconds(CheckInterval);

        while (_watchdogActive)
        {
            CheckPeers();
            yield return new WaitForSeconds(CheckInterval);
        }
    }

    /// <summary>
    /// Returns true if every human (non-bot) player in the room has a fresh heartbeat.
    /// Called by the host before firing FireMatchFound to prevent starting a game
    /// with a player who just dropped out but hasn't been detected by CheckPeers yet.
    /// </summary>
    private bool AllPlayersAlive(DocumentSnapshot snapshot)
    {
        if (!snapshot.Exists) return false;
        var dict = snapshot.ToDictionary();

        if (!dict.ContainsKey("playerLastSeen") ||
            !(dict["playerLastSeen"] is Dictionary<string, object> playerTs))
            return true; // no heartbeat map yet — allow (early join before first write)

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (_currentRoom?.players == null) return true;
        foreach (var p in _currentRoom.players)
        {
            if (p.isBot) continue;
            if (!playerTs.ContainsKey(p.id)) continue; // not yet written — allow

            long ts = Convert.ToInt64(playerTs[p.id]);
            float elapsed = (now - ts) / 1000f;

            if (ts == 0L || elapsed > DisconnectTimeout)
            {
                Debug.LogWarning($"[MatchmakingManager] AllPlayersAlive: {p.id} is stale ({elapsed:F0}s).");
                return false;
            }
        }

        return true;
    }

    private void CheckPeers()
    {
        if (!_watchdogActive || string.IsNullOrEmpty(_currentRoomId)) return;

        FirebaseManager.DB.Collection(RoomsCollection).Document(_currentRoomId)
            .GetSnapshotAsync()
            .ContinueWithOnMainThread(task =>
            {
                if (!_watchdogActive || task.IsFaulted) return;
                var snap = task.Result;
                if (!snap.Exists)
                {
                    HandleRoomDeletedExternally();
                    return;
                }

                var dict = snap.ToDictionary();
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // Every client checks every other real player's heartbeat.
                // Non-hosts now also detect when another non-host drops —
                // not just when the host drops. All clients act independently.
                if (!dict.ContainsKey("playerLastSeen") ||
                    !(dict["playerLastSeen"] is Dictionary<string, object> playerTs)) return;

                if (_currentRoom?.players == null) return;

                foreach (var p in _currentRoom.players)
                {
                    // Skip bots and self
                    if (p.isBot || p.id == PlayerDataManager.PlayFabId) continue;

                    if (!playerTs.ContainsKey(p.id)) continue;

                    long ts = Convert.ToInt64(playerTs[p.id]);
                    float elapsed = (now - ts) / 1000f;

                    if (ts == 0L || elapsed > DisconnectTimeout)
                    {
                        Debug.LogWarning($"[MatchmakingManager] Player {p.id} disconnected (elapsed={elapsed:F0}s).");
                        HandlePeerDisconnect(p.id);
                        return; // handle one dropout at a time
                    }
                }
            });
    }

    // ── Disconnect Handlers ───────────────────────────────────────────────────

    /// <summary>
    /// Called when THIS client disconnects (app paused/backgrounded/locked).
    /// </summary>
    private void HandleSelfDisconnect()
    {
        if (_disconnectHandled) return;
        _disconnectHandled = true;
        _watchdogActive = false;
        StopWatchdogCoroutines();

        if (!_coinsDeducted)
        {
            // Before coins — delete room, reload scene
            DeleteRoomAndReload();
        }
        else
        {
            // After coins — we lose. Room cleanup is handled by remaining players.
            // Just reload our own scene (we go back to home, no WinView for us).
            _roomListener?.Stop();
            _roomListener = null;
            _currentRoomId = null;
            _currentRoom = null;
            InviteManager.ClearCurrentRoomId();
            ReloadScene();
        }
    }

    /// <summary>
    /// Called when a PEER disconnects (detected via heartbeat timeout).
    /// Every client calls this independently — _disconnectHandled prevents double-fire.
    /// </summary>
    private void HandlePeerDisconnect(string disconnectedId)
    {
        if (_disconnectHandled) return;
        _disconnectHandled = true;
        _watchdogActive = false;
        StopWatchdogCoroutines();

        if (!_coinsDeducted)
        {
            // ── Before coins: delete room, everyone reloads ───────────────────
            Debug.Log($"[MatchmakingManager] Peer {disconnectedId} left before coins — deleting room.");

            // Every client tries to delete. Firestore ignores duplicate deletes.
            // This is safe because all clients have detected the dropout via their
            // own heartbeat poll and will all call DeleteRoomAndReload independently.
            // The first delete wins; subsequent ones are no-ops on an already-deleted doc.
            DeleteRoomAndReload();
        }
        else
        {
            // ── After coins: disconnected player loses, we win ────────────────
            Debug.Log($"[MatchmakingManager] Peer {disconnectedId} left after coins — declaring result.");

            // Every client independently tries to write the result.
            // WriteDisconnectResultGuarded reads Firestore first and only writes
            // if not already finished — prevents double-write from concurrent clients.
            WriteDisconnectResultGuarded(disconnectedId);
        }
    }

    /// <summary>
    /// Room was deleted by another client. Reload our scene.
    /// </summary>
    private void HandleRoomDeletedExternally()
    {
        if (_disconnectHandled) return;
        _disconnectHandled = true;
        _watchdogActive = false;
        StopWatchdogCoroutines();
        _roomListener?.Stop();
        _roomListener = null;
        _currentRoomId = null;
        _currentRoom = null;
        InviteManager.ClearCurrentRoomId();

        if (_coinsDeducted)
        {
            // Room was deleted after coins paid — treat as win for us
            // (the leaver caused the deletion, they lose)
            ShowWinViewForDisconnect();
        }
        else
        {
            ReloadScene();
        }
    }

    // ── Write Disconnect Result to Firestore ──────────────────────────────────

    private void WriteDisconnectResult(string loserId)
    {
        if (string.IsNullOrEmpty(_currentRoomId) || _currentRoom == null) return;

        var winners = new List<object>();
        if (_currentRoom.players != null)
            foreach (var p in _currentRoom.players)
                if (p.id != loserId)
                    winners.Add((object)p.id);

        var update = new Dictionary<string, object>
        {
            { "gameState.phase", GameState.PhaseFinished },
            { "gameState.bhabhi", loserId },
            { "gameState.winners", winners }
        };

        string roomId = _currentRoomId;
        FirebaseManager.DB.Collection(RoomsCollection).Document(roomId)
            .UpdateAsync(update)
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted)
                    Debug.LogWarning("[MatchmakingManager] WriteDisconnectResult failed: " + task.Exception?.Message);

                // Show win for everyone still here
                ShowWinViewForDisconnect();
            });
    }

    private void WriteDisconnectResultGuarded(string loserId)
    {
        if (string.IsNullOrEmpty(_currentRoomId)) return;

        FirebaseManager.DB.Collection(RoomsCollection).Document(_currentRoomId)
            .GetSnapshotAsync()
            .ContinueWithOnMainThread(readTask =>
            {
                if (readTask.IsFaulted || !readTask.Result.Exists)
                {
                    ShowWinViewForDisconnect();
                    return;
                }

                var dict = readTask.Result.ToDictionary();
                if (dict.ContainsKey("gameState") &&
                    dict["gameState"] is Dictionary<string, object> gsDict)
                {
                    string phase = gsDict.ContainsKey("phase") ? gsDict["phase"].ToString() : "";
                    if (phase == GameState.PhaseFinished)
                    {
                        ShowWinViewForDisconnect();
                        return;
                    }
                }

                WriteDisconnectResult(loserId);
            });
    }

    // ── Result Delivery ───────────────────────────────────────────────────────

    private void ShowWinViewForDisconnect()
    {
        if (_currentRoom == null)
        {
            ReloadScene();
            return;
        }

        string roomId = _currentRoomId;
        int entryFee = _currentRoom.entryFee;
        int prize = Mathf.RoundToInt(entryFee * 1.25f);

        // Clean up local state before showing WinView
        StopBotFill();
        _roomListener?.Stop();
        _roomListener = null;
        _currentRoomId = null;
        _currentRoom = null;
        _watchdogActive = false;
        InviteManager.ClearCurrentRoomId();

        // Delete room after a short delay (WinView handles scene reload on claim)
        StartCoroutine(DeleteRoomAfterDelay(roomId, 3f));

        // Build a minimal winner list containing just the local player so WinView works
        var winners = new List<string> { PlayerDataManager.PlayFabId };
        EventManager.FireGameFinished(winners, "disconnected");
    }

    private void DeleteRoomAndReload()
    {
        StopBotFill();
        _roomListener?.Stop();
        _roomListener = null;

        string roomId = _currentRoomId;
        _currentRoomId = null;
        _currentRoom = null;
        _watchdogActive = false;
        InviteManager.ClearCurrentRoomId();

        if (!string.IsNullOrEmpty(roomId) && FirebaseManager.DB != null)
        {
            FirebaseManager.DB.Collection(RoomsCollection).Document(roomId)
                .DeleteAsync()
                .ContinueWithOnMainThread(_ => ReloadScene());
        }
        else
        {
            ReloadScene();
        }
    }

    private IEnumerator DeleteRoomAfterDelay(string roomId, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (!string.IsNullOrEmpty(roomId) && FirebaseManager.DB != null)
            FirebaseManager.DB.Collection(RoomsCollection).Document(roomId).DeleteAsync();
    }

    private void ReloadScene()
    {
        Debug.Log("[MatchmakingManager] Reloading scene.");
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    // ─── Bot Fill ─────────────────────────────────────────────────────────────

    private IEnumerator BotFillRoutine(string roomId)
    {
        yield return new WaitForSeconds(Random.Range(BotFillDelayMin, BotFillDelayMax));

        while (_currentRoom != null &&
               (_currentRoom.players == null || _currentRoom.players.Count < MaxPlayers))
        {
            var bot = CreateBotSlot();
            if (_currentRoom.players == null) _currentRoom.players = new List<SlotData>();
            _currentRoom.players.Add(bot);

            FirebaseManager.DB.Collection(RoomsCollection).Document(roomId)
                .UpdateAsync(new Dictionary<string, object>
                {
                    { "players", FieldValue.ArrayUnion(SlotToDict(bot)) }
                });

            yield return new WaitForSeconds(Random.Range(BotFillDelayMin, BotFillDelayMax));
        }
    }

    // ─── Create Room For Invite ───────────────────────────────────────────────

    public static async Task<bool> CreateRoomAsync()
    {
        var db = FirebaseManager.DB;
        if (db == null)
        {
            Debug.LogWarning("[MatchmakingManager] Firebase not ready.");
            return false;
        }

        try
        {
            string roomId = Guid.NewGuid().ToString("N");
            int entryFee = GameModeManager.SelectedMode != null ? GameModeManager.SelectedMode.EntryFee : 0;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var roomData = new Dictionary<string, object>
            {
                { "hostId", PlayerDataManager.PlayFabId },
                { "entryFee", entryFee },
                { "status", "waiting" },
                {
                    "players", new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            { "id", PlayerDataManager.PlayFabId }, { "displayName", PlayerDataManager.DisplayName },
                            { "avatarIndex", PlayerDataManager.AvatarIndex }, { "isBot", false }
                        }
                    }
                },
                { "hostLastSeen", now },
                { "playerLastSeen", new Dictionary<string, object> { { PlayerDataManager.PlayFabId, now } } }
            };

            DocumentReference docRef = db.Collection(RoomsCollection).Document(roomId);
            await docRef.SetAsync(roomData);

            DocumentSnapshot snapshot = null;
            for (int i = 0; i < 3; i++)
            {
                snapshot = await docRef.GetSnapshotAsync();
                if (snapshot.Exists) break;
                await Task.Delay(500);
            }

            if (snapshot == null || !snapshot.Exists)
            {
                Debug.LogWarning("[MatchmakingManager] Room write could not be verified.");
                return false;
            }

            InviteManager.SetCurrentRoomId(roomId);
            Debug.Log("[MatchmakingManager] Room pre-created: " + roomId);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[MatchmakingManager] CreateRoomAsync failed: " + e.Message);
            return false;
        }
    }

    // ─── Leave / Cancel ───────────────────────────────────────────────────────

    private void HandleMatchmakingCancel() => CleanupRoom();
    private void HandleLeaveRoom() => CleanupRoom();

    private void CleanupRoom()
    {
        _watchdogActive = false;
        _disconnectHandled = false;
        _coinsDeducted = false;
        StopBotFill();
        StopWatchdogCoroutines();
        _roomListener?.Stop();
        _roomListener = null;

        if (_currentRoom != null && _isHost && FirebaseManager.DB != null)
            FirebaseManager.DB.Collection(RoomsCollection).Document(_currentRoom.roomId).DeleteAsync();

        InviteManager.ClearCurrentRoomId();
        _currentRoomId = null;
        _currentRoom = null;
        _isHost = false;
        EventManager.FireRoomLeft();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void SetRoomStatus(string roomId, string status)
    {
        FirebaseManager.DB.Collection(RoomsCollection).Document(roomId)
            .UpdateAsync(new Dictionary<string, object> { { "status", status } });
    }

    public RoomData GetCurrentRoom() => _currentRoom;

    private void StopWatchdogCoroutines()
    {
        if (_heartbeatCoroutine != null)
        {
            StopCoroutine(_heartbeatCoroutine);
            _heartbeatCoroutine = null;
        }

        if (_watchCoroutine != null)
        {
            StopCoroutine(_watchCoroutine);
            _watchCoroutine = null;
        }
    }

    private void StopBotFill()
    {
        if (_botFillCoroutine != null)
        {
            StopCoroutine(_botFillCoroutine);
            _botFillCoroutine = null;
        }
    }

    private SlotData BuildLocalSlot() => new SlotData
    {
        id = PlayerDataManager.PlayFabId,
        displayName = PlayerDataManager.DisplayName,
        avatarIndex = PlayerDataManager.AvatarIndex,
        isBot = false
    };

    private Dictionary<string, object> SlotToDict(SlotData s) => new Dictionary<string, object>
    {
        { "id", s.id }, { "displayName", s.displayName },
        { "avatarIndex", s.avatarIndex }, { "isBot", s.isBot }
    };

    private RoomData ParseRoomDoc(DocumentSnapshot doc)
    {
        var room = new RoomData { roomId = doc.Id, players = new List<SlotData>() };
        if (doc.TryGetValue("hostId", out string hostId)) room.hostId = hostId;
        if (doc.TryGetValue("entryFee", out int fee)) room.entryFee = fee;
        if (doc.TryGetValue("status", out string status)) room.status = status;

        if (doc.TryGetValue("players", out List<object> players))
            foreach (var p in players)
                if (p is Dictionary<string, object> pd)
                    room.players.Add(new SlotData
                    {
                        id = pd.TryGetValue("id", out var id) ? id.ToString() : "",
                        displayName = pd.TryGetValue("displayName", out var dn) ? dn.ToString() : "Player",
                        avatarIndex = pd.TryGetValue("avatarIndex", out var ai) ? Convert.ToInt32(ai) : 0,
                        isBot = pd.TryGetValue("isBot", out var ib) && Convert.ToBoolean(ib)
                    });
        return room;
    }

    // ── Bot name pool ─────────────────────────────────────────────────────────

    private SlotData CreateBotSlot()
    {
        _botCounter++;
        return new SlotData
        {
            id = "BOT_" + _botCounter,
            displayName = PickUniqueBotName(),
            avatarIndex = UnityEngine.Random.Range(0, 16),
            isBot = true
        };
    }

    private string PickUniqueBotName()
    {
        if (_botNames == null || _botNames.Length == 0) return "Bot" + _botCounter;

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_currentRoom?.players != null)
            foreach (var p in _currentRoom.players)
                usedNames.Add(p.displayName);

        int attempts = 0;
        while (attempts < _botNames.Length)
        {
            string candidate = _botNames[_botNameIndex % _botNames.Length];
            _botNameIndex++;
            if (!usedNames.Contains(candidate)) return candidate;
            attempts++;
        }

        return _botNames[_botCounter % _botNames.Length] + "_" + _botCounter;
    }

    private void LoadBotNames()
    {
        TextAsset asset = Resources.Load<TextAsset>("botNames");
        if (asset != null)
        {
            try
            {
                var wrapper = JsonUtility.FromJson<BotNamesWrapper>(asset.text);
                _botNames = (wrapper != null && wrapper.names != null && wrapper.names.Length > 0)
                    ? wrapper.names
                    : DefaultBotNames();
            }
            catch
            {
                _botNames = DefaultBotNames();
            }
        }
        else
        {
            _botNames = DefaultBotNames();
        }

        ShuffleBotNames();
    }

    private void ShuffleBotNames()
    {
        if (_botNames == null) return;
        for (int i = _botNames.Length - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (_botNames[i], _botNames[j]) = (_botNames[j], _botNames[i]);
        }

        _botNameIndex = 0;
    }

    private string[] DefaultBotNames()
    {
        var maleFirstNames = new[]
        {
            "Ali", "Ahmed", "Usman", "Hamza", "Hassan", "Hussain", "Bilal", "Danish", "Saad", "Fahad",
            "Omar", "Talha", "Adeel", "Imran", "Kamran", "Noman", "Waqas", "Rizwan", "Asad", "Shahzaib",
            "Junaid", "Tariq", "Farhan", "Arslan", "Zain", "Adnan", "Sami", "Yasir", "Naveed", "Mudassir",
            "Salman", "Taimoor", "Haroon", "Sajid", "Majid", "Ahsan", "Zubair", "Shahid", "Azhar", "Akram",
            "Waqar", "Sohail", "Nadeem", "Javed", "Sarfraz", "Iqbal", "Naseer", "Shahbaz", "Arif", "Kashif",
            "Zeeshan", "Shoaib", "Aamir", "Rauf", "Imtiaz", "Faisal", "Waleed", "Umair", "Dawood", "Anas",
            "Rehan", "Saifullah", "Muneeb", "Hadi", "Usama", "Hamid", "Raheel", "Taha", "Sameer", "Irfan",
            "Qasim", "Sultan", "Sarmad", "Basit", "Furqan", "Asim", "Nabil", "Aqeel", "Jawad", "Talal",
            "Rafiq", "Munir", "Zafar", "Mustafa", "Rasheed", "Haroon", "Shakir", "Latif", "Nadir", "Shayan"
        };

        var femaleFirstNames = new[]
        {
            "Ayesha", "Fatima", "Zainab", "Maryam", "Hira", "Iqra", "Amna", "Laiba", "Noor", "Mahnoor",
            "Eman", "Hafsa", "Maham", "Areeba", "Mehwish", "Sana", "Sidra", "Saba", "Khadija", "Rabia",
            "Nimra", "Madiha", "Sadia", "Shazia", "Anum", "Kanza", "Aiman", "Rida", "Aleena", "Komal",
            "Sehrish", "Sahar", "Minal", "Anaya", "Hoor", "Zoya", "Inaya", "Rimsha", "Sania", "Bushra"
        };

        var surnames = new[]
        {
            "Khan", "Ahmed", "Malik", "Butt", "Sheikh", "Shah", "Raza", "Chaudhry", "Farooq", "Abbasi"
        };

        var names = new List<string>();

        // Generate 900 male names
        foreach (var first in maleFirstNames)
        {
            foreach (var last in surnames)
            {
                names.Add(first + last);
                if (names.Count == 900)
                    break;
            }

            if (names.Count == 900)
                break;
        }

        // Generate 100 female names
        foreach (var first in femaleFirstNames)
        {
            foreach (var last in surnames)
            {
                names.Add(first + last);
                if (names.Count == 1000)
                    break;
            }

            if (names.Count == 1000)
                break;
        }

        return names.ToArray();
    }

    [Serializable]
    private class BotNamesWrapper
    {
        public string[] names;
    }
}