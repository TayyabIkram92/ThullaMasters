using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using Firebase.Extensions;
using Firebase.Firestore;

/// <summary>
/// Manages the InGame state: seat assignment, card dealing, and Firestore sync.
///
/// Flow:
///  1. OnMatchFound fires → MatchmakingView shows "Joining Game..." → UIManager
///     navigates to InGame (fired from MatchmakingView.HandleMatchFound TODO).
///  2. InGame GO becomes active → InGameManager.OnEnable fires.
///  3. If local player is host:
///       - Generate shuffled deck
///       - Split into 4 hands of 13
///       - Write hands + status="dealing" to Firestore in ONE update
///       - Read back own hand from written data
///  4. If local player is NOT host:
///       - Listen to room doc for status="dealing"
///       - When seen, read own hand from hands dict
///  5. Once hand is ready, fire OnGameReady(localHand, seatedPlayers)
///     → InGameView picks it up and populates UI.
///
/// Seat assignment (clockwise from local player):
///   Profile    = local player
///   Profile(1) = player at (localIndex + 1) % 4
///   Profile(2) = player at (localIndex + 2) % 4
///   Profile(3) = player at (localIndex + 3) % 4
///
/// Attach to the same persistent root GO as FirebaseManager and MatchmakingManager.
/// </summary>
public class InGameManager : MonoBehaviour
{
    public static InGameManager Instance { get; private set; }

    private const string RoomsCollection = "rooms";

    // ── State ─────────────────────────────────────────────────────────────────

    private RoomData _room;
    private bool _isHost;
    private bool _proceeded; // guard: ProceedToGame fires only once per game
    private ListenerRegistration _listener;

    /// <summary>Exposed for GameManager to access room data and hands.</summary>
    public RoomData CurrentRoom => _room;

    // ── Unity ─────────────────────────────────────────────────────────────────

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
        EventManager.OnMatchFound += HandleMatchFound;
        EventManager.OnLeaveGameRequested += HandleLeaveGame;
    }

    private void OnDisable()
    {
        EventManager.OnMatchFound -= HandleMatchFound;
        EventManager.OnLeaveGameRequested -= HandleLeaveGame;
        StopListener();
    }

    private void OnDestroy()
    {
        StopListener();
    }

    // ── Match Found ───────────────────────────────────────────────────────────

    private void HandleMatchFound(RoomData room)
    {
        _room = room;
        _isHost = room.hostId == PlayerDataManager.PlayFabId;
        _proceeded = false;

        Debug.Log($"[InGameManager] HandleMatchFound. isHost={_isHost} roomId={room.roomId} " +
                  $"players={room.players.Count}");

        if (_isHost)
            DealCards();
        else
            ListenForDeal();
    }

    // ── Host: Deal Cards ──────────────────────────────────────────────────────

    /// <summary>
    /// Host generates the deck, splits into 4 hands of 13, writes to Firestore
    /// in a single update call, then immediately uses the written data locally.
    /// 1 Firestore write total.
    /// </summary>
    private void DealCards()
    {
        var deck = CardData.GenerateShuffledDeck();

        _room.hands.Clear();

        // Validate all player IDs before building hands
        // Empty or whitespace IDs would cause Firestore to reject the write
        for (int i = 0; i < _room.players.Count; i++)
        {
            var player = _room.players[i];

            if (string.IsNullOrWhiteSpace(player.id))
            {
                Debug.LogError($"[InGameManager] Player at index {i} has empty id. " +
                               $"Name={player.displayName}, isBot={player.isBot}. Aborting deal.");
                EventManager.FireShowPopUp("Game error: invalid player data. Please try again.");
                return;
            }

            var hand = new List<string>();
            for (int c = i * 13; c < (i * 13) + 13; c++)
                hand.Add(deck[c].ToFirestoreString());

            // Sanitize key: replace any characters Firestore rejects in field names
            // Firestore field keys must not contain '.' and must not be empty
            string safeKey = player.id.Replace(".", "_");
            _room.hands[safeKey] = hand;

            // Keep id in sync with the safe key so ProceedToGame can look it up
            _room.players[i].id = safeKey;
        }

        Debug.Log($"[InGameManager] Dealing cards to {_room.hands.Count} players: " +
                  string.Join(", ", _room.hands.Keys));

        var updateDict = new Dictionary<string, object>
        {
            { "status", "dealing" },
            { "hands", _room.HandsToDictionary() }
        };

        FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(_room.roomId)
            .UpdateAsync(updateDict)
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted)
                {
                    Debug.LogError($"[InGameManager] DealCards write failed: {task.Exception}");
                    EventManager.FireShowPopUp("Failed to deal cards. Please try again.");
                    return;
                }

                Debug.Log("[InGameManager] Cards dealt and written to Firestore.");
                // Small delay to ensure non-host clients receive the Firestore
                // update before host fires OnGameReady and GameManager starts.
                // Prevents race where non-host hand is empty on game init.
                StartCoroutine(DelayedProceedToGame(2f));
            });
    }

    // ── Non-Host: Listen for Deal ─────────────────────────────────────────────

    /// <summary>
    /// Non-host listens to the room doc.
    /// When status flips to "dealing", reads own hand from hands dict.
    /// 1 Firestore listener (reads only on doc change).
    /// </summary>
    private void ListenForDeal()
    {
        StopListener();

        _listener = FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(_room.roomId)
            .Listen(snapshot =>
            {
                if (!snapshot.Exists) return;

                var updated = RoomData.FromDictionary(snapshot.Id, snapshot.ToDictionary());

                if (updated.status != "dealing") return;

                // Check for both raw id and sanitized id (dots replaced with underscores)
                string localId = PlayerDataManager.PlayFabId;
                string sanitizedId = localId.Replace(".", "_");
                bool hasHand = updated.hands.ContainsKey(localId) ||
                               updated.hands.ContainsKey(sanitizedId);
                if (!hasHand)
                {
                    Debug.LogWarning($"[InGameManager] Hand not found for {localId} or {sanitizedId}. Waiting...");
                    return;
                }

                // Use sanitized key if that's what the host wrote
                if (!updated.hands.ContainsKey(localId) && updated.hands.ContainsKey(sanitizedId))
                {
                    // Remap so ProceedToGame can find it
                    updated.hands[localId] = updated.hands[sanitizedId];
                    // Also fix the player id in the players list
                    for (int i = 0; i < updated.players.Count; i++)
                        if (updated.players[i].id == sanitizedId)
                            updated.players[i].id = localId;
                }

                if (_proceeded) return; // already proceeding — ignore duplicate snapshot

                // Got our hand — stop listening, proceed
                _room = updated;
                _proceeded = true;
                StopListener();

                Debug.Log("[InGameManager] Received hand from Firestore. Proceeding to game.");
                ProceedToGame();
            });
    }

    // ── Proceed To Game ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds the local hand as CardData list and the clockwise-seated player list,
    /// then fires OnGameReady for InGameView to consume.
    /// </summary>
    private void ProceedToGame()
    {
        // Parse local hand from short codes
        var localHandCodes = _room.hands.ContainsKey(PlayerDataManager.PlayFabId)
            ? _room.hands[PlayerDataManager.PlayFabId]
            : new List<string>();

        var localHand = new List<CardData>();
        foreach (var code in localHandCodes)
        {
            var card = CardData.FromShortCode(code);
            if (card != null) localHand.Add(card);
        }

        // Build clockwise seat order starting from local player
        // Profile    = local player
        // Profile(1) = (localIndex + 1) % 4
        // Profile(2) = (localIndex + 2) % 4
        // Profile(3) = (localIndex + 3) % 4
        int localIndex = 0;
        for (int i = 0; i < _room.players.Count; i++)
        {
            if (_room.players[i].id == PlayerDataManager.PlayFabId)
            {
                localIndex = i;
                break;
            }
        }

        var seatedPlayers = new List<SlotData>();
        for (int seat = 0; seat < 4; seat++)
            seatedPlayers.Add(_room.players[(localIndex + seat) % _room.players.Count]);

        Debug.Log($"[InGameManager] Local hand: {localHand.Count} cards. Proceeding to InGame.");

        EventManager.FireGameReady(localHand, seatedPlayers);
    }

    // ── Leave Game ────────────────────────────────────────────────────────────

    private void HandleLeaveGame()
    {
        StopListener();
        _room = null;
        _isHost = false;
        _proceeded = false;

        EventManager.FireShowView(UI.ViewType.Home);
    }

    // ── Delayed Proceed ──────────────────────────────────────────────────────

    private IEnumerator DelayedProceedToGame(float delay)
    {
        yield return new UnityEngine.WaitForSeconds(delay);
        if (!_proceeded)
        {
            _proceeded = true;
            ProceedToGame();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void StopListener()
    {
        _listener?.Stop();
        _listener = null;
    }
}