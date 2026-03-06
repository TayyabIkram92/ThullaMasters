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
    private bool _isExecutingMove; // true while a move is in-flight (prevents double-play)
    private int _lastProcessedTurnKey = -1; // roundNumber*1000+playerIndex, avoids double-processing
    private Coroutine _turnTimerCoroutine;
    private Coroutine _botCoroutine;
    private Coroutine _resolveCoroutine; // 3-second "show cards" delay before resolving
    private ListenerRegistration _listener;

    // How long all players can see the played cards before the round resolves
    private const float ResolveDelaySeconds = 3f;

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
        // Always fetch room from InGameManager — it has the post-deal state with hands.
        _room = GetCurrentRoom();
        if (_room == null)
        {
            Debug.LogError("[GameManager] HandleGameReady: CurrentRoom is null. Cannot start.");
            return;
        }

        _isHost = _room.hostId == PlayerDataManager.PlayFabId;
        _gameActive = false;
        _isExecutingMove = false;
        _lastProcessedTurnKey = -1;
        _gs = null;

        // Cache local hand
        _myHand.Clear();
        foreach (var c in localHand) _myHand.Add(c.ShortCode);

        Debug.Log($"[GameManager] HandleGameReady. isHost={_isHost} hand={_myHand.Count} " +
                  $"room={_room.roomId} players={_room.players.Count} hands={_room.hands.Count}");

        if (_isHost)
            InitialiseGameState();
        else
            StartListening();
    }

    private RoomData GetCurrentRoom()
    {
        // MUST use InGameManager — it holds the post-deal room with hands + sanitized IDs.
        // MatchmakingManager.CurrentRoom is the pre-deal room (no hands dict).
        return InGameManager.Instance?.CurrentRoom;
    }

    // ── Host: Build Initial GameState ─────────────────────────────────────────

    private void InitialiseGameState()
    {
        _room = GetCurrentRoom();
        if (_room == null)
        {
            Debug.LogError("[GameManager] InitialiseGameState: Room is null.");
            return;
        }

        Debug.Log($"[GameManager] InitialiseGameState: {_room.players.Count} players, " +
                  $"{_room.hands.Count} hands.");

        if (_room.hands.Count == 0)
        {
            Debug.LogError("[GameManager] InitialiseGameState: Room has no hands. " +
                           "DealCards may not have completed yet.");
            return;
        }

        _gs = new GameState();
        _gs.phase = GameState.PhasePlaying;
        _gs.roundNumber = 1;

        // activePlayers in room order
        foreach (var p in _room.players)
            _gs.activePlayers.Add(p.id);

        // Copy hands from room
        foreach (var kv in _room.hands)
            _gs.hands[kv.Key] = new List<string>(kv.Value);

        // Find who has AS → first player
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
            Debug.Log($"[GameManager] GameState written. First player index={startIndex} " +
                      $"({_gs.CurrentPlayerId})");
            _gameActive = true;
            StartListening();
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

                // ── Bug 1 fix: detect if local hand GAINED cards ──────────────
                // ResolveOutOfSuit only fires FireLocalHandUpdated on the HOST device.
                // For non-host: Firestore listener is the only delivery path.
                // If local hand count increased (thulla pickup / steal / shootout),
                // we must fire FireLocalHandUpdated ourselves so InGameView rebuilds.
                string localId = PlayerDataManager.PlayFabId;
                int oldHandCount = (_gs != null && _gs.hands.ContainsKey(localId))
                    ? _gs.hands[localId].Count
                    : -1;

                _gs = newGs;

                // Sync local hand from Firestore (authoritative)
                bool handUpdated = false;
                if (_gs.hands.ContainsKey(localId))
                {
                    var newHand = _gs.hands[localId];
                    // Hand gained cards (thulla/steal/shootout) — or first sync
                    if (newHand.Count != _myHand.Count ||
                        (oldHandCount >= 0 && newHand.Count > oldHandCount))
                    {
                        _myHand = new List<string>(newHand);
                        handUpdated = true;
                    }
                    else
                    {
                        _myHand = new List<string>(newHand);
                    }
                }

                _gameActive = _gs.phase != GameState.PhaseFinished;

                // ── Bug 2 fix: reset _isExecutingMove before FireGameStateUpdated ──
                // For non-host: after ExecuteMove → WriteGameStateAndProcess fires
                // FireGameStateUpdated immediately (enabling buttons), then Firestore
                // write completes. The listener echo arrives later with turnChanged=true.
                // During that gap the player sees enabled buttons but _isExecutingMove
                // is still true → "move already in-flight" rejection.
                // Fix: reset here so cards are usable the moment buttons light up.
                if (turnChanged)
                    _isExecutingMove = false;

                EventManager.FireGameStateUpdated(_gs);

                // Fire hand rebuild if local hand gained cards (thulla/steal/shootout).
                // FireLocalHandUpdated (→ SpawnCards) is only fired on the HOST device
                // inside ResolveOutOfSuit/HandleStealHand. For non-host this listener
                // is the only delivery path — fire it whenever local hand content changed.
                if (handUpdated)
                    EventManager.FireLocalHandUpdated(new List<string>(_myHand), _gs);

                if (_gameActive && turnChanged)
                {
                    // Both host AND non-host call ProcessCurrentTurn:
                    // • Non-host: enables their turn, starts timer.
                    // • Host: schedules bot moves after non-host players write their card.
                    //   Without this, bots stall because host listener previously skipped it.
                    ProcessCurrentTurn();
                }

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

        if (_gs.phase == GameState.PhaseFinished) return;

        string currentId = _gs.CurrentPlayerId;
        string localId = PlayerDataManager.PlayFabId;

        if (string.IsNullOrEmpty(currentId))
        {
            Debug.LogWarning("[GameManager] ProcessCurrentTurn: currentId is empty.");
            return;
        }

        // Dedup: if host already processed this exact turn (from write callback)
        // and the listener echo fires it again, skip to prevent double bot scheduling.
        int turnKey = _gs.roundNumber * 1000 + _gs.currentPlayerIndex;
        if (turnKey == _lastProcessedTurnKey)
        {
            Debug.Log($"[GameManager] ProcessCurrentTurn: already processed turn key {turnKey}, skipping.");
            return;
        }

        _lastProcessedTurnKey = turnKey;

        // Reset move guard for the new turn
        _isExecutingMove = false;

        float remaining = _gs.SecondsRemaining(GameState.TurnSeconds);
        if (remaining < 2f) remaining = 2f;

        Debug.Log($"[GameManager] ProcessCurrentTurn: current={currentId} local={localId} " +
                  $"round={_gs.roundNumber} remaining={remaining:F1}s isHost={_isHost}");

        if (currentId == localId)
            Debug.Log($"[GameManager] MY TURN. Round={_gs.roundNumber} Lead='{_gs.leadSuit}'");

        // Start timer for everyone: drives UI bar + auto-play fallback
        _turnTimerCoroutine = StartCoroutine(TurnTimerCoroutine(remaining));

        // Host schedules bot move
        if (_isHost && IsBot(currentId))
        {
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
        if (_gs == null) yield break;

        string currentId = _gs.CurrentPlayerId;
        string localId = PlayerDataManager.PlayFabId;

        if (currentId == localId && !_isExecutingMove)
        {
            Debug.Log("[GameManager] Timer expired — auto-playing for local player.");
            _isExecutingMove = true;
            AutoPlay(localId);
        }
        else if (_isHost && IsBot(currentId))
        {
            Debug.Log($"[GameManager] Bot timer expired — auto-playing for {currentId}.");
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
        // Round 1 leader MUST play Ace of Spades
        if (_gs.roundNumber == 1 && _gs.cardsInPlay.Count == 0 && hand.Contains("AS"))
            return "AS";

        // Round 1 followers: must play a spade if they have one.
        // If no spade, play any card — goes to discard regardless (Special Rule 1).
        if (_gs.roundNumber == 1 && _gs.cardsInPlay.Count > 0)
        {
            var spades = hand.FindAll(c => GetSuit(c) == "S");
            return spades.Count > 0 ? spades[0] : hand[0];
        }

        // No lead suit set (player is leading) — play first card in hand
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
        // Guard: game must be active and in playing phase
        if (_gs == null || !_gameActive) return;
        if (_gs.phase != GameState.PhasePlaying) return;

        string localId = PlayerDataManager.PlayFabId;

        // Guard: must be this player's turn according to authoritative game state
        if (_gs.CurrentPlayerId != localId)
        {
            Debug.LogWarning($"[GameManager] Card click ignored — not my turn. " +
                             $"Current={_gs.CurrentPlayerId} Me={localId}");
            return;
        }

        // Guard: prevent double-play (second click while move is in-flight)
        if (_isExecutingMove)
        {
            Debug.LogWarning("[GameManager] Card click ignored — move already in-flight.");
            return;
        }

        // Guard: must have this card
        if (!_myHand.Contains(cardCode))
        {
            Debug.LogWarning($"[GameManager] Card click ignored — {cardCode} not in hand. " +
                             $"Hand={string.Join(",", _myHand)}");
            return;
        }

        // Round 1 leader: must play AS
        if (_gs.roundNumber == 1 && _gs.cardsInPlay.Count == 0 && cardCode != "AS")
        {
            Debug.LogWarning("[GameManager] Round 1 leader must play AS.");
            return;
        }

        // Round 1 followers: must play a spade if they have one
        if (_gs.roundNumber == 1 && _gs.cardsInPlay.Count > 0)
        {
            bool hasSpade = _myHand.Exists(c => GetSuit(c) == "S");
            if (hasSpade && GetSuit(cardCode) != "S")
            {
                Debug.LogWarning("[GameManager] Round 1: must play a spade.");
                return;
            }
        }

        // Normal rounds: must follow lead suit if possible
        if (_gs.roundNumber > 1 && !string.IsNullOrEmpty(_gs.leadSuit))
        {
            bool hasLeadSuit = _myHand.Exists(c => GetSuit(c) == _gs.leadSuit);
            if (hasLeadSuit && GetSuit(cardCode) != _gs.leadSuit)
            {
                Debug.LogWarning($"[GameManager] Must follow suit {_gs.leadSuit}.");
                return;
            }
        }

        // All guards passed — execute the move
        _isExecutingMove = true;
        StopTurnTimer();
        Debug.Log($"[GameManager] Local player plays {cardCode}.");
        ExecuteMove(localId, cardCode);
    }

    // ── Steal Hand ────────────────────────────────────────────────────────────

    private void HandleStealHand()
    {
        if (_gs == null || !_gameActive) return;
        if (_gs.phase != GameState.PhasePlaying) return;
        if (_gs.CurrentPlayerId != PlayerDataManager.PlayFabId) return;
        if (!string.IsNullOrEmpty(_gs.leadSuit)) return; // can only steal when leading
        if (_isExecutingMove) return;

        string localId = PlayerDataManager.PlayFabId;
        string leftId = GetNextActivePlayerLeft(localId);

        if (string.IsNullOrEmpty(leftId)) return;
        if (!_gs.hands.ContainsKey(leftId) || _gs.hands[leftId].Count == 0) return;

        // Move left player's cards to local hand
        var stolen = new List<string>(_gs.hands[leftId]);
        _gs.hands[leftId].Clear();
        _gs.hands[localId].AddRange(stolen);
        _myHand = new List<string>(_gs.hands[localId]);
        EventManager.FireLocalHandUpdated(new List<string>(_myHand), _gs);

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
            EventManager.FireLocalHandUpdated(new List<string>(_myHand), _gs);
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
        {
            _myHand.Remove(cardCode);
            // Immediately update view — removes the card GO from MyCards.
            // For thulla/steal/shootout, FireLocalHandUpdated fires again later
            // with the full new hand including picked-up cards.
            EventManager.FireLocalHandUpdated(new List<string>(_myHand), _gs);
        }

        // Set lead suit on first card of round
        if (_gs.cardsInPlay.Count == 0)
            _gs.leadSuit = GetSuit(cardCode);

        // Add to cardsInPlay
        _gs.cardsInPlay.Add(new PlayedCard(playerId, cardCode));

        Debug.Log($"[GameManager] {playerId} played {cardCode}. Cards in play: {_gs.cardsInPlay.Count}");

        // Check if round is complete
        bool roundComplete = _gs.cardsInPlay.Count >= _gs.activePlayers.Count;

        if (roundComplete)
        {
            // Improvement 1: write state with all cards still visible, then wait
            // ResolveDelaySeconds so every client can see the complete round before
            // cards are cleared and the next turn begins.
            StopResolveCoroutine();
            ShowCardsAndDelay(() => ResolveRound());
        }
        else
        {
            bool outOfSuit = GetSuit(cardCode) != _gs.leadSuit;

            if (outOfSuit && _gs.roundNumber > 1)
            {
                // Improvement 2: write state with thulla card visible, then wait
                // so everyone can see which card triggered the pickup.
                StopResolveCoroutine();
                ShowCardsAndDelay(() => ResolveOutOfSuit());
            }
            else
            {
                // Mid-round: advance immediately — no delay
                AdvanceToNextPlayer();
                _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                WriteGameStateAndProcess();
            }
        }
    }

    /// <summary>
    /// Write current state (cards still in cardsInPlay) to Firestore so every
    /// client can see them, then after ResolveDelaySeconds call the resolve action.
    /// </summary>
    private void ShowCardsAndDelay(Action resolve)
    {
        // Broadcast current state immediately — every client sees the cards.
        EventManager.FireGameStateUpdated(_gs);
        WriteGameState(() =>
        {
            // Start the delay coroutine only after the write succeeds so the
            // state is guaranteed visible on all devices before we proceed.
            _resolveCoroutine = StartCoroutine(ResolveAfterDelay(resolve));
        });
    }

    private IEnumerator ResolveAfterDelay(Action resolve)
    {
        yield return new WaitForSeconds(ResolveDelaySeconds);
        _resolveCoroutine = null;
        if (_gameActive) resolve?.Invoke();
    }

    // ── Round Resolution ──────────────────────────────────────────────────────

    /// <summary>
    /// All players played a card of the same suit (or round 1).
    /// Discard all cards. Highest lead-suit card wins the lead.
    /// Called after ResolveDelaySeconds so every client already saw all cards.
    /// </summary>
    private void ResolveRound()
    {
        string roundLeadSuit = _gs.leadSuit;

        // Find highest-card holder BEFORE clearing cardsInPlay
        string winnerId = "";
        int highestRank = -1;

        foreach (var pc in _gs.cardsInPlay)
        {
            if (GetSuit(pc.card) != roundLeadSuit) continue;
            int rank = GetRankValue(pc.card);
            if (rank > highestRank)
            {
                highestRank = rank;
                winnerId = pc.playerId;
            }
        }

        Debug.Log($"[GameManager] Round {_gs.roundNumber} complete. Winner: {winnerId}");

        // Save snapshot for Special Rule 3 BEFORE clearing
        var playedSnapshot = new List<PlayedCard>(_gs.cardsInPlay);

        // Discard all cards
        _gs.cardsInPlay.Clear();
        _gs.leadSuit = "";
        _gs.roundNumber++;

        // Remove players whose hands are now empty — they have won
        CheckForNewWinners();

        // ── Scenario R6: ALL active players ran out in the same round ──────────
        // winnerId (highest card) is the Bhabhi — they held on longest.
        if (_gs.activePlayers.Count == 0)
        {
            CheckGameOverWithBhabhi(winnerId);
            return;
        }

        // ── Special Rule 3: round winner ran out of cards ──────────────────────
        // winnerId no longer in activePlayers (just removed). The player with the
        // next-highest lead-suit card in this round leads instead.
        if (!string.IsNullOrEmpty(winnerId) && !_gs.activePlayers.Contains(winnerId))
        {
            string nextLeaderId = GetSecondHighestSuitCardPlayer(winnerId, roundLeadSuit, playedSnapshot);
            if (!string.IsNullOrEmpty(nextLeaderId))
                winnerId = nextLeaderId;
            else if (_gs.activePlayers.Count > 0)
                winnerId = _gs.activePlayers[0];
        }

        // Check shootout BEFORE game over (2 players left, one has 0 cards)
        if (CheckShootout()) return;

        // Check normal game over (1 player left = Bhabhi)
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
    /// Highest lead-suit card holder picks up ALL cards in play.
    /// Play stops immediately — remaining players do not play this round.
    /// Called after ResolveDelaySeconds so every client already saw the thulla card.
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

        Debug.Log($"[GameManager] Thulla! {pickupId} picks up {_gs.cardsInPlay.Count} cards.");

        // Give all cards in play to the pickup player
        if (!string.IsNullOrEmpty(pickupId) && _gs.hands.ContainsKey(pickupId))
        {
            foreach (var pc in _gs.cardsInPlay)
                _gs.hands[pickupId].Add(pc.card);
        }

        _gs.cardsInPlay.Clear();
        _gs.leadSuit = "";

        // ── BUG FIX: CheckForNewWinners BEFORE setting currentPlayerIndex ───────
        // CheckForNewWinners removes the thulla-giver (empty hand) from activePlayers.
        // If we set currentPlayerIndex = indexOf(pickupId) BEFORE that removal,
        // and the thulla-giver sat BEFORE pickupId in activePlayers, the removal
        // shifts pickupId left by 1 and AdjustCurrentIndexAfterRemoval mis-points.
        // Fix: remove finished players FIRST, then find pickupId's current index.
        CheckForNewWinners();

        // pickupId received cards, so their hand is never empty here — they stay active.
        // Set them as next leader now that activePlayers list is final.
        if (!string.IsNullOrEmpty(pickupId) && _gs.activePlayers.Contains(pickupId))
            _gs.currentPlayerIndex = _gs.activePlayers.IndexOf(pickupId);

        // Fire local hand update so the pickup player's UI rebuilds with new cards.
        // For non-host this is also delivered via the Firestore listener, but firing
        // locally gives instant feedback without waiting for the round-trip.
        if (pickupId == PlayerDataManager.PlayFabId)
        {
            _myHand = new List<string>(_gs.hands[pickupId]);
            EventManager.FireLocalHandUpdated(new List<string>(_myHand), _gs);
        }

        // Check shootout (2 players left, one has 0 cards)
        if (CheckShootout()) return;

        // Check game over (1 player left = Bhabhi)
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
            // Last remaining player is Bhabhi
            _gs.bhabhi = _gs.activePlayers[0];
            _gs.activePlayers.Clear();
            Debug.Log($"[GameManager] Game over. Bhabhi: {_gs.bhabhi}");
        }
        else
        {
            // activePlayers.Count == 0: everyone finished in the same round.
            // Caller must use CheckGameOverWithBhabhi to set the correct bhabhi.
            // Fallback: use last winner added (least-correct but never crashes).
            if (_gs.winners.Count > 0)
                _gs.bhabhi = _gs.winners[_gs.winners.Count - 1];
            Debug.LogWarning("[GameManager] CheckGameOver: 0 active players — bhabhi set to last winner.");
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
    /// Scenario R6: ALL active players ran out of cards in the same round.
    /// The player who held the highest card (forcedBhabhi) is the Bhabhi because
    /// they were the last one "holding" — everyone else already played their last card
    /// earlier in the same round.
    /// </summary>
    private void CheckGameOverWithBhabhi(string forcedBhabhi)
    {
        _gs.bhabhi = string.IsNullOrEmpty(forcedBhabhi) && _gs.winners.Count > 0
            ? _gs.winners[_gs.winners.Count - 1]
            : forcedBhabhi;

        _gs.activePlayers.Clear();
        Debug.Log($"[GameManager] Game over (all ran out). Bhabhi: {_gs.bhabhi}");

        _gs.phase = GameState.PhaseFinished;
        WriteGameState(() =>
        {
            EventManager.FireGameStateUpdated(_gs);
            HandleGameFinished();
        });
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
        _isExecutingMove = false;
        _lastProcessedTurnKey = -1;
        StopTurnTimer();
        StopBotCoroutine();
        StopResolveCoroutine();
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

    private void StopResolveCoroutine()
    {
        if (_resolveCoroutine != null)
        {
            StopCoroutine(_resolveCoroutine);
            _resolveCoroutine = null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool IsBot(string id) =>
        id != null && id.StartsWith("BOT_");

    /// <summary>
    /// Special Rule 3 helper: finds the player who played the second-highest
    /// card of the lead suit in the current round's cardsInPlay snapshot.
    /// Used when the round winner ran out of cards.
    /// excludeId = the winner (who just ran out, already removed from activePlayers).
    /// Returns "" if no other suit card was played.
    /// </summary>
    /// <summary>
    /// Special Rule 3: from a snapshot of this round's played cards,
    /// find the player (excluding the winner who ran out) with the highest
    /// lead-suit card who is still an active player.
    /// Uses a pre-clear snapshot since cardsInPlay is cleared before this runs.
    /// </summary>
    private string GetSecondHighestSuitCardPlayer(
        string excludeId, string leadSuit, List<PlayedCard> snapshot)
    {
        string bestId = "";
        int bestRank = -1;

        foreach (var pc in snapshot)
        {
            if (pc.playerId == excludeId) continue;
            if (GetSuit(pc.card) != leadSuit) continue;

            int rank = GetRankValue(pc.card);
            if (rank > bestRank)
            {
                bestRank = rank;
                bestId = pc.playerId;
            }
        }

        // Only valid if that player is still active (has cards left)
        if (!string.IsNullOrEmpty(bestId) && _gs.activePlayers.Contains(bestId))
            return bestId;

        return "";
    }

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
        // Reset move guard BEFORE firing the state update.
        // FireGameStateUpdated enables card buttons immediately (synchronous).
        // If _isExecutingMove is still true at that point, a click during the
        // async Firestore write window would be silently rejected.
        // ExecuteMove is synchronous and already finished, so it's safe to clear now.
        _isExecutingMove = false;

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
        _isExecutingMove = false;
        _lastProcessedTurnKey = -1;
        StopAllCoroutines();
        StopListener();
    }
}