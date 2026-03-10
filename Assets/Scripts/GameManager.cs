using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Firebase.Firestore;
using System.Text;
using System.IO;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }
    public GameState CurrentGs => _gs;
    private const string RoomsCollection = "rooms";
    private RoomData _room;
    private GameState _gs;
    private bool _isHost;
    private bool _gameActive;
    private bool _isExecutingMove;
    private int _lastProcessedTurnKey = -1;
    private int _subRoundIndex = 0; // increments every thulla pickup — makes turnKey unique within a round
    private Coroutine _turnTimerCoroutine;
    private Coroutine _botCoroutine;
    private Coroutine _resolveCoroutine;
    private ListenerRegistration _listener;
    private const float ResolveDelaySeconds = 3f;
    private List<string> _myHand = new List<string>();

    // ── Round Logger ──────────────────────────────────────────────────────────
    private readonly List<string> _roundLog = new List<string>();

    private readonly List<(string pid, string card, bool isThulla)> _currentRoundBuffer
        = new List<(string, string, bool)>();

    private List<SlotData> _seatedPlayers = new List<SlotData>();
    private string _initialHandSnapshot = "";

    [Header("Bot Difficulty")] [Tooltip("True = bots use hard AI strategy. False = bots use basic auto-play.")]
    public bool useHardBot = false;

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
        EventManager.OnLeaveGameRequested += HandleLeaveGame;
    }

    private void OnDisable()
    {
        EventManager.OnGameReady -= HandleGameReady;
        EventManager.OnLocalCardPlayed -= HandleLocalCardPlayed;
        EventManager.OnStealHandRequested -= HandleStealHand;
        EventManager.OnLeaveGameRequested -= HandleLeaveGame;
        StopListener();
        StopAllCoroutines();
    }

    private void HandleGameReady(List<CardData> localHand, List<SlotData> seatedPlayers)
    {
        _room = GetCurrentRoom();
        if (_room == null)
        {
            Debug.LogError("[GameManager] HandleGameReady: CurrentRoom is null.");
            return;
        }

        _isHost = _room.hostId == PlayerDataManager.PlayFabId;
        _gameActive = false;
        _isExecutingMove = false;
        _lastProcessedTurnKey = -1;
        _subRoundIndex = 0;
        _botStrategy.Reset();
        _roundLog.Clear();
        _initialHandSnapshot = "";
        _seatedPlayers = seatedPlayers != null ? new List<SlotData>(seatedPlayers) : new List<SlotData>();
        _myHand.Clear();
        foreach (var c in localHand) _myHand.Add(c.ShortCode);
        Debug.Log($"[GameManager] HandleGameReady. isHost={_isHost} hand={_myHand.Count} room={_room.roomId}");
        if (_isHost) InitialiseGameState();
        else StartListening();
    }

    private RoomData GetCurrentRoom() => InGameManager.Instance?.CurrentRoom;

    private void InitialiseGameState() => _ = InitialiseGameStateAsync();

    private async Task InitialiseGameStateAsync()
    {
        _room = GetCurrentRoom();
        if (_room == null)
        {
            Debug.LogError("[GameManager] InitialiseGameState: Room is null.");
            return;
        }

        if (_room.hands.Count == 0)
        {
            Debug.LogError("[GameManager] InitialiseGameState: no hands.");
            return;
        }

        _gs = new GameState();
        _gs.phase = GameState.PhasePlaying;
        _gs.roundNumber = 1;
        foreach (var p in _room.players) _gs.activePlayers.Add(p.id);
        foreach (var kv in _room.hands) _gs.hands[kv.Key] = new List<string>(kv.Value);

        BalanceHumanHands();

        LogInitialHandSnapshot();

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

        bool ok = await WriteGameStateAsync();
        if (!ok)
        {
            Debug.LogError("[GameManager] InitialiseGameState: write failed — aborting.");
            EventManager.FireShowPopUp("Network error starting game. Please try again.");
            return;
        }

        Debug.Log($"[GameManager] GameState confirmed. First player index={startIndex} ({_gs.CurrentPlayerId})");

        _gameActive = true;
        StartListening();
        EventManager.FireGameStateUpdated(_gs);
        ProcessCurrentTurn();
    }

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

                bool turnChanged = _gs == null ||
                                   newGs.currentPlayerIndex != _gs.currentPlayerIndex ||
                                   newGs.roundNumber != _gs.roundNumber ||
                                   newGs.phase != _gs.phase ||
                                   newGs.cardsInPlay.Count != _gs.cardsInPlay.Count;

                string localId = PlayerDataManager.PlayFabId;

                _gs = newGs;

                bool handUpdated = false;
                if (_gs.hands.ContainsKey(localId))
                {
                    var newHand = _gs.hands[localId];
                    if (!HandsMatchSorted(newHand, _myHand))
                    {
                        _myHand = new List<string>(newHand);
                        handUpdated = true;
                    }
                }

                _gameActive = _gs.phase != GameState.PhaseFinished;

                if (turnChanged) _isExecutingMove = false;

                EventManager.FireGameStateUpdated(_gs);

                if (handUpdated)
                    EventManager.FireLocalHandUpdated(new List<string>(_myHand), _gs);

                if (_gameActive && turnChanged)
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

    private bool HandsMatchSorted(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        var sa = new List<string>(a);
        sa.Sort();
        var sb = new List<string>(b);
        sb.Sort();
        for (int i = 0; i < sa.Count; i++)
            if (sa[i] != sb[i])
                return false;
        return true;
    }

    private void ProcessCurrentTurn()
    {
        StopTurnTimer();
        StopBotCoroutine();
        if (_gs == null || !_gameActive) return;
        if (_gs.phase == GameState.PhaseFinished) return;

        string currentId = _gs.CurrentPlayerId;
        string localId = PlayerDataManager.PlayFabId;
        if (string.IsNullOrEmpty(currentId))
        {
            Debug.LogWarning("[GameManager] ProcessCurrentTurn: empty.");
            return;
        }

        int turnKey = _gs.roundNumber * 100000 + _gs.currentPlayerIndex * 100 + _subRoundIndex;
        if (turnKey == _lastProcessedTurnKey)
        {
            Debug.Log($"[GameManager] Skipping dup turn {turnKey}");
            return;
        }

        _lastProcessedTurnKey = turnKey;
        _isExecutingMove = false;

        // INFINITE LOOP FIX: Check game over after auto-removing a player
        if (_gs.hands.ContainsKey(currentId) && _gs.hands[currentId].Count == 0)
        {
            if (!_gs.winners.Contains(currentId)) _gs.winners.Add(currentId);
            _gs.activePlayers.Remove(currentId);
            AdjustCurrentIndexAfterRemoval();

            if (CheckGameOver()) return; // Added safety

            WriteGameStateAndProcess();
            return;
        }

        float remaining = _gs.SecondsRemaining(GameState.TurnSeconds);
        if (remaining < 2f) remaining = 2f;

        Debug.Log($"[GameManager] ProcessCurrentTurn: current={currentId} round={_gs.roundNumber} isHost={_isHost}");
        if (currentId == localId) Debug.Log($"[GameManager] MY TURN round={_gs.roundNumber} lead='{_gs.leadSuit}'");

        _turnTimerCoroutine = StartCoroutine(TurnTimerCoroutine(remaining));

        if (_isHost && IsBot(currentId))
        {
            float botDelay = UnityEngine.Random.Range(1f, 3f);
            float effectiveDelay = Mathf.Min(botDelay, remaining - 1f);
            if (effectiveDelay < 0.5f) effectiveDelay = 0.5f;
            _botCoroutine = StartCoroutine(BotMoveCoroutine(currentId, effectiveDelay));
        }
    }

    private IEnumerator TurnTimerCoroutine(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (!_gameActive || _gs == null) yield break;
        string currentId = _gs.CurrentPlayerId;
        string localId = PlayerDataManager.PlayFabId;
        if (currentId == localId && !_isExecutingMove)
        {
            Debug.Log("[GameManager] Timer expired — auto-playing.");
            _isExecutingMove = true;
            AutoPlay(localId);
        }
        else if (_isHost && IsBot(currentId))
        {
            Debug.Log($"[GameManager] Bot timer expired for {currentId}.");
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

    private void AutoPlay(string playerId)
    {
        if (_gs == null || !_gs.hands.ContainsKey(playerId)) return;

        string card = useHardBot && IsBot(playerId)
            ? ChooseHardBotCard(playerId, _gs.hands[playerId])
            : ChooseAutoCard(playerId, _gs.hands[playerId]);

        if (card == "STEAL")
        {
            ExecuteSteal(playerId);
        }
        else
        {
            ExecuteMove(playerId, card);
        }
    }

    private void ExecuteSteal(string playerId)
    {
        string leftId = GetNextActivePlayerLeft(playerId);
        if (string.IsNullOrEmpty(leftId)) return;
        if (!_gs.hands.ContainsKey(leftId) || _gs.hands[leftId].Count == 0) return;
        var stolen = new List<string>(_gs.hands[leftId]);
        _gs.hands[leftId].Clear();
        _gs.hands[playerId].AddRange(stolen);
        if (!_gs.winners.Contains(leftId)) _gs.winners.Add(leftId);
        _gs.activePlayers.Remove(leftId);
        AdjustCurrentIndexAfterRemoval();

        if (CheckGameOver()) return; // Added safety

        // Reset turn key so ProcessCurrentTurn does not skip as duplicate
        // (same bot is still leading after stealing — round/index unchanged).
        _lastProcessedTurnKey = -1;
        _isExecutingMove = false;
        _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WriteGameStateAndProcess();
        Debug.Log($"[GameManager] Bot {playerId} stole from {leftId}.");
    }

    private readonly BotStrategy _botStrategy = new BotStrategy();

    private string ChooseHardBotCard(string botId, List<string> hand)
        => _botStrategy.ChooseCard(_gs, botId, hand);

    private void RecordPlayHistory(string playerId, string cardCode)
        => _botStrategy.RecordMove(_gs, playerId, cardCode);

    private string ChooseAutoCard(string playerId, List<string> hand)
    {
        if (_gs.roundNumber == 1 && _gs.cardsInPlay.Count == 0 && hand.Contains("AS")) return "AS";
        if (_gs.roundNumber == 1 && _gs.cardsInPlay.Count > 0)
        {
            var spades = hand.FindAll(c => GetSuit(c) == "S");
            return spades.Count > 0 ? spades[0] : hand[0];
        }

        if (string.IsNullOrEmpty(_gs.leadSuit)) return hand[0];
        var suitCards = hand.FindAll(c => GetSuit(c) == _gs.leadSuit);
        return suitCards.Count > 0 ? suitCards[0] : hand[0];
    }

    private void HandleLocalCardPlayed(string cardCode)
    {
        if (_gs == null || !_gameActive) return;
        if (_gs.phase != GameState.PhasePlaying) return;
        string localId = PlayerDataManager.PlayFabId;
        if (_gs.CurrentPlayerId != localId)
        {
            Debug.LogWarning($"[GameManager] Not my turn. Current={_gs.CurrentPlayerId}");
            return;
        }

        if (_isExecutingMove)
        {
            Debug.LogWarning("[GameManager] Move in-flight.");
            return;
        }

        if (!_myHand.Contains(cardCode))
        {
            Debug.LogWarning($"[GameManager] Card {cardCode} not in hand.");
            return;
        }

        // SOFTLOCK FIX: Only block if they ACTUALLY have the AS
        if (_gs.roundNumber == 1 && _gs.cardsInPlay.Count == 0)
        {
            if (_myHand.Contains("AS") && cardCode != "AS")
            {
                Debug.LogWarning("[GameManager] Must play AS.");
                return;
            }
        }

        if (_gs.roundNumber == 1 && _gs.cardsInPlay.Count > 0)
        {
            bool hasSpade = _myHand.Exists(c => GetSuit(c) == "S");
            if (hasSpade && GetSuit(cardCode) != "S")
            {
                Debug.LogWarning("[GameManager] Must play spade.");
                return;
            }
        }

        if (_gs.roundNumber > 1 && !string.IsNullOrEmpty(_gs.leadSuit))
        {
            bool hasLeadSuit = _myHand.Exists(c => GetSuit(c) == _gs.leadSuit);
            if (hasLeadSuit && GetSuit(cardCode) != _gs.leadSuit)
            {
                Debug.LogWarning($"[GameManager] Must follow {_gs.leadSuit}.");
                return;
            }
        }

        _isExecutingMove = true;
        StopTurnTimer();
        ExecuteMove(localId, cardCode);
    }

    private void HandleStealHand()
    {
        if (_gs == null || !_gameActive) return;
        if (_gs.phase != GameState.PhasePlaying) return;
        if (_gs.CurrentPlayerId != PlayerDataManager.PlayFabId) return;
        if (!string.IsNullOrEmpty(_gs.leadSuit)) return;
        if (_isExecutingMove) return;

        string localId = PlayerDataManager.PlayFabId;

        // ── Find the next active player to steal from (human or bot) ─────────
        // Always steal from the immediately next active player in seat order,
        // regardless of whether they are a bot or human.
        string targetId = GetNextActivePlayerLeft(localId);
        if (string.IsNullOrEmpty(targetId))
        {
            Debug.Log("[Steal] No active player found to steal from.");
            return;
        }
        if (!_gs.hands.ContainsKey(targetId) || _gs.hands[targetId].Count == 0) return;

        // ── Only upgrade the hand when stealing from a bot ────────────────────
        // UpgradeStealHand swaps weak bot cards with stronger ones from other
        // bots. This makes no sense when stealing from a human — skip it.
        if (IsBot(targetId))
            UpgradeStealHand(targetId);

        // ── Give the (now upgraded) hand to the human ────────────────────────
        var stolen = new List<string>(_gs.hands[targetId]);
        _gs.hands[targetId].Clear();
        _gs.hands[localId].AddRange(stolen);
        _myHand = new List<string>(_gs.hands[localId]);
        EventManager.FireLocalHandUpdated(new List<string>(_myHand), _gs);
        if (!_gs.winners.Contains(targetId)) _gs.winners.Add(targetId);
        _gs.activePlayers.Remove(targetId);
        AdjustCurrentIndexAfterRemoval();

        if (CheckGameOver()) return;

        // Reset turn key so ProcessCurrentTurn does not treat this as a
        // duplicate turn — same player is still leading after the steal.
        _lastProcessedTurnKey = -1;
        _isExecutingMove = false;
        _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WriteGameStateAndProcess();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  UPGRADE STEAL HAND
    //
    //  Before the stolen bot hand is given to the human, compare every card in
    //  it against the same-suit cards held by ALL OTHER active bots.
    //  For each suit in the stolen hand:
    //    1. Collect all cards of that suit from other bots that are HIGHER than
    //       the stolen hand card.
    //    2. Swap the stolen hand's lower card with the other bot's higher card.
    //       → Stolen hand gets stronger; donor bot absorbs the weaker card.
    //    3. 2s and 3s: only kept in the stolen hand if NO other bot has ANY
    //       card of that suit (nothing to swap with).
    //
    //  A and K are ALLOWED in the stolen hand (no restriction here).
    //  Only 2 and 3 are pushed out if possible.
    //
    //  Example:
    //    Stolen hand (Bot1): 6H  4H  QS  7D  3C
    //    Bot2 has:           JH  KH  9S  5D  8C
    //    Bot3 has:           AH  10D  6C
    //
    //    Hearts:   6H < JH → swap 6H↔JH. 4H < KH → swap 4H↔KH.
    //              Stolen now has JH KH. Bot2 absorbs 6H 4H.
    //    Spades:   QS > 9S → no higher bot card. Keep QS.
    //    Diamonds: 7D < 10D → swap 7D↔10D. Stolen now has 10D. Bot3 absorbs 7D.
    //    Clubs:    3C is a 3. Bot2 has 8C (same suit) → swap 3C↔8C. Keep 8C.
    //              (If no bot had any club, 3C would stay in stolen hand.)
    //
    //  Final stolen hand given to human: JH KH QS 10D 8C
    // ══════════════════════════════════════════════════════════════════════════

    private void UpgradeStealHand(string stealTargetId)
    {
        if (!_gs.hands.ContainsKey(stealTargetId)) return;

        List<string> stealHand = _gs.hands[stealTargetId];

        // Collect all other active bots (exclude the steal target and any humans)
        List<string> otherBots = _gs.activePlayers
            .Where(pid => pid != stealTargetId && IsBot(pid) && _gs.hands.ContainsKey(pid))
            .ToList();

        if (otherBots.Count == 0)
        {
            Debug.Log("[Steal] No other bots to upgrade steal hand from.");
            return;
        }

        // Work suit by suit on a snapshot of current steal hand cards
        foreach (string suit in new[] { "S", "H", "D", "C" })
        {
            // Cards of this suit currently in the steal hand, sorted lowest→highest
            // (process weakest first so we give the human the best possible upgrade)
            List<string> stealSuitCards = stealHand
                .Where(c => GetSuit(c) == suit)
                .OrderBy(c => GetRankValue(c))
                .ToList();

            if (stealSuitCards.Count == 0) continue;

            foreach (string stealCard in stealSuitCards)
            {
                int stealRank = GetRankValue(stealCard);
                bool isLowBanned = stealRank == 2 || stealRank == 3;

                // ── Skip if this card is already known ────────────────────────
                // A known card has been played publicly already — no point
                // swapping it out, the human already knows it exists.
                if (_botStrategy.IsKnownCard(stealCard))
                {
                    Debug.Log($"[Steal Upgrade] Skipping known card {stealCard}");
                    continue;
                }

                // Find the best donor: another bot with the highest card of this
                // suit that is strictly greater than the steal card.
                // For 2s/3s: any higher card of this suit qualifies.
                // For normal cards: find the highest available upgrade.
                string bestDonorBot  = null;
                string bestDonorCard = null;
                int    bestDonorRank = stealRank; // must beat current card

                foreach (string botId in otherBots)
                {
                    List<string> botHand = _gs.hands[botId];
                    // Find highest card of this suit from this bot that beats stealCard
                    string candidate = botHand
                        .Where(c => GetSuit(c) == suit && GetRankValue(c) > bestDonorRank)
                        .OrderByDescending(c => GetRankValue(c))
                        .FirstOrDefault();

                    if (candidate != null)
                    {
                        bestDonorBot  = botId;
                        bestDonorCard = candidate;
                        bestDonorRank = GetRankValue(candidate);
                    }
                }

                if (bestDonorCard != null)
                {
                    // Perform swap: steal hand gets stronger card, donor bot gets weaker card
                    stealHand.Remove(stealCard);
                    _gs.hands[bestDonorBot].Remove(bestDonorCard);
                    stealHand.Add(bestDonorCard);
                    _gs.hands[bestDonorBot].Add(stealCard);

                    Debug.Log($"[Steal Upgrade] {stealTargetId} [{stealCard}] ↔ bot {bestDonorBot} [{bestDonorCard}]");
                }
                else if (isLowBanned)
                {
                    // 2 or 3 and no bot has a higher card of this suit.
                    // Check if any bot has ANY card of this suit at all —
                    // if they do, we could at least swap equal-or-lower (keep as-is),
                    // but since nothing is higher, the 2/3 stays in steal hand.
                    bool anyBotHasSuit = otherBots.Any(b =>
                        _gs.hands[b].Any(c => GetSuit(c) == suit));

                    if (!anyBotHasSuit)
                        Debug.Log($"[Steal Upgrade] No bot has {suit} — keeping {stealCard} in steal hand.");
                    else
                        Debug.Log($"[Steal Upgrade] No higher {suit} card available — keeping {stealCard}.");
                }
            }
        }
    }

    private void ExecuteMove(string playerId, string cardCode)
    {
        if (_gs == null) return;
        if (!_gs.hands.ContainsKey(playerId)) return;

        if (string.IsNullOrEmpty(cardCode) && _gs.hands[playerId].Count == 0)
        {
            if (!_gs.winners.Contains(playerId)) _gs.winners.Add(playerId);
            _gs.activePlayers.Remove(playerId);
            AdjustCurrentIndexAfterRemoval();

            if (CheckGameOver()) return; // Added safety

            WriteGameStateAndProcess();
            return;
        }

        if (!_gs.hands[playerId].Contains(cardCode))
        {
            if (_gs.hands[playerId].Count == 0) return;
            cardCode = _gs.hands[playerId][0];
        }

        _gs.hands[playerId].Remove(cardCode);
        if (playerId == PlayerDataManager.PlayFabId)
            _myHand.Remove(cardCode);

        if (_gs.cardsInPlay.Count == 0)
            _gs.leadSuit = GetSuit(cardCode);

        _gs.cardsInPlay.Add(new PlayedCard(playerId, cardCode));

        if (playerId == PlayerDataManager.PlayFabId)
            EventManager.FireLocalHandUpdated(new List<string>(_myHand), _gs);

        _currentRoundBuffer.Add((playerId, cardCode, GetSuit(cardCode) != _gs.leadSuit && _gs.cardsInPlay.Count > 1));
        RecordPlayHistory(playerId, cardCode);

        bool roundComplete = _gs.cardsInPlay.Count >= _gs.activePlayers.Count;
        bool outOfSuit = GetSuit(cardCode) != _gs.leadSuit && _gs.cardsInPlay.Count > 1;

        if (outOfSuit && _gs.roundNumber > 1)
        {
            StopResolveCoroutine();
            ShowCardsAndDelay(() => ResolveOutOfSuit());
        }
        else if (roundComplete)
        {
            StopResolveCoroutine();
            ShowCardsAndDelay(() => ResolveRound());
        }
        else
        {
            AdvanceToNextPlayer();
            _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            WriteGameStateAndProcess();
        }
    }

    private void ShowCardsAndDelay(Action resolve)
    {
        EventManager.FireGameStateUpdated(_gs);
        WriteGameState(() => { _resolveCoroutine = StartCoroutine(ResolveAfterDelay(resolve)); });
    }

    private IEnumerator ResolveAfterDelay(Action resolve)
    {
        yield return new WaitForSeconds(ResolveDelaySeconds);
        _resolveCoroutine = null;
        if (_gameActive) resolve?.Invoke();
    }

    private void ResolveRound()
    {
        string roundLeadSuit = _gs.leadSuit;
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

        FlushRoundLog(isThulla: false, pickupId: winnerId);
        _botStrategy.RecordDiscard(_gs.cardsInPlay.Select(pc => pc.card).ToList());
        _subRoundIndex = 0;
        var playedSnapshot = new List<PlayedCard>(_gs.cardsInPlay);
        _gs.cardsInPlay.Clear();
        _gs.leadSuit = "";
        _gs.roundNumber++;
        CheckForNewWinners();

        if (_gs.activePlayers.Count == 0)
        {
            CheckGameOverWithBhabhi(winnerId);
            return;
        }

        if (!string.IsNullOrEmpty(winnerId) && !_gs.activePlayers.Contains(winnerId))
        {
            string nextLeaderId = GetSecondHighestSuitCardPlayer(winnerId, roundLeadSuit, playedSnapshot);
            if (!string.IsNullOrEmpty(nextLeaderId)) winnerId = nextLeaderId;
            else if (_gs.activePlayers.Count > 0) winnerId = _gs.activePlayers[0];
        }

        if (CheckGameOver()) return;
        if (!string.IsNullOrEmpty(winnerId) && _gs.activePlayers.Contains(winnerId))
            _gs.currentPlayerIndex = _gs.activePlayers.IndexOf(winnerId);
        else
            AdvanceToNextPlayer();
        _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WriteGameStateAndProcess();
    }

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

        FlushRoundLog(isThulla: true, pickupId: pickupId);

        _subRoundIndex++;

        var receivedCards = new List<string>();
        bool special2pEnd = false;

        if (_gs.activePlayers.Count == 2 && _gs.hands.ContainsKey(pickupId) && _gs.hands[pickupId].Count == 0)
        {
            string tochooId = _gs.cardsInPlay[_gs.cardsInPlay.Count - 1].playerId;
            _gs.winners.Add(tochooId);
            _gs.activePlayers.Remove(tochooId);
            _gs.bhabhi = pickupId;
            special2pEnd = true;
        }
        else if (!string.IsNullOrEmpty(pickupId) && _gs.hands.ContainsKey(pickupId))
        {
            foreach (var pc in _gs.cardsInPlay)
            {
                _gs.hands[pickupId].Add(pc.card);
                receivedCards.Add(pc.card);
            }
        }

        _gs.cardsInPlay.Clear();
        _gs.leadSuit = "";

        if (!special2pEnd && !string.IsNullOrEmpty(pickupId) && IsBot(pickupId) && receivedCards.Count > 0)
            _botStrategy.RecordPickup(_gs, pickupId, receivedCards);

        CheckForNewWinners();

        if (!string.IsNullOrEmpty(pickupId) && _gs.activePlayers.Contains(pickupId))
            _gs.currentPlayerIndex = _gs.activePlayers.IndexOf(pickupId);

        if (pickupId == PlayerDataManager.PlayFabId)
        {
            _myHand = new List<string>(_gs.hands[pickupId]);
            EventManager.FireLocalHandUpdated(new List<string>(_myHand), _gs);
        }

        if (special2pEnd || CheckGameOver()) return;
        _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WriteGameStateAndProcess();
    }

    private void LogInitialHandSnapshot()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[INITIAL HANDS]");

        if (_seatedPlayers != null)
        {
            foreach (var slot in _seatedPlayers)
            {
                if (!_gs.hands.ContainsKey(slot.id)) continue;
                var hand = _gs.hands[slot.id];
                if (hand.Count == 0) continue;

                string label = LabelOf(slot.id);
                string cards = string.Join(", ", SortHandForDisplay(hand));
                sb.AppendLine($"  {label,-10}: {cards}");
            }
        }

        _initialHandSnapshot = sb.ToString();
        Debug.Log(_initialHandSnapshot);
    }

    private List<string> SortHandForDisplay(List<string> hand)
    {
        var sorted = new List<string>(hand);
        string suitOrder = "SHDC";
        sorted.Sort((a, b) =>
        {
            int sA = suitOrder.IndexOf(GetSuit(a));
            int sB = suitOrder.IndexOf(GetSuit(b));
            if (sA != sB) return sA.CompareTo(sB);
            return GetRankValue(b).CompareTo(GetRankValue(a));
        });
        return sorted;
    }

    private void FlushRoundLog(bool isThulla, string pickupId)
    {
        if (_currentRoundBuffer.Count == 0) return;

        int roundNum = _gs.roundNumber;

        var sb = new System.Text.StringBuilder();
        sb.Append($"[ROUND {roundNum,2}] ");

        for (int i = 0; i < _currentRoundBuffer.Count; i++)
        {
            var (pid, card, _) = _currentRoundBuffer[i];
            bool isThullaCard = isThulla && i == _currentRoundBuffer.Count - 1;

            sb.Append($"{card} ({LabelOf(pid)})");
            if (isThullaCard) sb.Append(" ← THULLA");
            if (i < _currentRoundBuffer.Count - 1) sb.Append("  ");
        }

        if (isThulla)
        {
            int cardCount = _currentRoundBuffer.Count;
            string pickupLabel = LabelOf(pickupId);
            sb.Append($"    → {pickupLabel} PICKS UP {cardCount} card{(cardCount == 1 ? "" : "s")}");
            if (_gs.hands.ContainsKey(pickupId))
                sb.Append($" (hand now {_gs.hands[pickupId].Count + cardCount})");
        }
        else
        {
            string winLabel = LabelOf(pickupId);
            sb.Append($"    → {winLabel} leads next");
        }

        string line = sb.ToString();
        _roundLog.Add(line);
        Debug.Log(line);

        _currentRoundBuffer.Clear();
    }

    private void DumpGameLog()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("══════ COMPLETE GAME LOG ══════");
        sb.Append(_initialHandSnapshot);
        sb.AppendLine();

        foreach (var round in _roundLog)
        {
            sb.AppendLine(round);
        }

        sb.Append("══════ END ══════");

        Debug.Log(sb.ToString());

        string logPath = Application.persistentDataPath + "/game_log.txt";
        File.WriteAllText(logPath, sb.ToString());
        Debug.Log($"[GameLog] Full log written to: {logPath}");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  HAND BALANCING  —  Humans must never hold a 2 or 3 of any suit.
    //
    //  After the initial deal, for every human player we scan their hand for
    //  any 2 or 3 (rank 2 / rank 3).  For each such card we find the closest
    //  bot seat that comes BEFORE this player in _seatedPlayers order (wrapping
    //  around the table if needed).  We then swap:
    //    • Prefer: bot's highest card of the SAME suit as the low card.
    //    • Fallback: bot's highest card of ANY suit.
    //  The bot absorbs the 2/3 and the human receives the stronger card.
    //  If there are NO bots at the table the hands are left untouched.
    // ══════════════════════════════════════════════════════════════════════════

    private void BalanceHumanHands()
    {
        if (_seatedPlayers == null || _seatedPlayers.Count == 0) return;

        // Quick check: are there any bots at all?
        bool anyBot = _seatedPlayers.Exists(s => s.isBot);
        if (!anyBot)
        {
            Debug.Log("[Balance] No bots present — skipping hand balancing.");
            return;
        }

        int seatCount = _seatedPlayers.Count;

        // Iterate every human seat
        for (int seatIdx = 0; seatIdx < seatCount; seatIdx++)
        {
            SlotData humanSlot = _seatedPlayers[seatIdx];
            if (humanSlot.isBot) continue;                          // skip bots
            if (!_gs.hands.ContainsKey(humanSlot.id)) continue;

            List<string> humanHand = _gs.hands[humanSlot.id];

            // Collect all cards the human must never hold: 2, 3, Ace (14), King (13).
            // Work on a snapshot so we can safely modify the list while iterating.
            List<string> lowCards = humanHand
                .FindAll(c => GetRankValue(c) == 2
                           || GetRankValue(c) == 3
                           || GetRankValue(c) == 13   // King
                           || GetRankValue(c) == 14); // Ace

            if (lowCards.Count == 0) continue;

            // Find the donor bot — walk backwards from this seat
            string donorBotId = FindPreviousBotId(seatIdx);
            if (string.IsNullOrEmpty(donorBotId))
            {
                Debug.Log($"[Balance] No bot found for human {humanSlot.id} — low cards kept.");
                continue;
            }

            List<string> botHand = _gs.hands[donorBotId];

            foreach (string lowCard in lowCards)
            {
                string lowSuit = GetSuit(lowCard);

                // 1. Try to find the bot's highest card of the SAME suit
                string donorCard = GetHighestCardOfSuit(botHand, lowSuit);

                // 2. Fallback: bot's highest card of any suit
                if (string.IsNullOrEmpty(donorCard))
                    donorCard = GetHighestCardOfAny(botHand);

                if (string.IsNullOrEmpty(donorCard))
                {
                    Debug.LogWarning($"[Balance] Bot {donorBotId} has no cards to swap for {lowCard}.");
                    continue;
                }

                // Perform the swap
                humanHand.Remove(lowCard);
                botHand.Remove(donorCard);
                humanHand.Add(donorCard);
                botHand.Add(lowCard);

                Debug.Log($"[Balance] Swapped human {humanSlot.id} [{lowCard}] ↔ bot {donorBotId} [{donorCard}]");
            }
        }

        // ── PASS 2 : Upgrade lower cards with the bot immediately AFTER ──────
        //
        // For each human, find the bot sitting immediately next (clockwise).
        // For every suit the human holds, collect all human cards of that suit
        // that are LOWER than the bot's highest card of the same suit.
        // Swap ALL such lower human cards with the bot's higher cards of that
        // same suit (one-for-one, highest bot card paired with lowest human card).
        // This is done suit-by-suit so the swap is always same-suit for same-suit.

        // ── Count humans vs bots for Pass 3 restriction logic ───────────────
        int humanCount = _seatedPlayers.Count(s => !s.isBot);
        int botCount   = _seatedPlayers.Count(s =>  s.isBot);

        Debug.Log("[Balance] ── Pass 2: upgrade lower suit cards via next bot ──");

        for (int seatIdx = 0; seatIdx < seatCount; seatIdx++)
        {
            SlotData humanSlot = _seatedPlayers[seatIdx];
            if (humanSlot.isBot) continue;
            if (!_gs.hands.ContainsKey(humanSlot.id)) continue;

            // Find the bot immediately AFTER this human (clockwise)
            string nextBotId = FindNextBotId(seatIdx);
            if (string.IsNullOrEmpty(nextBotId)) continue;

            List<string> humanHand = _gs.hands[humanSlot.id];
            List<string> botHand   = _gs.hands[nextBotId];

            // Work suit-by-suit
            foreach (string suit in new[] { "S", "H", "D", "C" })
            {
                // Bot's highest card of this suit
                string botHighest = GetHighestCardOfSuit(botHand, suit);
                if (string.IsNullOrEmpty(botHighest)) continue;    // bot has none of this suit
                int botHighRank = GetRankValue(botHighest);

                // All human cards of this suit that are strictly lower than bot's highest
                List<string> humanLower = humanHand
                    .FindAll(c => GetSuit(c) == suit && GetRankValue(c) < botHighRank);

                if (humanLower.Count == 0) continue;

                // Collect all bot cards of this suit that are strictly higher
                // than each human card we want to replace AND are not Ace/King
                // (rank < 13), sorted highest→lowest (best safe card first).
                // Only fall back to Ace/King if absolutely no other card exists.
                // Donor cards must not be 2, 3, King(13), or Ace(14) — humans must never hold those.
                List<string> botHigherCards = botHand
                    .FindAll(c => GetSuit(c) == suit
                               && GetRankValue(c) > GetRankValue(humanLower[0])
                               && GetRankValue(c) > 3     // exclude 2 and 3
                               && GetRankValue(c) < 13)   // exclude King(13) and Ace(14)
                    .OrderByDescending(c => GetRankValue(c))
                    .ToList();

                // Fallback: if no safe card exists, allow any higher card (edge case — bot hand limited)
                if (botHigherCards.Count == 0)
                {
                    botHigherCards = botHand
                        .FindAll(c => GetSuit(c) == suit && GetRankValue(c) > GetRankValue(humanLower[0]))
                        .OrderByDescending(c => GetRankValue(c))
                        .ToList();
                }

                // Re-sort human lower cards worst→best so we swap the weakest first
                humanLower.Sort((a, b) => GetRankValue(a).CompareTo(GetRankValue(b)));

                int swapCount = Mathf.Min(humanLower.Count, botHigherCards.Count);
                for (int i = 0; i < swapCount; i++)
                {
                    string humanCard = humanLower[i];
                    string botCard   = botHigherCards[i];

                    // Safety: skip if bot card is not actually higher than human card
                    if (GetRankValue(botCard) <= GetRankValue(humanCard)) continue;

                    humanHand.Remove(humanCard);
                    botHand.Remove(botCard);
                    humanHand.Add(botCard);
                    botHand.Add(humanCard);

                    Debug.Log($"[Balance P2] {humanSlot.id} [{humanCard}] ↔ bot {nextBotId} [{botCard}]");
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  PASS 3 : Ensure each human has at least 2 cards of every suit
        //
        //  After Passes 1 and 2, some humans may still hold fewer than 2
        //  cards of a particular suit (e.g. only 1 Spade, or 0 Diamonds).
        //  This pass tops them up to 2 by pulling cards from any available
        //  bot hand.
        //
        //  Allowed donor cards depend on table composition:
        //    2 humans + 2 bots  →  A and K allowed in human hand, but NOT 2 or 3
        //    3 humans + 1 bot   →  A and K allowed in human hand, but NOT 2 or 3
        //    default (1 human)  →  A, K, 2, 3 all banned from human hand
        //
        //  Donor search: scan ALL bots, pick the highest available card of
        //  the needed suit within the allowed rank range. Any bot can donate.
        //
        //  Example (2 humans + 2 bots):
        //    Human has: AS KH 7D 9C 5C
        //    Suit counts: S=1 H=1 D=1 C=2
        //    Need 1 more Spade → bots scanned → best is QS (rank 12, allowed) → swap
        //    Need 1 more Heart → bots scanned → best is JH (rank 11, allowed) → swap
        //    Need 1 more Diamond → bots scanned → best is 10D → swap
        //    Clubs already ≥ 2 → skip
        // ══════════════════════════════════════════════════════════════════

        Debug.Log("[Balance] ── Pass 3: ensure each human has ≥2 cards per suit ──");

        // Determine if A and K are allowed in human hands for this session
        // (true when 2+ humans at table; false in default 1-human config)
        bool akAllowedInHumanHand = humanCount >= 2;

        for (int seatIdx = 0; seatIdx < seatCount; seatIdx++)
        {
            SlotData humanSlot = _seatedPlayers[seatIdx];
            if (humanSlot.isBot) continue;
            if (!_gs.hands.ContainsKey(humanSlot.id)) continue;

            List<string> humanHand = _gs.hands[humanSlot.id];

            foreach (string suit in new[] { "S", "H", "D", "C" })
            {
                int suitCount = humanHand.Count(c => GetSuit(c) == suit);
                if (suitCount >= 2) continue;   // already has 2+ of this suit

                int needed = 2 - suitCount;

                for (int n = 0; n < needed; n++)
                {
                    // Find the best donor card from any bot
                    // Best = highest rank of this suit within allowed range
                    string bestCard   = null;
                    string bestBotId  = null;
                    int    bestRank   = -1;

                    foreach (SlotData slot in _seatedPlayers)
                    {
                        if (!slot.isBot) continue;
                        if (!_gs.hands.ContainsKey(slot.id)) continue;

                        foreach (string c in _gs.hands[slot.id])
                        {
                            if (GetSuit(c) != suit) continue;
                            int rk = GetRankValue(c);

                            // Never give 2 or 3 to human under any circumstance
                            if (rk == 2 || rk == 3) continue;

                            // A (14) and K (13) only allowed when akAllowedInHumanHand
                            if (!akAllowedInHumanHand && (rk == 13 || rk == 14)) continue;

                            if (rk > bestRank)
                            {
                                bestRank  = rk;
                                bestCard  = c;
                                bestBotId = slot.id;
                            }
                        }
                    }

                    if (bestCard == null)
                    {
                        Debug.Log($"[Balance P3] No valid donor card of {suit} for human {humanSlot.id} — skipping.");
                        break;
                    }

                    // Find a card from human hand to swap back to bot
                    // Prefer: lowest card of any other suit (keep the suit we just got)
                    // Fallback: lowest card overall
                    string swapBack = humanHand
                        .Where(c => GetSuit(c) != suit)
                        .OrderBy(c => GetRankValue(c))
                        .FirstOrDefault()
                        ?? humanHand.OrderBy(c => GetRankValue(c)).FirstOrDefault();

                    if (swapBack == null)
                    {
                        Debug.Log($"[Balance P3] Human {humanSlot.id} has no card to swap back — skipping.");
                        break;
                    }

                    humanHand.Remove(swapBack);
                    _gs.hands[bestBotId].Remove(bestCard);
                    humanHand.Add(bestCard);
                    _gs.hands[bestBotId].Add(swapBack);

                    Debug.Log($"[Balance P3] Human {humanSlot.id} [{swapBack}] ↔ bot {bestBotId} [{bestCard}] (needed {suit} ×{needed})");
                }
            }
        }
    }

    /// <summary>
    /// Starting from the seat AFTER <paramref name="fromSeatIndex"/> and
    /// walking forwards (clockwise / wrapping), returns the first bot player id found.
    /// Returns empty string if no bot exists at the table.
    /// </summary>
    private string FindNextBotId(int fromSeatIndex)
    {
        int seatCount = _seatedPlayers.Count;
        for (int offset = 1; offset < seatCount; offset++)
        {
            int idx = (fromSeatIndex + offset) % seatCount;
            SlotData slot = _seatedPlayers[idx];
            if (slot.isBot && _gs.hands.ContainsKey(slot.id) && _gs.hands[slot.id].Count > 0)
                return slot.id;
        }
        return "";
    }

    /// <summary>
    /// Starting from the seat BEFORE <paramref name="fromSeatIndex"/> and
    /// walking backwards (wrapping), returns the first bot player id found.
    /// Returns empty string if no bot exists at the table.
    /// </summary>
    private string FindPreviousBotId(int fromSeatIndex)
    {
        int seatCount = _seatedPlayers.Count;
        for (int offset = 1; offset < seatCount; offset++)
        {
            int idx = (fromSeatIndex - offset + seatCount) % seatCount;
            SlotData slot = _seatedPlayers[idx];
            if (slot.isBot && _gs.hands.ContainsKey(slot.id) && _gs.hands[slot.id].Count > 0)
                return slot.id;
        }
        return "";
    }

    /// <summary>
    /// Returns the highest-ranked card of <paramref name="suit"/> from the list
    /// that is NOT an Ace or King (rank >= 13). Those are too valuable to donate to humans.
    /// Falls back to the true highest only if every card of that suit is an Ace/King.
    /// Returns "" if the hand has no card of that suit at all.
    /// </summary>
    private string GetHighestCardOfSuit(List<string> hand, string suit)
    {
        string bestSafe = "";
        string bestAny  = "";
        int    bestSafeRk = -1;
        int    bestAnyRk  = -1;
        foreach (var c in hand)
        {
            if (GetSuit(c) != suit) continue;
            int rk = GetRankValue(c);
            if (rk > bestAnyRk) { bestAnyRk = rk; bestAny = c; }
            // Skip 2, 3, King(13), Ace(14) — humans must never receive these
            if (rk <= 3 || rk >= 13) continue;
            if (rk > bestSafeRk) { bestSafeRk = rk; bestSafe = c; }
        }
        return !string.IsNullOrEmpty(bestSafe) ? bestSafe : bestAny;
    }

    /// <summary>
    /// Returns the highest-ranked card from any suit in the list
    /// that is NOT an Ace or King (rank >= 13).
    /// Falls back to the true highest only if all cards are Aces/Kings.
    /// Returns "" if the hand is empty.
    /// </summary>
    private string GetHighestCardOfAny(List<string> hand)
    {
        string bestSafe = "";
        string bestAny  = "";
        int    bestSafeRk = -1;
        int    bestAnyRk  = -1;
        foreach (var c in hand)
        {
            int rk = GetRankValue(c);
            if (rk > bestAnyRk) { bestAnyRk = rk; bestAny = c; }
            // Skip 2, 3, King(13), Ace(14) — humans must never receive these
            if (rk <= 3 || rk >= 13) continue;
            if (rk > bestSafeRk) { bestSafeRk = rk; bestSafe = c; }
        }
        return !string.IsNullOrEmpty(bestSafe) ? bestSafe : bestAny;
    }

    private string LabelOf(string pid)
    {
        if (string.IsNullOrEmpty(pid)) return "?";

        if (_seatedPlayers != null && _seatedPlayers.Count > 0)
        {
            int botCount = 0;
            int humanCount = 0;
            foreach (var slot in _seatedPlayers)
            {
                if (slot.isBot) botCount++;
                else humanCount++;

                if (slot.id == pid)
                {
                    int seatBot = 0;
                    int seatHuman = 0;
                    foreach (var s2 in _seatedPlayers)
                    {
                        if (s2.id == pid) break;
                        if (s2.isBot) seatBot++;
                        else seatHuman++;
                    }

                    return slot.isBot
                        ? $"Bot{seatBot + 1}"
                        : (humanCount == 1 ? "Player" : $"Player{seatHuman + 1}");
                }
            }
        }

        if (pid.StartsWith("BOT_")) return pid.Replace("BOT_", "Bot");
        return pid.Length > 6 ? pid.Substring(0, 6) : pid;
    }

    private void CheckForNewWinners()
    {
        var toRemove = new List<string>();
        foreach (var pid in _gs.activePlayers)
            if (_gs.hands.ContainsKey(pid) && _gs.hands[pid].Count == 0)
            {
                if (!_gs.winners.Contains(pid))
                {
                    _gs.winners.Add(pid);
                    Debug.Log($"[GameManager] {pid} won!");
                }

                toRemove.Add(pid);
            }

        foreach (var pid in toRemove) _gs.activePlayers.Remove(pid);
        AdjustCurrentIndexAfterRemoval();
    }

    private bool CheckGameOver()
    {
        if (_gs.activePlayers.Count > 1) return false;
        if (_gs.activePlayers.Count == 1)
        {
            _gs.bhabhi = _gs.activePlayers[0];
            _gs.activePlayers.Clear();
            Debug.Log($"[GameManager] Bhabhi: {_gs.bhabhi}");
        }
        else
        {
            if (_gs.winners.Count > 0) _gs.bhabhi = _gs.winners[_gs.winners.Count - 1];
        }

        _gs.phase = GameState.PhaseFinished;
        WriteGameState(() =>
        {
            EventManager.FireGameStateUpdated(_gs);
            HandleGameFinished();
        });
        return true;
    }

    private void CheckGameOverWithBhabhi(string forcedBhabhi)
    {
        _gs.bhabhi = string.IsNullOrEmpty(forcedBhabhi) && _gs.winners.Count > 0
            ? _gs.winners[_gs.winners.Count - 1]
            : forcedBhabhi;
        _gs.activePlayers.Clear();
        Debug.Log($"[GameManager] All ran out. Bhabhi: {_gs.bhabhi}");
        _gs.phase = GameState.PhaseFinished;
        WriteGameState(() =>
        {
            EventManager.FireGameStateUpdated(_gs);
            HandleGameFinished();
        });
    }

    private void HandleGameFinished()
    {
        _gameActive = false;
        _isExecutingMove = false;
        _lastProcessedTurnKey = -1;
        _subRoundIndex = 0;
        if (_isHost) EventManager.FireAwardGameCoinsRequested(_gs.winners, Mathf.RoundToInt(_room.entryFee * 1.25f));
        EventManager.FireGameFinished(_gs.winners, _gs.bhabhi);
        DumpGameLog();
    }

    private IEnumerator BotMoveCoroutine(string botId, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (_gs?.CurrentPlayerId != botId || !_gameActive) yield break;
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

    private bool IsBot(string id) => id != null && id.StartsWith("BOT_");

    private string GetSecondHighestSuitCardPlayer(string excludeId, string leadSuit, List<PlayedCard> snapshot)
    {
        string bestId = "";
        int bestRank = -1;
        foreach (var pc in snapshot)
        {
            if (pc.playerId == excludeId || GetSuit(pc.card) != leadSuit) continue;
            int rank = GetRankValue(pc.card);
            if (rank > bestRank)
            {
                bestRank = rank;
                bestId = pc.playerId;
            }
        }

        return (!string.IsNullOrEmpty(bestId) && _gs.activePlayers.Contains(bestId)) ? bestId : "";
    }

    private string GetSuit(string code) => string.IsNullOrEmpty(code) ? "" : code[code.Length - 1].ToString();

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

    private string GetNextActivePlayerLeft(string fromId)
    {
        if (_gs.activePlayers.Count <= 1) return "";
        var allIds = new List<string>();
        foreach (var p in _room.players) allIds.Add(p.id);
        int fromIdx = allIds.IndexOf(fromId);
        if (fromIdx < 0) return "";
        for (int i = 1; i < allIds.Count; i++)
        {
            string candidate = allIds[(fromIdx + i) % allIds.Count];
            if (_gs.activePlayers.Contains(candidate) && candidate != fromId) return candidate;
        }

        return "";
    }

    private async Task<bool> WriteGameStateAsync()
    {
        if (_room == null) return true;
        try
        {
            await FirebaseManager.DB
                .Collection(RoomsCollection)
                .Document(_room.roomId)
                .UpdateAsync(new Dictionary<string, object> { { "gameState", _gs.ToDictionary() } });
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[GameManager] WriteGameStateAsync failed: {ex.Message}");
            return false;
        }
    }

    private void WriteGameState(Action onComplete = null)
    {
        if (_room == null)
        {
            onComplete?.Invoke();
            return;
        }

        _ = WriteGameStateAndInvokeAsync(onComplete);
    }

    private async Task WriteGameStateAndInvokeAsync(Action onComplete)
    {
        bool ok = await WriteGameStateAsync();
        if (!ok) Debug.LogWarning("[GameManager] WriteGameState: write failed, invoking callback anyway.");
        onComplete?.Invoke();
    }

    private async Task WriteGameStateAndProcessAsync()
    {
        _isExecutingMove = false;

        bool ok = await WriteGameStateAsync();
        if (!ok)
            Debug.LogWarning("[GameManager] WriteGameStateAndProcess: write failed.");

        EventManager.FireGameStateUpdated(_gs);

        if (_isHost && _gameActive)
            ProcessCurrentTurn();
    }

    private void WriteGameStateAndProcess() => _ = WriteGameStateAndProcessAsync();

    private void HandleLeaveGame()
    {
        _gameActive = false;
        _isExecutingMove = false;
        _lastProcessedTurnKey = -1;
        _subRoundIndex = 0;
    }
}