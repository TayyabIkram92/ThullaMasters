using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using Firebase.Extensions;
using Firebase.Firestore;

/// <summary>
/// Central game logic manager.
///
/// Responsibilities:
///  - Initialises GameState from dealt hands (host only)
///  - Listens to Firestore room doc for gameState changes
///  - Executes local player moves and writes result to Firestore
///  - Executes bot moves (host only) after a random 2-15s delay
///  - Manages turn timer: writes turnStartTime once, all clients count locally
///  - Resolves rounds: discard or pickup
///  - Handles Special Rules 1, 2, 3, 4
///  - Marks winners / bhabhi and triggers coin payout
///
/// Attach to the same persistent DontDestroyOnLoad GO as InGameManager.
/// </summary>
public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    /// <summary>Exposed for HostWatchdog to check current game phase.</summary>
    public GameState CurrentGs => _gs;

    // ── Constants ─────────────────────────────────────────────────────────────

    private const string RoomsCollection = "rooms";

    // ── State ─────────────────────────────────────────────────────────────────

    private RoomData _room;
    private GameState _gs;
    private bool _isHost;
    private bool _gameActive;
    private bool _myTurnActive; // true only while it's local player's turn
    private Coroutine _turnTimerCoroutine;
    private Coroutine _botCoroutine;
    private ListenerRegistration _listener;

    // Local player's hand (kept in sync with _gs.hands[localId])
    private List<string> _myHand = new List<string>();

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
        EventManager.OnGameReady += HandleGameReady;
        EventManager.OnLocalCardPlayed += HandleLocalCardPlayed;
        EventManager.OnStealHandRequested += HandleStealHand;
        EventManager.OnShootoutCardChosen += HandleShootoutCardChosen;
        EventManager.OnLeaveGameRequested += HandleLeaveGame;
    }

    private void OnDisable()
    {
        EventManager.OnGameReady -= HandleGameReady;
        EventManager.OnLocalCardPlayed -= HandleLocalCardPlayed;
        EventManager.OnStealHandRequested -= HandleStealHand;
        EventManager.OnShootoutCardChosen -= HandleShootoutCardChosen;
        EventManager.OnLeaveGameRequested -= HandleLeaveGame;
        StopListener();
        StopAllCoroutines();
    }

    // ── Initialise ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by InGameManager after hands are dealt and game is ready to start.
    /// </summary>
    private void HandleGameReady(List<CardData> localHand, List<SlotData> seatedPlayers)
    {
        _room = InGameManager.Instance != null ? GetCurrentRoom() : null;
        _isHost = _room != null && _room.hostId == PlayerDataManager.PlayFabId;

        // Cache local hand as string codes
        _myHand.Clear();
        foreach (var c in localHand) _myHand.Add(c.ShortCode);

        if (_isHost)
            InitialiseGameState();
        else
            StartListening();
    }

    private RoomData GetCurrentRoom()
    {
        return MatchmakingManager.Instance?.CurrentRoom;
    }

    // ── Host: Build Initial GameState ─────────────────────────────────────────

    private void InitialiseGameState()
    {
        _room = GetCurrentRoom();
        if (_room == null)
        {
            Debug.LogError("[GameManager] Room is null on init.");
            return;
        }

        _gs = new GameState();
        _gs.phase = GameState.PhasePlaying;
        _gs.roundNumber = 1;

        // activePlayers = all players in room order
        foreach (var p in _room.players)
            _gs.activePlayers.Add(p.id);

        // Copy hands from room
        foreach (var kv in _room.hands)
            _gs.hands[kv.Key] = new List<string>(kv.Value);

        // Find who has Ace of Spades → they go first
        int startIndex = 0;
        for (int i = 0; i < _gs.activePlayers.Count; i++)
        {
            string pid = _gs.activePlayers[i];
            if (_gs.hands.ContainsKey(pid) && _gs.hands[pid].Contains("AS"))
            {
                startIndex = i;
                break;
            }
        }

        _gs.currentPlayerIndex = startIndex;
        _gs.leadSuit = "";
        _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        WriteGameState(() =>
        {
            Debug.Log($"[GameManager] Game initialised. First player index: {startIndex}");
            StartListening();
            _gameActive = true;
            EventManager.FireGameStateUpdated(_gs);
            ProcessCurrentTurn();
        });
    }

    // ── Firestore Listener ────────────────────────────────────────────────────

    private void StartListening()
    {
        StopListener();
        if (_room == null) return;

        _listener = FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(_room.roomId)
            .Listen(snapshot =>
            {
                if (!snapshot.Exists) return;

                var dict = snapshot.ToDictionary();
                if (!dict.ContainsKey("gameState")) return;
                if (!(dict["gameState"] is Dictionary<string, object> gsDict)) return;

                var newGs = GameState.FromDictionary(gsDict);

                // Only process if something meaningful changed
                bool turnChanged = _gs == null ||
                                   newGs.currentPlayerIndex != _gs.currentPlayerIndex ||
                                   newGs.roundNumber != _gs.roundNumber ||
                                   newGs.phase != _gs.phase ||
                                   newGs.cardsInPlay.Count != _gs.cardsInPlay.Count;

                _gs = newGs;

                // Sync local hand
                string localId = PlayerDataManager.PlayFabId;
                if (_gs.hands.ContainsKey(localId))
                    _myHand = new List<string>(_gs.hands[localId]);

                _gameActive = _gs.phase != GameState.PhaseFinished;

                EventManager.FireGameStateUpdated(_gs);

                // Non-host clients process turn from listener. Host processes directly after write.
                if (turnChanged && _gameActive && !_isHost)
                    ProcessCurrentTurn();

                if (_gs.phase == GameState.PhaseFinished)
                    HandleGameFinished();
            });
    }

    private void StopListener()
    {
        _listener?.Stop();
        _listener = null;
    }

    // ── Turn Processing ───────────────────────────────────────────────────────

    /// <summary>
    /// Called whenever the turn changes (from listener or host init).
    /// Decides whether to start local player's turn or schedule bot move.
    /// </summary>
    private void ProcessCurrentTurn()
    {
        StopTurnTimer();
        StopBotCoroutine();

        if (_gs == null || !_gameActive) return;
        if (_gs.phase == GameState.PhaseShootout)
        {
            ProcessShootoutTurn();
            return;
        }

        string currentId = _gs.CurrentPlayerId;
        string localId = PlayerDataManager.PlayFabId;

        // Start local countdown (everyone does this — purely visual)
        float remaining = _gs.SecondsRemaining(GameState.TurnSeconds);
        _turnTimerCoroutine = StartCoroutine(TurnTimerCoroutine(remaining));

        if (currentId == localId)
        {
            // It's our turn
            _myTurnActive = true;
            Debug.Log($"[GameManager] My turn. Round {_gs.roundNumber}. Lead suit: '{_gs.leadSuit}'");
        }
        else if (_isHost && IsBot(currentId))
        {
            // Host executes bot move
            float botDelay = UnityEngine.Random.Range(2f, 15f);
            float effectiveDelay = Mathf.Min(botDelay, remaining - 1f);
            if (effectiveDelay < 1f) effectiveDelay = 1f;
            _botCoroutine = StartCoroutine(BotMoveCoroutine(currentId, effectiveDelay));
        }
    }

    // ── Turn Timer ────────────────────────────────────────────────────────────

    private IEnumerator TurnTimerCoroutine(float seconds)
    {
        yield return new WaitForSeconds(seconds);

        if (!_gameActive) yield break;

        string currentId = _gs?.CurrentPlayerId ?? "";
        string localId = PlayerDataManager.PlayFabId;

        if (currentId == localId && _myTurnActive)
        {
            Debug.Log("[GameManager] Turn timer expired — auto playing.");
            AutoPlay(localId);
        }
        else if (_isHost && IsBot(currentId))
        {
            // Bot didn't play yet (race condition guard)
            StopBotCoroutine();
            AutoPlay(currentId);
        }
    }

    private void StopTurnTimer()
    {
        if (_turnTimerCoroutine != null)
        {
            StopCoroutine(_turnTimerCoroutine);
            _turnTimerCoroutine = null;
        }
    }

    // ── Auto Play ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Selects and plays a card automatically for the given player.
    /// Rules: play a card of the lead suit if possible, otherwise play any card (thulla).
    /// Special: if it's round 1 and player is the leader (Ace of Spades), play AS.
    /// </summary>
    private void AutoPlay(string playerId)
    {
        if (_gs == null || !_gs.hands.ContainsKey(playerId)) return;

        var hand = _gs.hands[playerId];
        if (hand.Count == 0) return;

        string cardToPlay = ChooseAutoCard(playerId, hand);
        ExecuteMove(playerId, cardToPlay);
    }

    private string ChooseAutoCard(string playerId, List<string> hand)
    {
        // Round 1 leader must play Ace of Spades
        if (_gs.roundNumber == 1 && _gs.cardsInPlay.Count == 0 && hand.Contains("AS"))
            return "AS";

        // No lead suit yet (player is leading) — play first card in hand
        if (string.IsNullOrEmpty(_gs.leadSuit))
            return hand[0];

        // Try to play a card of the lead suit
        var suitCards = hand.FindAll(c => GetSuit(c) == _gs.leadSuit);
        if (suitCards.Count > 0)
            return suitCards[0];

        // Thulla — play any card
        return hand[0];
    }

    // ── Local Player Move ─────────────────────────────────────────────────────

    private void HandleLocalCardPlayed(string cardCode)
    {
        if (!_myTurnActive) return;
        if (_gs?.CurrentPlayerId != PlayerDataManager.PlayFabId) return;
        if (!_myHand.Contains(cardCode)) return;

        // Validate: must follow suit if possible (and not leading)
        if (!string.IsNullOrEmpty(_gs.leadSuit))
        {
            bool hasLeadSuit = _myHand.Exists(c => GetSuit(c) == _gs.leadSuit);
            if (hasLeadSuit && GetSuit(cardCode) != _gs.leadSuit)
            {
                Debug.LogWarning("[GameManager] Must follow suit!");
                return;
            }
        }

        _myTurnActive = false;
        StopTurnTimer();
        ExecuteMove(PlayerDataManager.PlayFabId, cardCode);
    }

    // ── Steal Hand ────────────────────────────────────────────────────────────

    private void HandleStealHand()
    {
        if (_gs == null || !_myTurnActive) return;
        if (_gs.CurrentPlayerId != PlayerDataManager.PlayFabId) return;
        if (!string.IsNullOrEmpty(_gs.leadSuit)) return; // can only steal when leading

        string localId = PlayerDataManager.PlayFabId;
        string leftId = GetNextActivePlayerLeft(localId);

        if (string.IsNullOrEmpty(leftId)) return;
        if (!_gs.hands.ContainsKey(leftId) || _gs.hands[leftId].Count == 0) return;

        // Move left player's cards to local hand
        var stolen = new List<string>(_gs.hands[leftId]);
        _gs.hands[leftId].Clear();
        _gs.hands[localId].AddRange(stolen);
        _myHand = new List<string>(_gs.hands[localId]);
        EventManager.FireLocalHandUpdated(new List<string>(_myHand));

        // Mark left player as winner
        if (!_gs.winners.Contains(leftId))
            _gs.winners.Add(leftId);
        _gs.activePlayers.Remove(leftId);

        // Adjust currentPlayerIndex after removal
        AdjustCurrentIndexAfterRemoval();

        // Write and continue — local player still leads
        _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WriteGameState(() =>
        {
            Debug.Log($"[GameManager] Stole hand from {leftId}.");
            EventManager.FireGameStateUpdated(_gs);
        });
    }

    // ── Shootout ──────────────────────────────────────────────────────────────

    private void ProcessShootoutTurn()
    {
        string localId = PlayerDataManager.PlayFabId;
        float remaining = _gs.SecondsRemaining(GameState.ShootoutSeconds);
        _turnTimerCoroutine = StartCoroutine(ShootoutTimerCoroutine(remaining));

        if (_gs.shootoutDrawerId == localId)
        {
            Debug.Log("[GameManager] Shootout: I am the drawer.");
        }
        else if (_isHost && IsBot(_gs.shootoutDrawerId))
        {
            float delay = Mathf.Min(UnityEngine.Random.Range(2f, 8f), remaining - 1f);
            _botCoroutine = StartCoroutine(BotShootoutDrawCoroutine(_gs.shootoutDrawerId, delay));
        }
    }

    private IEnumerator ShootoutTimerCoroutine(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (_gs?.phase != GameState.PhaseShootout) yield break;

        string drawerId = _gs.shootoutDrawerId;
        string localId = PlayerDataManager.PlayFabId;

        if (drawerId == localId)
            ExecuteShootoutDraw(localId);
        else if (_isHost && IsBot(drawerId))
            ExecuteShootoutDraw(drawerId);
    }

    private void HandleShootoutCardChosen()
    {
        if (_gs?.phase != GameState.PhaseShootout) return;
        if (_gs.shootoutDrawerId != PlayerDataManager.PlayFabId) return;

        StopTurnTimer();
        ExecuteShootoutDraw(PlayerDataManager.PlayFabId);
    }

    /// <summary>
    /// Drawer picks a random card from opponent's hand.
    /// Then the drawer plays that card as a normal lead.
    /// </summary>
    private void ExecuteShootoutDraw(string drawerId)
    {
        // Find the other active player
        string responderId = "";
        foreach (var pid in _gs.activePlayers)
            if (pid != drawerId)
            {
                responderId = pid;
                break;
            }

        if (string.IsNullOrEmpty(responderId)) return;
        if (!_gs.hands.ContainsKey(responderId) || _gs.hands[responderId].Count == 0) return;

        // Pick random card from responder's hand
        var responderHand = _gs.hands[responderId];
        int idx = UnityEngine.Random.Range(0, responderHand.Count);
        string drawnCard = responderHand[idx];

        // Remove from responder, add to drawer
        responderHand.RemoveAt(idx);
        if (!_gs.hands.ContainsKey(drawerId)) _gs.hands[drawerId] = new List<string>();
        _gs.hands[drawerId].Add(drawnCard);

        // Sync local hand if we are the drawer
        if (drawerId == PlayerDataManager.PlayFabId)
        {
            _myHand = new List<string>(_gs.hands[drawerId]);
            EventManager.FireLocalHandUpdated(new List<string>(_myHand));
        }

        // Now drawer leads with that drawn card
        // Switch phase back to playing, drawer is current player, clear leadSuit
        _gs.phase = GameState.PhasePlaying;
        _gs.leadSuit = "";
        _gs.cardsInPlay.Clear();
        _gs.currentPlayerIndex = _gs.activePlayers.IndexOf(drawerId);
        _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _gs.shootoutDrawerId = "";

        // Drawer must play the drawn card
        ExecuteMove(drawerId, drawnCard);
    }

    private IEnumerator BotShootoutDrawCoroutine(string botId, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (_gs?.phase == GameState.PhaseShootout && _gs.shootoutDrawerId == botId)
            ExecuteShootoutDraw(botId);
    }

    // ── Core Move Execution ───────────────────────────────────────────────────

    /// <summary>
    /// The single authoritative method for playing a card.
    /// Called for local player, bots, and auto-play.
    /// Removes card from hand, adds to cardsInPlay, resolves round if complete.
    /// </summary>
    private void ExecuteMove(string playerId, string cardCode)
    {
        if (_gs == null) return;
        if (!_gs.hands.ContainsKey(playerId)) return;
        if (!_gs.hands[playerId].Contains(cardCode))
        {
            // Fallback: play first available card
            if (_gs.hands[playerId].Count == 0) return;
            cardCode = _gs.hands[playerId][0];
        }

        // Remove card from hand
        _gs.hands[playerId].Remove(cardCode);
        if (playerId == PlayerDataManager.PlayFabId)
            _myHand.Remove(cardCode);

        // Set lead suit on first card of round
        if (_gs.cardsInPlay.Count == 0)
            _gs.leadSuit = GetSuit(cardCode);

        // Add to cardsInPlay
        _gs.cardsInPlay.Add(new PlayedCard(playerId, cardCode));

        Debug.Log($"[GameManager] {playerId} played {cardCode}. Cards in play: {_gs.cardsInPlay.Count}");

        // Check if player just ran out of cards
        bool playerRanOut = _gs.hands[playerId].Count == 0;

        // Check if round is complete
        bool roundComplete = _gs.cardsInPlay.Count >= _gs.activePlayers.Count;

        if (roundComplete)
        {
            ResolveRound(playerRanOut);
        }
        else
        {
            // Check if this card is out of suit — round stops immediately
            bool outOfSuit = GetSuit(cardCode) != _gs.leadSuit;

            if (outOfSuit && _gs.roundNumber > 1)
            {
                // Round stops — highest lead suit card holder picks up
                ResolveOutOfSuit();
            }
            else
            {
                // Advance to next player
                AdvanceToNextPlayer();
                _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                WriteGameStateAndProcess();
            }
        }
    }

    // ── Round Resolution ──────────────────────────────────────────────────────

    /// <summary>
    /// All players played a card of the same suit (or round 1).
    /// Discard all cards. Highest lead suit card wins the lead.
    /// </summary>
    private void ResolveRound(bool lastPlayerRanOut)
    {
        // Find highest card of lead suit
        string winnerId = "";
        int highestRank = -1;

        foreach (var pc in _gs.cardsInPlay)
        {
            if (GetSuit(pc.card) != _gs.leadSuit) continue;
            int rank = GetRankValue(pc.card);
            if (rank > highestRank)
            {
                highestRank = rank;
                winnerId = pc.playerId;
            }
        }

        Debug.Log($"[GameManager] Round {_gs.roundNumber} complete. Winner of round: {winnerId}");

        // Discard all cards
        _gs.cardsInPlay.Clear();
        _gs.leadSuit = "";
        _gs.roundNumber++;

        // Check for players who ran out of cards
        CheckForNewWinners();

        // Special Rule 3: if round winner has no cards, skip to their left
        if (!string.IsNullOrEmpty(winnerId))
        {
            if (_gs.hands.ContainsKey(winnerId) && _gs.hands[winnerId].Count == 0)
            {
                // Winner ran out — player to their left leads
                int winnerIdx = _gs.activePlayers.IndexOf(winnerId);
                if (winnerIdx >= 0)
                    winnerId = GetPlayerIdAt((winnerIdx + 1) % _gs.activePlayers.Count);
            }
        }

        // Check game over
        if (CheckGameOver()) return;

        // Set next leader
        if (!string.IsNullOrEmpty(winnerId) && _gs.activePlayers.Contains(winnerId))
            _gs.currentPlayerIndex = _gs.activePlayers.IndexOf(winnerId);
        else
            AdvanceToNextPlayer();

        _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WriteGameStateAndProcess();
    }

    /// <summary>
    /// A player played out of suit (thulla) in round 2+.
    /// Highest lead suit card holder picks up ALL cards in play.
    /// Play stops immediately — don't wait for remaining players.
    /// </summary>
    private void ResolveOutOfSuit()
    {
        string pickupId = "";
        int highestRank = -1;

        foreach (var pc in _gs.cardsInPlay)
        {
            if (GetSuit(pc.card) != _gs.leadSuit) continue;
            int rank = GetRankValue(pc.card);
            if (rank > highestRank)
            {
                highestRank = rank;
                pickupId = pc.playerId;
            }
        }

        Debug.Log($"[GameManager] Out of suit! {pickupId} picks up {_gs.cardsInPlay.Count} cards.");

        // Give all cards in play to the pickup player
        if (!string.IsNullOrEmpty(pickupId) && _gs.hands.ContainsKey(pickupId))
        {
            foreach (var pc in _gs.cardsInPlay)
                _gs.hands[pickupId].Add(pc.card);
        }

        _gs.cardsInPlay.Clear();
        _gs.leadSuit = "";

        // Pickup player leads next round — set BEFORE firing hand update
        // so InGameView.RefreshCardInteractability sees correct CurrentPlayerId
        if (!string.IsNullOrEmpty(pickupId) && _gs.activePlayers.Contains(pickupId))
            _gs.currentPlayerIndex = _gs.activePlayers.IndexOf(pickupId);

        // Fire hand update AFTER currentPlayerIndex is set, so RefreshCardInteractability
        // correctly enables cards for the pickup player (bug fix #3 and #4)
        if (pickupId == PlayerDataManager.PlayFabId)
        {
            _myHand = new List<string>(_gs.hands[pickupId]);
            EventManager.FireLocalHandUpdated(new List<string>(_myHand));
        }

        CheckForNewWinners();
        if (CheckGameOver()) return;

        _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WriteGameStateAndProcess();
    }

    // ── Win / Game Over ───────────────────────────────────────────────────────

    /// <summary>Mark any players whose hand is empty as winners and remove from active.</summary>
    private void CheckForNewWinners()
    {
        var toRemove = new List<string>();
        foreach (var pid in _gs.activePlayers)
        {
            if (_gs.hands.ContainsKey(pid) && _gs.hands[pid].Count == 0)
            {
                if (!_gs.winners.Contains(pid))
                {
                    _gs.winners.Add(pid);
                    Debug.Log($"[GameManager] {pid} has won!");
                }

                toRemove.Add(pid);
            }
        }

        foreach (var pid in toRemove)
            _gs.activePlayers.Remove(pid);

        AdjustCurrentIndexAfterRemoval();
    }

    /// <summary>Returns true if game is over (1 or fewer active players).</summary>
    private bool CheckGameOver()
    {
        if (_gs.activePlayers.Count > 1) return false;

        if (_gs.activePlayers.Count == 1)
        {
            // Last player is Bhabhi
            _gs.bhabhi = _gs.activePlayers[0];
            _gs.activePlayers.Clear();
            Debug.Log($"[GameManager] Game over. Bhabhi: {_gs.bhabhi}");
        }
        else if (_gs.activePlayers.Count == 0)
        {
            // Shouldn't happen but handle gracefully
            if (_gs.winners.Count > 0)
                _gs.bhabhi = _gs.winners[_gs.winners.Count - 1];
        }

        _gs.phase = GameState.PhaseFinished;
        WriteGameState(() =>
        {
            EventManager.FireGameStateUpdated(_gs);
            HandleGameFinished();
        });
        return true;
    }

    /// <summary>
    /// Check if we need a shootout (2 players left, drawer has 0 cards after discard).
    /// Called before CheckGameOver when a round completes.
    /// </summary>
    private bool CheckShootout()
    {
        if (_gs.activePlayers.Count != 2) return false;

        // Find if one player has 0 cards
        string zeroCardId = "";
        foreach (var pid in _gs.activePlayers)
            if (_gs.hands.ContainsKey(pid) && _gs.hands[pid].Count == 0)
            {
                zeroCardId = pid;
                break;
            }

        if (string.IsNullOrEmpty(zeroCardId)) return false;

        // The player with 0 cards becomes the drawer
        _gs.phase = GameState.PhaseShootout;
        _gs.shootoutDrawerId = zeroCardId;
        _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Debug.Log($"[GameManager] Shootout! Drawer: {zeroCardId}");
        WriteGameStateAndProcess();
        return true;
    }

    private void HandleGameFinished()
    {
        _gameActive = false;
        StopTurnTimer();
        StopBotCoroutine();
        StopListener();

        // Only host pays out coins
        if (_isHost)
        {
            int payout = Mathf.RoundToInt(_room.entryFee * 1.25f);
            EventManager.FireAwardGameCoinsRequested(_gs.winners, payout);
        }

        EventManager.FireGameFinished(_gs.winners, _gs.bhabhi);
    }

    // ── Bot Move ──────────────────────────────────────────────────────────────

    private IEnumerator BotMoveCoroutine(string botId, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (_gs?.CurrentPlayerId != botId) yield break;
        if (!_gameActive) yield break;

        AutoPlay(botId);
    }

    private void StopBotCoroutine()
    {
        if (_botCoroutine != null)
        {
            StopCoroutine(_botCoroutine);
            _botCoroutine = null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool IsBot(string id) =>
        id != null && id.StartsWith("BOT_");

    private string GetSuit(string code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        return code[code.Length - 1].ToString();
    }

    /// <summary>
    /// Returns numeric rank value for comparison (Ace highest = 14).
    /// Rank codes: A=14, K=13, Q=12, J=11, 10=10, 9=9 ... 2=2
    /// </summary>
    private int GetRankValue(string code)
    {
        if (string.IsNullOrEmpty(code)) return 0;
        string rankStr = code.Substring(0, code.Length - 1);
        switch (rankStr)
        {
            case "A": return 14;
            case "K": return 13;
            case "Q": return 12;
            case "J": return 11;
            default: return int.TryParse(rankStr, out int v) ? v : 0;
        }
    }

    private void AdvanceToNextPlayer()
    {
        if (_gs.activePlayers.Count == 0) return;
        _gs.currentPlayerIndex = (_gs.currentPlayerIndex + 1) % _gs.activePlayers.Count;
    }

    private void AdjustCurrentIndexAfterRemoval()
    {
        if (_gs.activePlayers.Count == 0)
        {
            _gs.currentPlayerIndex = 0;
            return;
        }

        _gs.currentPlayerIndex = _gs.currentPlayerIndex % _gs.activePlayers.Count;
    }

    private string GetPlayerIdAt(int index)
    {
        if (_gs.activePlayers.Count == 0) return "";
        return _gs.activePlayers[index % _gs.activePlayers.Count];
    }

    /// <summary>
    /// Returns the id of the nearest active player to the immediate left of given player.
    /// Skips winners. Returns "" if only one active player or none found.
    /// </summary>
    private string GetNextActivePlayerLeft(string fromId)
    {
        if (_gs.activePlayers.Count <= 1) return "";

        // Use original room order to determine "left" (next clockwise)
        var allIds = new List<string>();
        foreach (var p in _room.players) allIds.Add(p.id);

        int fromIdx = allIds.IndexOf(fromId);
        if (fromIdx < 0) return "";

        for (int i = 1; i < allIds.Count; i++)
        {
            string candidate = allIds[(fromIdx + i) % allIds.Count];
            if (_gs.activePlayers.Contains(candidate) && candidate != fromId)
                return candidate;
        }

        return "";
    }

    // ── Firestore Write ───────────────────────────────────────────────────────

    private void WriteGameState(Action onComplete = null)
    {
        if (_room == null)
        {
            onComplete?.Invoke();
            return;
        }

        var update = new Dictionary<string, object>
        {
            { "gameState", _gs.ToDictionary() }
        };

        FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(_room.roomId)
            .UpdateAsync(update)
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted)
                    Debug.LogError($"[GameManager] WriteGameState failed: {task.Exception}");
                else
                    onComplete?.Invoke();
            });
    }

    // ── Write + Process ───────────────────────────────────────────────────────

    /// <summary>
    /// Writes game state to Firestore, fires the update event, then immediately
    /// calls ProcessCurrentTurn on the host.
    /// This avoids relying on the Firestore listener echo to trigger the next turn,
    /// which can stall in single-client or slow-network scenarios.
    /// </summary>
    private void WriteGameStateAndProcess()
    {
        EventManager.FireGameStateUpdated(_gs);
        WriteGameState(() =>
        {
            if (_isHost)
                ProcessCurrentTurn();
        });
    }

    // ── Leave ─────────────────────────────────────────────────────────────────

    private void HandleLeaveGame()
    {
        _gameActive = false;
        _myTurnActive = false;
        StopAllCoroutines();
        StopListener();
    }
}