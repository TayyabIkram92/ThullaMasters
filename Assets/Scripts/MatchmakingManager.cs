using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Firebase.Extensions;
using Firebase.Firestore;

/// <summary>
/// Handles all Firestore matchmaking logic:
///  - Creates/joins a room document
///  - Listens for other players via onSnapshot (1 listener = minimal reads)
///  - Fills empty slots with bots after 20s each
///  - Deletes the room doc once all 4 are bots (pure local) or when game starts
///
/// Bot names loaded from Assets/Resources/botNames.json at startup.
/// Bots look identical to real players — no badge shown intentionally.
///
/// Attach to the same persistent root GameObject as FirebaseManager.
/// </summary>
[Serializable]
public class BotNamesData
{
    public string[] names;
}

public class MatchmakingManager : MonoBehaviour
{
    public static MatchmakingManager Instance { get; private set; }

    // ── Constants ─────────────────────────────────────────────────────────────

    private const string RoomsCollection = "rooms";
    private const int BotFillDelaySecs = 10;
    private const int MaxPlayers = 4;

    // Bot names loaded from Assets/Resources/botNames.json
    // Bot avatars are random indices picked at runtime
    private string[] _botNames;
    private System.Random _rng = new System.Random();

    // ── State ─────────────────────────────────────────────────────────────────

    private RoomData _room;
    private ListenerRegistration _listener;
    private bool _isHost = false;
    private bool _isSearching = false;
    private Coroutine _botCoroutine;
    private GameModeData _selectedMode;

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        LoadBotNames();
    }

    /// <summary>Load bot names from Assets/Resources/botNames.json.</summary>
    private void LoadBotNames()
    {
        TextAsset jsonFile = Resources.Load<TextAsset>("botNames");
        if (jsonFile != null)
        {
            try
            {
                BotNamesData data = JsonUtility.FromJson<BotNamesData>(jsonFile.text);
                if (data != null && data.names != null && data.names.Length > 0)
                {
                    _botNames = data.names;
                    Debug.Log($"[MatchmakingManager] Loaded {_botNames.Length} bot names.");
                    return;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[MatchmakingManager] Failed to parse botNames.json: {e.Message}");
            }
        }

        // Fallback if file missing or parse fails
        Debug.LogWarning("[MatchmakingManager] botNames.json not found. Using fallback names.");
        _botNames = new[] { "Aryan", "Sara", "Zaid", "Maya", "Rohan", "Aisha" };
    }

    /// <summary>Pick a random bot name and a random avatar index.</summary>
    private SlotData CreateBotSlot(int botNumber)
    {
        string name = _botNames[_rng.Next(_botNames.Length)];
        int avatarIndex = _rng.Next(16); // random avatar from 0-15

        return new SlotData($"BOT_{botNumber}", name, avatarIndex, true);
    }

    private void OnEnable()
    {
        EventManager.OnMatchmakingStartRequested += HandleStartRequested;
        EventManager.OnMatchmakingCancelled += HandleCancelled;
    }

    private void OnDisable()
    {
        EventManager.OnMatchmakingStartRequested -= HandleStartRequested;
        EventManager.OnMatchmakingCancelled -= HandleCancelled;
    }

    private void OnDestroy()
    {
        StopListener();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public RoomData CurrentRoom => _room;

    // ── Start Matchmaking ─────────────────────────────────────────────────────

    private void HandleStartRequested(GameModeData mode)
    {
        if (_isSearching) return;

        _selectedMode = mode;
        _isSearching = true;

        if (!FirebaseManager.IsReady)
        {
            // Wait for Firebase then retry
            EventManager.OnFirebaseReady += RetryStart;
            return;
        }

        StartMatchmaking();
    }

    private void RetryStart()
    {
        EventManager.OnFirebaseReady -= RetryStart;
        StartMatchmaking();
    }

    private void StartMatchmaking()
    {
        // Build local player slot
        var localSlot = new SlotData(
            PlayerDataManager.PlayFabId,
            PlayerDataManager.DisplayName,
            PlayerDataManager.AvatarIndex,
            false
        );

        // Try to find an open room first, then create one if none found
        TryJoinOrCreateRoom(localSlot);
    }

    // ── Join or Create ────────────────────────────────────────────────────────

    /// <summary>
    /// Query for a "waiting" room with fewer than 4 players and matching entry fee.
    /// If found → join it. If not → create a new one.
    /// This is 1 Firestore query (1 read per document returned, capped by limit(1)).
    /// </summary>
    private void TryJoinOrCreateRoom(SlotData localSlot)
    {
        var query = FirebaseManager.DB
            .Collection(RoomsCollection)
            .WhereEqualTo("status", "waiting")
            .WhereEqualTo("entryFee", _selectedMode.EntryFee)
            .Limit(1);

        // ContinueWithOnMainThread — Firebase's own extension method.
        // Runs the callback on Unity's main thread automatically.
        // Never use ContinueWith for Firebase in Unity — it runs on a background thread and crashes.
        query.GetSnapshotAsync().ContinueWithOnMainThread(task =>
        {
            if (task.IsFaulted || task.IsCanceled)
            {
                Debug.LogError($"[MatchmakingManager] Query error: {task.Exception}");
                CreateRoom(localSlot);
                return;
            }

            var snapshot = task.Result;

            // snapshot.Documents is IEnumerable<DocumentSnapshot> — NOT indexable with [0].
            // Use .FirstOrDefault() (via System.Linq) or foreach to get the first doc.
            var firstDoc = snapshot.Documents.FirstOrDefault();

            if (firstDoc != null)
            {
                // Found an open room — join it
                JoinRoom(firstDoc.Id, localSlot);
            }
            else
            {
                // No open room — become host
                CreateRoom(localSlot);
            }
        });
    }

    // ── Create Room (Host) ────────────────────────────────────────────────────

    private void CreateRoom(SlotData localSlot)
    {
        _isHost = true;

        string roomId = $"{PlayerDataManager.PlayFabId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        _room = new RoomData(roomId, PlayerDataManager.PlayFabId, _selectedMode.EntryFee);
        _room.players.Add(localSlot);

        FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(roomId)
            .SetAsync(_room.ToDictionary())
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted)
                {
                    Debug.LogError($"[MatchmakingManager] CreateRoom failed: {task.Exception}");
                    EventManager.FireMatchmakingError("Failed to create room. Please try again.");
                    _isSearching = false;
                    return;
                }

                Debug.Log($"[MatchmakingManager] Room created: {roomId}");
                StartListening(roomId);
                StartBotFillCoroutine();
            });
    }

    // ── Join Room (Non-host) ──────────────────────────────────────────────────

    private void JoinRoom(string roomId, SlotData localSlot)
    {
        _isHost = false;

        // Read the room first (1 read) to get current data
        FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(roomId)
            .GetSnapshotAsync()
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted || !task.Result.Exists)
                {
                    CreateRoom(localSlot);
                    return;
                }

                var dict = task.Result.ToDictionary();
                _room = RoomData.FromDictionary(roomId, dict);

                if (_room.players.Count >= MaxPlayers || _room.status != "waiting")
                {
                    CreateRoom(localSlot);
                    return;
                }

                FirebaseManager.DB
                    .Collection(RoomsCollection)
                    .Document(roomId)
                    .UpdateAsync("players", FieldValue.ArrayUnion(localSlot.ToDictionary()))
                    .ContinueWithOnMainThread(updateTask =>
                    {
                        if (updateTask.IsFaulted)
                        {
                            CreateRoom(localSlot);
                            return;
                        }

                        Debug.Log($"[MatchmakingManager] Joined room: {roomId}");
                        StartListening(roomId);
                    });
            });
    }

    // ── Firestore Listener ────────────────────────────────────────────────────

    /// <summary>
    /// One onSnapshot listener on the room document.
    /// Every update to the doc counts as 1 read regardless of how many fields changed.
    /// </summary>
    private void StartListening(string roomId)
    {
        StopListener();

        _listener = FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(roomId)
            .Listen(snapshot =>
            {
                // The Firebase Unity SDK's Listen() callback fires on the main thread automatically.
                // No dispatcher needed.
                if (!snapshot.Exists) return;

                var updatedRoom = RoomData.FromDictionary(snapshot.Id, snapshot.ToDictionary());

                if (_isHost)
                    MergeHostBots(updatedRoom);
                else
                    _room = updatedRoom;

                EventManager.FireRoomUpdated(_room);

                if (_room.players.Count >= MaxPlayers)
                    OnRoomFull();
            });
    }

    /// <summary>
    /// When the host has already added bots locally and Firestore echoes back the doc
    /// without those bots (because we haven't written them yet), preserve them.
    /// </summary>
    private void MergeHostBots(RoomData firestoreRoom)
    {
        // Start from the Firestore version (real players)
        var merged = firestoreRoom;

        // Add back any bot slots the host was holding locally
        foreach (var localSlot in _room.players)
        {
            if (!localSlot.isBot) continue;
            bool alreadyIn = false;
            foreach (var s in merged.players)
                if (s.id == localSlot.id)
                {
                    alreadyIn = true;
                    break;
                }

            if (!alreadyIn)
                merged.players.Add(localSlot);
        }

        _room = merged;
    }

    private void StopListener()
    {
        _listener?.Stop();
        _listener = null;
    }

    // ── Room Full ─────────────────────────────────────────────────────────────

    private void OnRoomFull()
    {
        StopBotCoroutine();
        StopListener();
        _isSearching = false;

        // Mark room as starting so no new players can join it
        // Then fire match found regardless of write result
        SetRoomStatus("starting", () => { EventManager.FireMatchFound(_room); });
    }

    // ── Bot Fill Coroutine ────────────────────────────────────────────────────

    private void StartBotFillCoroutine()
    {
        StopBotCoroutine();
        _botCoroutine = StartCoroutine(BotFillRoutine());
    }

    private void StopBotCoroutine()
    {
        if (_botCoroutine != null)
        {
            StopCoroutine(_botCoroutine);
            _botCoroutine = null;
        }
    }

    /// <summary>
    /// Every 20 seconds, if a slot is still empty, add a bot.
    /// Always writes to Firestore first, waits for confirmation, then calls OnRoomFull.
    /// </summary>
    private IEnumerator BotFillRoutine()
    {
        int botIndex = 0;

        while (_room != null && _room.players.Count < MaxPlayers)
        {
            yield return new WaitForSeconds(BotFillDelaySecs);

            if (_room == null || _room.players.Count >= MaxPlayers) yield break;

            // Add a bot
            var botSlot = CreateBotSlot(botIndex + 1);
            _room.players.Add(botSlot);
            botIndex++;

            Debug.Log($"[MatchmakingManager] Bot added: {botSlot.displayName}. Slots: {_room.players.Count}/4");

            bool roomFull = _room.players.Count >= MaxPlayers;

            // Check if there are any real opponents
            bool hasRealOpponent = false;
            foreach (var slot in _room.players)
                if (!slot.isBot && slot.id != PlayerDataManager.PlayFabId)
                {
                    hasRealOpponent = true;
                    break;
                }

            // Always write to Firestore — even for the last bot, even all-bots scenario.
            // This ensures the document reflects the final state before we proceed.
            bool writeDone = false;
            bool writeFailed = false;

            var playerList = new List<object>();
            foreach (var p in _room.players)
                playerList.Add(p.ToDictionary());

            FirebaseManager.DB
                .Collection(RoomsCollection)
                .Document(_room.roomId)
                .UpdateAsync("players", playerList)
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsFaulted)
                    {
                        Debug.LogError($"[MatchmakingManager] WritePlayersToFirestore failed: {task.Exception}");
                        writeFailed = true;
                    }

                    writeDone = true;
                });

            // Wait for Firestore write to complete before proceeding
            yield return new WaitUntil(() => writeDone);

            // Update UI with the latest room state
            EventManager.FireRoomUpdated(_room);

            if (roomFull)
            {
                OnRoomFull();
                yield break;
            }
        }
    }

    // ── Firestore Helpers ─────────────────────────────────────────────────────

    private void WritePlayersToFirestore()
    {
        if (_room == null || string.IsNullOrEmpty(_room.roomId)) return;

        var playerList = new List<object>();
        foreach (var p in _room.players)
            playerList.Add(p.ToDictionary());

        FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(_room.roomId)
            .UpdateAsync("players", playerList)
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted)
                    Debug.LogError($"[MatchmakingManager] WritePlayersToFirestore failed: {task.Exception}");
            });
    }

    private void SetRoomStatus(string status, Action onComplete = null)
    {
        if (_room == null)
        {
            onComplete?.Invoke();
            return;
        }

        FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(_room.roomId)
            .UpdateAsync("status", status)
            .ContinueWithOnMainThread(task => onComplete?.Invoke());
    }

    private void DeleteRoomDocument()
    {
        StopListener();

        if (_room == null || string.IsNullOrEmpty(_room.roomId)) return;

        FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(_room.roomId)
            .DeleteAsync()
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted)
                    Debug.LogError($"[MatchmakingManager] DeleteRoom failed: {task.Exception}");
                else
                    Debug.Log($"[MatchmakingManager] Room deleted: {_room.roomId}");
            });
    }

    // ── Cancel ────────────────────────────────────────────────────────────────

    private void HandleCancelled()
    {
        if (!_isSearching) return;

        StopBotCoroutine();
        StopListener();

        if (_isHost)
            DeleteRoomDocument();

        _isSearching = false;
        _isHost = false;
        _room = null;

        Debug.Log("[MatchmakingManager] Matchmaking cancelled.");
    }

    // ── Cleanup on app quit ───────────────────────────────────────────────────

    private void OnApplicationQuit()
    {
        HandleCancelled();
    }
}