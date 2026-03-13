using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Firebase.Firestore;

/// <summary>
/// Manages the transition from matchmaking into the in-game phase.
///
/// Sync Protocol (replaces fragile 2-second delay):
/// ─────────────────────────────────────────────────
/// 1. HOST deals cards → writes { status:"dealing", hands:{...}, readyPlayers:[] }
///    to Firestore (awaited).
/// 2. HOST immediately marks itself + all bots as ready (ArrayUnion, awaited).
/// 3. Each NON-HOST polls via GetSnapshotAsync until status=="dealing" and their
///    hand is present, then writes their own ID into readyPlayers (ArrayUnion, awaited).
/// 4. HOST polls readyPlayers every second. Once all 4 IDs appear → proceed.
///    If a human hasn't acknowledged within 20s → replace with a bot → proceed.
/// 5. ProceedToGame fires OnGameReady → GameManager starts.
///
/// All Firestore operations are async/await (GetSnapshotAsync / UpdateAsync).
/// No persistent listeners are used here — one-shot reads are sufficient and
/// immune to listener-registration race conditions.
/// </summary>
public class InGameManager : MonoBehaviour
{
    public static InGameManager Instance { get; private set; }

    private const string RoomsCollection      = "rooms";
    private const float  ReadyPollIntervalSec = 1f;
    private const float  ReadyTimeoutSec      = 20f;

    private RoomData _room;
    private bool     _isHost;
    private bool     _proceeded;

    public RoomData CurrentRoom => _room;

    // Navigation-race guard: InGameView may activate after OnGameReady fires
    private List<CardData> _pendingHand;
    private List<SlotData> _pendingSeatedPlayers;
    public  bool           HasPendingGameReady => _pendingHand != null;

    public void ConsumePendingGameReady(out List<CardData> hand, out List<SlotData> players)
    {
        hand    = _pendingHand;
        players = _pendingSeatedPlayers;
        _pendingHand          = null;
        _pendingSeatedPlayers = null;
    }

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void OnEnable()
    {
        EventManager.OnMatchFound         += HandleMatchFound;
        EventManager.OnLeaveGameRequested += HandleLeaveGame;
    }

    private void OnDisable()
    {
        EventManager.OnMatchFound         -= HandleMatchFound;
        EventManager.OnLeaveGameRequested -= HandleLeaveGame;
    }

    // ── Entry Point ───────────────────────────────────────────────────────────

    private void HandleMatchFound(RoomData room)
    {
        _room      = room;
        _isHost    = room.hostId == PlayerDataManager.PlayFabId;
        _proceeded = false;
        _pendingHand          = null;
        _pendingSeatedPlayers = null;

        Debug.Log($"[InGameManager] HandleMatchFound. isHost={_isHost} room={room.roomId} " +
                  $"players={room.players.Count}");

        if (_isHost)
            _ = HostDealAndWaitForReadyAsync();
        else
            _ = NonHostWaitForHandAsync();
    }

    // ── HOST Path ─────────────────────────────────────────────────────────────

    private async Task HostDealAndWaitForReadyAsync()
    {
        // Step 1: Deal cards locally
        var deck = CardData.GenerateShuffledDeck();
        _room.hands.Clear();

        for (int i = 0; i < _room.players.Count; i++)
        {
            var player = _room.players[i];
            if (string.IsNullOrWhiteSpace(player.id))
            {
                Debug.LogError($"[InGameManager] Player {i} has empty id — aborting deal.");
                EventManager.FireShowPopUp("Game error: invalid player data. Please try again.");
                return;
            }

            string safeKey = player.id.Replace(".", "_");
            var hand = new List<string>();
            for (int c = i * 13; c < (i * 13) + 13; c++)
                hand.Add(deck[c].ToFirestoreString());

            _room.hands[safeKey]    = hand;
            // _room.players[i].id    = safeKey;
        }

        Debug.Log($"[InGameManager] Dealt cards to {_room.hands.Count} players: " +
                  string.Join(", ", _room.hands.Keys));

        // Step 2: Write dealing state + empty readyPlayers (awaited)
        var dealWrite = new Dictionary<string, object>
        {
            { "status",       "dealing"               },
            { "hands",        _room.HandsToDictionary() },
            { "readyPlayers", new List<object>()       }
        };

        bool writeOk = await FirestoreUpdateAsync(_room.roomId, dealWrite);
        if (!writeOk)
        {
            Debug.LogError("[InGameManager] Failed to write dealing state.");
            EventManager.FireShowPopUp("Network error during deal. Please try again.");
            return;
        }

        Debug.Log("[InGameManager] Host: dealing state written.");

        // Step 3: Mark host + bots as ready immediately (they run on this device)
        var immediateReady = new List<object>();
        foreach (var p in _room.players)
            if (p.id == PlayerDataManager.PlayFabId || p.isBot)
                immediateReady.Add((object)p.id);

        if (immediateReady.Count > 0)
        {
            await FirestoreUpdateAsync(_room.roomId, new Dictionary<string, object>
            {
                { "readyPlayers", FieldValue.ArrayUnion(immediateReady.ToArray()) }
            });
            Debug.Log($"[InGameManager] Host: marked {immediateReady.Count} seat(s) ready.");
        }

        // Step 4: Poll until all 4 confirmed (replacing timed-out humans with bots)
        await WaitForAllPlayersReadyAsync();

        // Step 5: Proceed
        if (!_proceeded)
        {
            _proceeded = true;
            ProceedToGame();
        }
    }

    private async Task WaitForAllPlayersReadyAsync()
    {
        // Collect human IDs (non-host, non-bot) that need to acknowledge
        var humanIds = new List<string>();
        foreach (var p in _room.players)
            if (!p.isBot && p.id != PlayerDataManager.PlayFabId)
                humanIds.Add(p.id);

        if (humanIds.Count == 0)
        {
            Debug.Log("[InGameManager] No humans to wait for (all host/bot).");
            return;
        }

        Debug.Log($"[InGameManager] Waiting for {humanIds.Count} human(s): " +
                  string.Join(", ", humanIds));

        float elapsed  = 0f;
        var confirmed  = new HashSet<string>();
        var timedOut   = new HashSet<string>();

        while (true)
        {
            await Task.Delay(System.Math.Max(1, Mathf.RoundToInt(ReadyPollIntervalSec * 1000)));
            elapsed += ReadyPollIntervalSec;

            var snapshot = await FirestoreGetAsync(_room.roomId);
            if (snapshot == null || !snapshot.Exists) continue;

            var dict = snapshot.ToDictionary();

            // Read confirmed ready players
            if (dict.ContainsKey("readyPlayers") && dict["readyPlayers"] is List<object> rawReady)
                foreach (var r in rawReady) confirmed.Add(r.ToString());

            // Sync local player list from Firestore (may have changed due to previous bot replacements)
            if (dict.ContainsKey("players") && dict["players"] is List<object> rawPlayers)
            {
                _room.players.Clear();
                foreach (var item in rawPlayers)
                    if (item is Dictionary<string, object> pd)
                        _room.players.Add(SlotData.FromDictionary(pd));
            }

            // Check which humans are still pending
            bool allDone = true;
            foreach (var id in humanIds)
            {
                if (confirmed.Contains(id) || timedOut.Contains(id)) continue;
                allDone = false;
            }

            if (allDone)
            {
                Debug.Log("[InGameManager] All players confirmed ready.");
                return;
            }

            Debug.Log($"[InGameManager] Still waiting ({elapsed:F0}s elapsed)...");

            // Replace anyone who exceeded the timeout
            if (elapsed >= ReadyTimeoutSec)
            {
                foreach (var id in humanIds)
                {
                    if (confirmed.Contains(id) || timedOut.Contains(id)) continue;
                    timedOut.Add(id);
                    Debug.LogWarning($"[InGameManager] {id} timed out — replacing with bot.");
                    await ReplacePlayerWithBotAsync(id);
                }

                Debug.Log("[InGameManager] All timed-out players replaced. Proceeding.");
                return;
            }
        }
    }

    private async Task ReplacePlayerWithBotAsync(string humanId)
    {
        string botId = "BOT_" + System.Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper();

        // Replace locally
        for (int i = 0; i < _room.players.Count; i++)
        {
            if (_room.players[i].id != humanId) continue;
            _room.players[i] = new SlotData(botId, "Bot", _room.players[i].avatarIndex, true);
            break;
        }

        // Transfer hand
        if (_room.hands.ContainsKey(humanId))
        {
            _room.hands[botId] = _room.hands[humanId];
            _room.hands.Remove(humanId);
        }

        // Write to Firestore (awaited)
        var playerList = new List<object>();
        foreach (var p in _room.players) playerList.Add(p.ToDictionary());

        await FirestoreUpdateAsync(_room.roomId, new Dictionary<string, object>
        {
            { "players",      playerList               },
            { "hands",        _room.HandsToDictionary() },
            { "readyPlayers", FieldValue.ArrayUnion(botId) }
        });

        Debug.Log($"[InGameManager] Replaced {humanId} with {botId}.");
    }

    // ── NON-HOST Path ─────────────────────────────────────────────────────────

    private async Task NonHostWaitForHandAsync()
    {
        string localId     = PlayerDataManager.PlayFabId;
        string sanitizedId = localId.Replace(".", "_");

        Debug.Log($"[InGameManager] Non-host: polling for hand. id={localId}");

        float elapsed = 0f;

        while (elapsed < ReadyTimeoutSec)
        {
            await Task.Delay(Mathf.RoundToInt(ReadyPollIntervalSec * 1000));
            elapsed += ReadyPollIntervalSec;

            var snapshot = await FirestoreGetAsync(_room.roomId);
            if (snapshot == null || !snapshot.Exists) continue;

            var dict = snapshot.ToDictionary();

            // Wait for host to write dealing status
            if (!dict.ContainsKey("status") || dict["status"].ToString() != "dealing") continue;

            // Check we haven't been replaced by a bot (timed out from a previous attempt)
            bool stillInRoom = false;
            if (dict.ContainsKey("players") && dict["players"] is List<object> rawPlayers)
                foreach (var item in rawPlayers)
                    if (item is Dictionary<string, object> pd && pd.ContainsKey("id") &&
                        (pd["id"].ToString() == localId || pd["id"].ToString() == sanitizedId))
                    { stillInRoom = true; break; }

            if (!stillInRoom)
            {
                Debug.LogWarning("[InGameManager] Non-host replaced by bot — showing error.");
                EventManager.FireShowPopUp("Connection timed out. Returning to menu.");
                await Task.Delay(2000);
                EventManager.FireLeaveGameRequested();
                return;
            }

            // Check hand is available
            bool hasHand = dict.ContainsKey("hands") &&
                           dict["hands"] is Dictionary<string, object> rawHands &&
                           (rawHands.ContainsKey(localId) || rawHands.ContainsKey(sanitizedId));
            if (!hasHand) continue;

            // Update local room model from authoritative Firestore snapshot
            _room = RoomData.FromDictionary(_room.roomId, dict);

            // Normalize sanitized key back to real localId
            if (!_room.hands.ContainsKey(localId) && _room.hands.ContainsKey(sanitizedId))
            {
                _room.hands[localId] = _room.hands[sanitizedId];
                _room.hands.Remove(sanitizedId);
                for (int i = 0; i < _room.players.Count; i++)
                    if (_room.players[i].id == sanitizedId)
                        _room.players[i].id = localId;
            }

            Debug.Log($"[InGameManager] Non-host: hand received ({_room.hands[localId].Count} cards). " +
                      "Writing ready acknowledgment...");

            // Write ready acknowledgment (awaited)
            bool ackOk = await FirestoreUpdateAsync(_room.roomId, new Dictionary<string, object>
            {
                { "readyPlayers", FieldValue.ArrayUnion(localId) }
            });

            if (!ackOk)
                Debug.LogWarning("[InGameManager] Non-host: ack write failed — proceeding anyway.");
            else
                Debug.Log("[InGameManager] Non-host: ready acknowledged.");

            if (!_proceeded)
            {
                _proceeded = true;
                ProceedToGame();
            }
            return;
        }

        // Timed out — host never dealt
        Debug.LogError("[InGameManager] Non-host: timed out waiting for dealing state.");
        EventManager.FireShowPopUp("Host disconnected. Returning to menu.");
        await Task.Delay(2000);
        EventManager.FireLeaveGameRequested();
    }

    // ── Proceed To Game ───────────────────────────────────────────────────────

    private void ProceedToGame()
    {
        string localId = PlayerDataManager.PlayFabId;

        var localHandCodes = _room.hands.ContainsKey(localId)
            ? _room.hands[localId]
            : new List<string>();

        var localHand = new List<CardData>();
        foreach (var code in localHandCodes)
        {
            var card = CardData.FromShortCode(code);
            if (card != null) localHand.Add(card);
        }

        int localIndex = 0;
        for (int i = 0; i < _room.players.Count; i++)
            if (_room.players[i].id == localId) { localIndex = i; break; }

        var seatedPlayers = new List<SlotData>();
        for (int seat = 0; seat < 4; seat++)
            seatedPlayers.Add(_room.players[(localIndex + seat) % _room.players.Count]);

        Debug.Log($"[InGameManager] ProceedToGame. Hand={localHand.Count} cards.");

        _pendingHand          = localHand;
        _pendingSeatedPlayers = seatedPlayers;

        EventManager.FireGameReady(localHand, seatedPlayers);

        StartCoroutine(ClearPendingAfterDelay(5f));
    }

    private IEnumerator ClearPendingAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        _pendingHand          = null;
        _pendingSeatedPlayers = null;
    }

    // ── Leave ─────────────────────────────────────────────────────────────────

    private void HandleLeaveGame()
    {
        _room      = null;
        _isHost    = false;
        _proceeded = false;
        _pendingHand          = null;
        _pendingSeatedPlayers = null;

        EventManager.FireShowView(ViewType.Home);
    }

    // ── Firestore Async Helpers ───────────────────────────────────────────────

    private async Task<bool> FirestoreUpdateAsync(string roomId, Dictionary<string, object> data)
    {
        try
        {
            await FirebaseManager.DB
                .Collection(RoomsCollection)
                .Document(roomId)
                .UpdateAsync(data);
            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[InGameManager] FirestoreUpdateAsync failed: {ex.Message}");
            return false;
        }
    }

    private async Task<DocumentSnapshot> FirestoreGetAsync(string roomId)
    {
        try
        {
            return await FirebaseManager.DB
                .Collection(RoomsCollection)
                .Document(roomId)
                .GetSnapshotAsync();
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[InGameManager] FirestoreGetAsync failed: {ex.Message}");
            return null;
        }
    }
}
