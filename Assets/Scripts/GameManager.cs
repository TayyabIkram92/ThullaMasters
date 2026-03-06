using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Firebase.Firestore;

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
    private Coroutine _turnTimerCoroutine;
    private Coroutine _botCoroutine;
    private Coroutine _resolveCoroutine;
    private ListenerRegistration _listener;
    private const float ResolveDelaySeconds = 3f;
    private List<string> _myHand = new List<string>();

    [Header("Bot Difficulty")]
    [Tooltip("True = bots use hard AI strategy. False = bots use basic auto-play.")]
    public bool useHardBot = false;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
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

    private void HandleGameReady(List<CardData> localHand, List<SlotData> seatedPlayers)
    {
        _room = GetCurrentRoom();
        if (_room == null) { Debug.LogError("[GameManager] HandleGameReady: CurrentRoom is null."); return; }
        _isHost = _room.hostId == PlayerDataManager.PlayFabId;
        _gameActive = false;
        _isExecutingMove = false;
        _lastProcessedTurnKey = -1;
        _gs = null;
        _voidTracker.Clear();
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
        if (_room == null) { Debug.LogError("[GameManager] InitialiseGameState: Room is null."); return; }
        if (_room.hands.Count == 0) { Debug.LogError("[GameManager] InitialiseGameState: no hands."); return; }

        _gs = new GameState();
        _gs.phase       = GameState.PhasePlaying;
        _gs.roundNumber = 1;
        foreach (var p in _room.players) _gs.activePlayers.Add(p.id);
        foreach (var kv in _room.hands)  _gs.hands[kv.Key] = new List<string>(kv.Value);

        int startIndex = 0;
        for (int i = 0; i < _gs.activePlayers.Count; i++)
        {
            string pid = _gs.activePlayers[i];
            if (_gs.hands.ContainsKey(pid) && _gs.hands[pid].Contains("AS")) { startIndex = i; break; }
        }

        _gs.currentPlayerIndex = startIndex;
        _gs.leadSuit           = "";
        _gs.turnStartTime      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Await the write: non-host listeners fire AFTER this confirms.
        // Guarantees all clients receive a confirmed gameState before round 1.
        bool ok = await WriteGameStateAsync();
        if (!ok)
        {
            Debug.LogError("[GameManager] InitialiseGameState: write failed — aborting.");
            EventManager.FireShowPopUp("Network error starting game. Please try again.");
            return;
        }

        Debug.Log($"[GameManager] GameState confirmed. First player index={startIndex} ({_gs.CurrentPlayerId})");

        _gameActive = true;
        StartListening();                       // start listener AFTER write confirmed
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

                // ── BUG 1 FIX: use content comparison, not count ──────────────────
                // Count-based checks miss the thulla pickup case: the pickup player B
                // played a card this round (−1) then receives all cards in play (+N).
                // If Firestore snapshots coalesce, or if _myHand was already updated
                // by ExecuteMove before the snapshot arrives, the count delta can be
                // exactly zero even though the hand contents are completely different.
                // Comparing sorted card lists catches every change unconditionally.
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

    private void StopListener() { _listener?.Stop(); _listener = null; }

    /// <summary>
    /// Returns true if both lists contain exactly the same cards (order-independent).
    /// Used by the Firestore listener to detect ANY hand change, including the thulla
    /// pickup case where a player loses 1 card and gains N in the same Firestore write.
    /// Count-based checks miss this: the net delta can be zero even with different cards.
    /// </summary>
    private bool HandsMatchSorted(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        var sa = new List<string>(a); sa.Sort();
        var sb = new List<string>(b); sb.Sort();
        for (int i = 0; i < sa.Count; i++)
            if (sa[i] != sb[i]) return false;
        return true;
    }

    private void ProcessCurrentTurn()
    {
        StopTurnTimer();
        StopBotCoroutine();
        if (_gs == null || !_gameActive) return;
        if (_gs.phase == GameState.PhaseShootout) { ProcessShootoutTurn(); return; }
        if (_gs.phase == GameState.PhaseFinished) return;

        string currentId = _gs.CurrentPlayerId;
        string localId = PlayerDataManager.PlayFabId;
        if (string.IsNullOrEmpty(currentId)) { Debug.LogWarning("[GameManager] ProcessCurrentTurn: empty."); return; }

        int turnKey = _gs.roundNumber * 1000 + _gs.currentPlayerIndex;
        if (turnKey == _lastProcessedTurnKey) { Debug.Log($"[GameManager] Skipping dup turn {turnKey}"); return; }
        _lastProcessedTurnKey = turnKey;
        _isExecutingMove = false;

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

    private void StopTurnTimer() { if (_turnTimerCoroutine != null) { StopCoroutine(_turnTimerCoroutine); _turnTimerCoroutine = null; } }

    private void AutoPlay(string playerId)
    {
        if (_gs == null || !_gs.hands.ContainsKey(playerId)) return;
        var hand = _gs.hands[playerId];
        if (hand.Count == 0) return;

        string card = useHardBot && IsBot(playerId)
            ? ChooseHardBotCard(playerId, hand)
            : ChooseAutoCard(playerId, hand);

        ExecuteMove(playerId, card);
    }


    // ═════════════════════════════════════════════════════════════════════════
    // ── HARD BOT AI  v3 ──────────────────────────────────────────────────────
    //
    // CORE UNDERSTANDING OF THULLA:
    //   When you LEAD a suit, if ANY player after you in this round has NO cards
    //   of that suit, they will play a thulla. The highest lead-suit card holder
    //   picks up ALL cards. If WE are the highest, WE pick up — that's bad.
    //
    //   Since the bot can see ALL hands, it knows EXACTLY who is void in what.
    //   This is the most powerful advantage: zero guessing.
    //
    // LEAD STRATEGY (priority order):
    //   1. SAFE DISCARD: Lead a suit where EVERYONE still playing has that suit.
    //      This guarantees cards are discarded — no thulla possible.
    //      Among safe suits, lead the one where we hold the highest card (burn it).
    //      If we're not the highest, still safe — cards get discarded.
    //
    //   2. FORCE THULLA ON OTHERS (not us): Lead a suit where:
    //      - The player void in it has ALREADY played (they can't thulla us)
    //      - OR we are NOT the highest card holder in that suit
    //        (if thulla happens, someone else picks up, not us)
    //      This dumps our card AND punishes someone else.
    //
    //   3. LAST RESORT: We have no completely safe suit. Pick the suit where
    //      we are NOT the highest card holder. If a thulla happens, someone
    //      else picks up. If round goes clean, cards discarded. Either way,
    //      we don't pick up.
    //
    //   4. ABSOLUTE FALLBACK: Lead smallest suit to go void fastest.
    //      (Happens only when we hold the highest card in every suit — very rare.)
    //
    // FOLLOW STRATEGY:
    //   - If thulla is coming from any remaining player → burn a high card
    //     (we won't be the pickup target since we're following suit)
    //   - Round is clean → play highest to win lead + control
    //   - If we ARE the current highest AND thulla is coming → play second-highest
    //     (can't avoid being highest anyway if nobody else has higher, so burn it)
    //
    // THULLA STRATEGY:
    //   - Play HIGHEST card from SMALLEST suit (dump dangerous card + go void faster)
    //
    // ═════════════════════════════════════════════════════════════════════════

    // ── Void Tracker ──────────────────────────────────────────────────────────
    private readonly Dictionary<string, HashSet<string>> _voidTracker
        = new Dictionary<string, HashSet<string>>();

    private void UpdateVoidTracker(string playerId, string cardCode)
    {
        if (!useHardBot) return;
        if (string.IsNullOrEmpty(_gs.leadSuit)) return;
        if (GetSuit(cardCode) == _gs.leadSuit) return;

        if (!_voidTracker.ContainsKey(playerId))
            _voidTracker[playerId] = new HashSet<string>();
        _voidTracker[playerId].Add(_gs.leadSuit);
        Debug.Log($"[HardBot] VoidTracker: {playerId} confirmed void in {_gs.leadSuit}");
    }

    private bool IsConfirmedVoid(string playerId, string suit)
        => _voidTracker.ContainsKey(playerId) && _voidTracker[playerId].Contains(suit);

    /// <summary>
    /// Returns true if the given player has NO cards of the given suit.
    /// Uses full hand visibility (bot sees all hands). Void tracker as backup.
    /// </summary>
    private bool ActuallyVoidInSuit(string pid, string suit)
    {
        if (IsConfirmedVoid(pid, suit)) return true;
        if (!_gs.hands.ContainsKey(pid)) return false;
        return !_gs.hands[pid].Exists(c => GetSuit(c) == suit);
    }

    // ── Entry Point ───────────────────────────────────────────────────────────

    private string ChooseHardBotCard(string botId, List<string> hand)
    {
        // Round 1 special rules
        if (_gs.roundNumber == 1 && _gs.cardsInPlay.Count == 0 && hand.Contains("AS"))
            return "AS";
        if (_gs.roundNumber == 1 && _gs.cardsInPlay.Count > 0)
        {
            var spades1 = hand.FindAll(c => GetSuit(c) == "S");
            if (spades1.Count > 0) return MidHighOf(spades1);
            return HighestOf(hand);
        }

        bool isLeading = string.IsNullOrEmpty(_gs.leadSuit);
        return isLeading ? HardBotLead(botId, hand) : HardBotFollow(botId, hand);
    }

    // ── LEADING ───────────────────────────────────────────────────────────────
    //
    // THULLA PICKUP RULE (exact mechanics):
    //   - You lead suitX with card C.
    //   - The IMMEDIATE NEXT player is void in suitX → they thulla → round ends NOW.
    //   - YOU pick up all cards (you played C, they played their thulla card).
    //   - Your hand INCREASES. This is the worst outcome when leading.
    //
    // So when leading, the ONE thing that matters most is:
    //   Does the IMMEDIATE NEXT player have cards of the suit I want to lead?
    //   If NO → I will pick up my own card + their thulla card. NEVER lead that suit.
    //   If YES → the next player follows. Now if a LATER player is void and thullas,
    //            the highest lead-suit card holder picks up — could be anyone, not me
    //            (unless I played the highest, but that's a separate calculation).
    //
    // PRIORITY ORDER:
    //   1. Lead a suit where the NEXT player has that suit AND someone further
    //      down the chain is void → PERFECT: we dump card, someone else picks up.
    //   2. Lead a suit where EVERYONE has it → safe discard, all cards discarded.
    //      Among these, prefer the suit where we hold the highest card (burn A/K).
    //   3. Lead a suit where the NEXT player has it (no immediate thulla on us).
    //      Even if round goes fully clean, at least our card is discarded.
    //      Among these, prefer suits where a later player IS void
    //      (likely someone else picks up, not us).
    //   4. Absolute fallback: every suit we have, the next player is void.
    //      We MUST pick up no matter what. Minimize damage: lead suit with
    //      fewest cards (lose fewest extra cards to pickup).

    private string HardBotLead(string botId, List<string> hand)
    {
        var    bySuit     = GroupBySuit(hand);
        string nextPid    = GetNextActivePlayer(botId);

        // Categorise each suit we hold by what will happen if we lead it
        var safeAndForcing  = new List<string>(); // next has it + later someone void
        var safeAndClean    = new List<string>(); // everyone has it (guaranteed discard)
        var safeOnly        = new List<string>(); // next has it, rest unknown/clean
        var dangerSuits     = new List<string>(); // next is void → we pick up

        foreach (var suit in bySuit.Keys)
        {
            bool nextHasSuit = !string.IsNullOrEmpty(nextPid) && !ActuallyVoidInSuit(nextPid, suit);

            if (!nextHasSuit)
            {
                dangerSuits.Add(suit);
                continue;
            }

            // Next player has it. Check if any LATER player is void (forcing a pickup on them)
            bool laterVoidExists = false;
            foreach (var pid in _gs.activePlayers)
            {
                if (pid == botId || pid == nextPid) continue;
                if (ActuallyVoidInSuit(pid, suit)) { laterVoidExists = true; break; }
            }

            // Check if ALL players have it (fully clean round)
            bool allHaveIt = true;
            foreach (var pid in _gs.activePlayers)
            {
                if (pid == botId) continue;
                if (ActuallyVoidInSuit(pid, suit)) { allHaveIt = false; break; }
            }

            if (allHaveIt)
                safeAndClean.Add(suit);
            else if (laterVoidExists)
                safeAndForcing.Add(suit);  // next follows, but later someone picks up
            else
                safeOnly.Add(suit);        // next follows, rest also have it
        }

        // ── PRIORITY 1: Next has it + later player void ───────────────────────
        // Perfect: we dump a card, someone else down the chain picks up.
        // Among these, prefer the suit where we are NOT the highest card holder
        // (minimises chance we're the one who picks up when thulla hits).
        if (safeAndForcing.Count > 0)
        {
            string best = PickLeadSuitFromList(botId, safeAndForcing, bySuit, preferNotHighest: true);
            return PickLeadCard(bySuit[best]);
        }

        // ── PRIORITY 2: Fully clean round (everyone has the suit) ────────────
        // Guaranteed discard. Prefer suit where WE hold the highest card
        // → burn our most dangerous card cleanly.
        if (safeAndClean.Count > 0)
        {
            string best = PickLeadSuitFromList(botId, safeAndClean, bySuit, preferNotHighest: false);
            return PickLeadCard(bySuit[best]);
        }

        // ── PRIORITY 3: Next has it (safe from immediate thulla) ─────────────
        // No later void found, so round likely goes clean. Safe to dump.
        if (safeOnly.Count > 0)
        {
            string best = PickLeadSuitFromList(botId, safeOnly, bySuit, preferNotHighest: false);
            return PickLeadCard(bySuit[best]);
        }

        // ── PRIORITY 4: All suits are dangerous (next player void in all) ─────
        // We WILL pick up no matter what. Lead suit with fewest cards to minimise
        // total cards added to hand (fewer cards in play = fewer picked up).
        string fallback = SmallestSuit(new List<string>(bySuit.Keys), bySuit);
        return PickLeadCard(bySuit[fallback]);
    }

    /// <summary>
    /// From a list of candidate suits, picks the best one to lead.
    /// preferNotHighest=true: prefer suits where we do NOT hold the
    ///   highest card across all players (someone else is the pickup target
    ///   if a thulla happens later in the round).
    /// preferNotHighest=false: prefer suits where we DO hold the highest card
    ///   (guarantees we burn our most dangerous card in a clean round).
    /// In both cases, among equally ranked options, prefer the suit where our
    /// highest card has the highest rank (burn the most dangerous card first).
    /// </summary>
    private string PickLeadSuitFromList(string botId, List<string> suits,
                                         Dictionary<string, List<string>> bySuit,
                                         bool preferNotHighest)
    {
        // Score each suit: higher score = better choice
        string bestSuit  = suits[0];
        int    bestScore = int.MinValue;

        foreach (var suit in suits)
        {
            int ourHighRank = GetRankValue(HighestOf(bySuit[suit]));

            // Are we the highest card holder in this suit across all players?
            bool weAreHighest = true;
            foreach (var pid in _gs.activePlayers)
            {
                if (pid == botId) continue;
                if (!_gs.hands.ContainsKey(pid)) continue;
                if (_gs.hands[pid].Exists(c => GetSuit(c) == suit && GetRankValue(c) > ourHighRank))
                {
                    weAreHighest = false;
                    break;
                }
            }

            int score = 0;

            if (preferNotHighest)
            {
                // Reward NOT being highest (someone else picks up if thulla happens)
                if (!weAreHighest) score += 100;
                // Among equally ranked options, prefer burning a high card
                score += ourHighRank;
            }
            else
            {
                // Reward BEING highest (we control the round, burn our dangerous card)
                if (weAreHighest) score += 100;
                score += ourHighRank;
            }

            if (score > bestScore) { bestScore = score; bestSuit = suit; }
        }

        return bestSuit;
    }

    /// <summary>
    /// Picks the card to play when leading a chosen suit.
    /// - 1 card  → play it
    /// - 2 cards → play highest (burn it, save the low one for endgame)
    /// - 3+cards → play second-highest (burn a dangerous card, keep the very
    ///             highest for later control AND the lowest for endgame escape)
    /// </summary>
    private string PickLeadCard(List<string> suitCards)
    {
        if (suitCards.Count == 1) return suitCards[0];
        if (suitCards.Count == 2) return HighestOf(suitCards);
        return MidHighOf(suitCards); // second-highest
    }

    // ── FOLLOWING ─────────────────────────────────────────────────────────────

    private string HardBotFollow(string botId, List<string> hand)
    {
        string lead      = _gs.leadSuit;
        var    suitCards = hand.FindAll(c => GetSuit(c) == lead);

        if (suitCards.Count > 0)
        {
            // Will any remaining player (who hasn't played yet) thulla?
            bool thullaComingFromRemaining = WillAnyRemainingPlayerThulla(botId, lead);

            if (!thullaComingFromRemaining)
            {
                // Clean round — play HIGHEST. Burns our dangerous card, win lead if possible.
                return HighestOf(suitCards);
            }
            else
            {
                // Thulla is coming from someone. We're following suit so we won't
                // be the pickup target (only lead-suit holders pick up, and the
                // thulla-er ends the round — the highest lead-suit card holder picks up).
                // Just burn our HIGHEST card here — someone else is the pickup.
                // Exception: if WE currently hold the highest lead-suit card played
                // AND there's still a higher card we could avoid (play second-highest
                // hoping someone else plays higher, or just burn it anyway).
                string ourHighest        = HighestOf(suitCards);
                string currentPlayHigh   = GetCurrentHighestInPlay(lead);
                int    currentHighRank   = GetRankValue(currentPlayHigh);
                int    ourRank           = GetRankValue(ourHighest);

                if (ourRank > currentHighRank)
                {
                    // We would become the new highest → thulla picks us up
                    // Play SECOND highest if available — maybe someone after us
                    // will also play higher, taking lead away from us
                    if (suitCards.Count >= 2) return SecondHighestOf(suitCards);
                }
                // Burn highest — we're not currently the top, so we're safe
                return ourHighest;
            }
        }
        else
        {
            // No lead suit — must thulla. Play HIGHEST from SMALLEST suit.
            return HighestCardFromSmallestSuit(hand);
        }
    }

    /// <summary>
    /// Returns true if any active player who has NOT yet played this round
    /// is void in the lead suit. Uses full hand visibility.
    /// </summary>
    private bool WillAnyRemainingPlayerThulla(string botId, string leadSuit)
    {
        var playedIds = new HashSet<string>();
        foreach (var pc in _gs.cardsInPlay) playedIds.Add(pc.playerId);

        foreach (var pid in _gs.activePlayers)
        {
            if (pid == botId) continue;
            if (playedIds.Contains(pid)) continue; // already played this round
            if (ActuallyVoidInSuit(pid, leadSuit)) return true;
        }
        return false;
    }

    private string GetCurrentHighestInPlay(string leadSuit)
    {
        string best = ""; int bestRank = -1;
        foreach (var pc in _gs.cardsInPlay)
        {
            if (GetSuit(pc.card) != leadSuit) continue;
            int rank = GetRankValue(pc.card);
            if (rank > bestRank) { bestRank = rank; best = pc.card; }
        }
        return best;
    }

    private string GetCurrentWinnerId(string leadSuit)
    {
        string winner = ""; int bestRank = -1;
        foreach (var pc in _gs.cardsInPlay)
        {
            if (GetSuit(pc.card) != leadSuit) continue;
            int rank = GetRankValue(pc.card);
            if (rank > bestRank) { bestRank = rank; winner = pc.playerId; }
        }
        return winner;
    }

    /// <summary>
    /// When forced to thulla: play HIGHEST from our SMALLEST suit.
    /// Dumps most dangerous card + progresses toward void in that suit.
    /// </summary>
    private string HighestCardFromSmallestSuit(List<string> hand)
    {
        var bySuit = GroupBySuit(hand);
        string smallest = ""; int fewest = int.MaxValue;
        foreach (var kv in bySuit)
            if (kv.Value.Count < fewest) { fewest = kv.Value.Count; smallest = kv.Key; }
        return string.IsNullOrEmpty(smallest) ? HighestOf(hand) : HighestOf(bySuit[smallest]);
    }

    // ── SHARED HELPERS ────────────────────────────────────────────────────────

    private string GetNextActivePlayer(string botId)
    {
        int idx = _gs.activePlayers.IndexOf(botId);
        if (idx < 0 || _gs.activePlayers.Count <= 1) return "";
        return _gs.activePlayers[(idx + 1) % _gs.activePlayers.Count];
    }

    private Dictionary<string, List<string>> GroupBySuit(List<string> hand)
    {
        var dict = new Dictionary<string, List<string>>();
        foreach (var c in hand)
        {
            string s = GetSuit(c);
            if (!dict.ContainsKey(s)) dict[s] = new List<string>();
            dict[s].Add(c);
        }
        return dict;
    }

    private string HighestOf(List<string> cards)
    {
        string best = cards[0];
        foreach (var c in cards)
            if (GetRankValue(c) > GetRankValue(best)) best = c;
        return best;
    }

    private string SecondHighestOf(List<string> cards)
    {
        if (cards.Count <= 1) return cards[0];
        var sorted = new List<string>(cards);
        sorted.Sort((a, b) => GetRankValue(b).CompareTo(GetRankValue(a)));
        return sorted[1];
    }

    private string MidHighOf(List<string> cards)
    {
        if (cards.Count <= 2) return HighestOf(cards);
        var sorted = new List<string>(cards);
        sorted.Sort((a, b) => GetRankValue(b).CompareTo(GetRankValue(a)));
        return sorted[1]; // second-highest
    }

    private string LowestOf(List<string> cards)
    {
        string best = cards[0];
        foreach (var c in cards)
            if (GetRankValue(c) < GetRankValue(best)) best = c;
        return best;
    }

    private string SmallestSuit(List<string> suits, Dictionary<string, List<string>> bySuit)
    {
        string best = suits[0];
        foreach (var s in suits)
            if (bySuit[s].Count < bySuit[best].Count) best = s;
        return best;
    }

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
        if (_gs.CurrentPlayerId != localId) { Debug.LogWarning($"[GameManager] Not my turn. Current={_gs.CurrentPlayerId}"); return; }
        if (_isExecutingMove) { Debug.LogWarning("[GameManager] Move in-flight."); return; }
        if (!_myHand.Contains(cardCode)) { Debug.LogWarning($"[GameManager] Card {cardCode} not in hand."); return; }
        if (_gs.roundNumber == 1 && _gs.cardsInPlay.Count == 0 && cardCode != "AS") { Debug.LogWarning("[GameManager] Must play AS."); return; }
        if (_gs.roundNumber == 1 && _gs.cardsInPlay.Count > 0)
        {
            bool hasSpade = _myHand.Exists(c => GetSuit(c) == "S");
            if (hasSpade && GetSuit(cardCode) != "S") { Debug.LogWarning("[GameManager] Must play spade."); return; }
        }
        if (_gs.roundNumber > 1 && !string.IsNullOrEmpty(_gs.leadSuit))
        {
            bool hasLeadSuit = _myHand.Exists(c => GetSuit(c) == _gs.leadSuit);
            if (hasLeadSuit && GetSuit(cardCode) != _gs.leadSuit) { Debug.LogWarning($"[GameManager] Must follow {_gs.leadSuit}."); return; }
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
        string leftId = GetNextActivePlayerLeft(localId);
        if (string.IsNullOrEmpty(leftId)) return;
        if (!_gs.hands.ContainsKey(leftId) || _gs.hands[leftId].Count == 0) return;
        var stolen = new List<string>(_gs.hands[leftId]);
        _gs.hands[leftId].Clear();
        _gs.hands[localId].AddRange(stolen);
        _myHand = new List<string>(_gs.hands[localId]);
        EventManager.FireLocalHandUpdated(new List<string>(_myHand), _gs);
        if (!_gs.winners.Contains(leftId)) _gs.winners.Add(leftId);
        _gs.activePlayers.Remove(leftId);
        AdjustCurrentIndexAfterRemoval();
        _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WriteGameState(() => { Debug.Log($"[GameManager] Stole from {leftId}."); EventManager.FireGameStateUpdated(_gs); });
    }

    private void ProcessShootoutTurn()
    {
        string localId = PlayerDataManager.PlayFabId;
        float remaining = _gs.SecondsRemaining(GameState.ShootoutSeconds);
        _turnTimerCoroutine = StartCoroutine(ShootoutTimerCoroutine(remaining));
        if (_gs.shootoutDrawerId == localId) Debug.Log("[GameManager] Shootout: I am the drawer.");
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
        if (drawerId == PlayerDataManager.PlayFabId) ExecuteShootoutDraw(drawerId);
        else if (_isHost && IsBot(drawerId)) ExecuteShootoutDraw(drawerId);
    }

    private void HandleShootoutCardChosen()
    {
        if (_gs?.phase != GameState.PhaseShootout) return;
        if (_gs.shootoutDrawerId != PlayerDataManager.PlayFabId) return;
        StopTurnTimer();
        ExecuteShootoutDraw(PlayerDataManager.PlayFabId);
    }

    private void ExecuteShootoutDraw(string drawerId)
    {
        string responderId = "";
        foreach (var pid in _gs.activePlayers) if (pid != drawerId) { responderId = pid; break; }
        if (string.IsNullOrEmpty(responderId)) return;
        if (!_gs.hands.ContainsKey(responderId) || _gs.hands[responderId].Count == 0) return;
        var responderHand = _gs.hands[responderId];
        int idx = UnityEngine.Random.Range(0, responderHand.Count);
        string drawnCard = responderHand[idx];
        responderHand.RemoveAt(idx);
        if (!_gs.hands.ContainsKey(drawerId)) _gs.hands[drawerId] = new List<string>();
        _gs.hands[drawerId].Add(drawnCard);
        if (drawerId == PlayerDataManager.PlayFabId)
        {
            _myHand = new List<string>(_gs.hands[drawerId]);
            EventManager.FireLocalHandUpdated(new List<string>(_myHand), _gs);
        }
        _gs.phase = GameState.PhasePlaying;
        _gs.leadSuit = "";
        _gs.cardsInPlay.Clear();
        _gs.currentPlayerIndex = _gs.activePlayers.IndexOf(drawerId);
        _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _gs.shootoutDrawerId = "";
        ExecuteMove(drawerId, drawnCard);
    }

    private IEnumerator BotShootoutDrawCoroutine(string botId, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (_gs?.phase == GameState.PhaseShootout && _gs.shootoutDrawerId == botId) ExecuteShootoutDraw(botId);
    }

    private void ExecuteMove(string playerId, string cardCode)
    {
        if (_gs == null) return;
        if (!_gs.hands.ContainsKey(playerId)) return;
        if (!_gs.hands[playerId].Contains(cardCode))
        {
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

        // BUG 2 FIX: Fire FireLocalHandUpdated AFTER adding card to cardsInPlay.
        // Previously this fired BEFORE cardsInPlay.Add(), so RefreshCardInteractability
        // saw an empty cardsInPlay and re-enabled all cards (player appeared to be leading).
        // Now cardsInPlay contains the played card, so the "already played" check works.
        if (playerId == PlayerDataManager.PlayFabId)
            EventManager.FireLocalHandUpdated(new List<string>(_myHand), _gs);

        Debug.Log($"[GameManager] {playerId} played {cardCode}. In play: {_gs.cardsInPlay.Count}");

        // Update void tracker — if card is out of lead suit, player is confirmed void
        UpdateVoidTracker(playerId, cardCode);

        bool roundComplete = _gs.cardsInPlay.Count >= _gs.activePlayers.Count;
        bool outOfSuit     = GetSuit(cardCode) != _gs.leadSuit;

        // THULLA CHECK MUST COME BEFORE roundComplete CHECK.
        // When the last player (D) plays out of suit, both conditions are true:
        //   roundComplete = true  (all 4 players have played)
        //   outOfSuit     = true  (D played a different suit)
        // The OLD code checked roundComplete first → called ResolveRound() which
        // DISCARDS all cards. The lead player (B, highest H) never received them.
        // FIX: check thulla first. A thulla by the last player is still a thulla —
        // the round is NOT cleanly resolved; the lead player picks up everything.
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
            if (rank > highestRank) { highestRank = rank; winnerId = pc.playerId; }
        }
        Debug.Log($"[GameManager] Round {_gs.roundNumber} done. Winner={winnerId}");
        var playedSnapshot = new List<PlayedCard>(_gs.cardsInPlay);
        _gs.cardsInPlay.Clear();
        _gs.leadSuit = "";
        _gs.roundNumber++;
        CheckForNewWinners();
        if (_gs.activePlayers.Count == 0) { CheckGameOverWithBhabhi(winnerId); return; }
        if (!string.IsNullOrEmpty(winnerId) && !_gs.activePlayers.Contains(winnerId))
        {
            string nextLeaderId = GetSecondHighestSuitCardPlayer(winnerId, roundLeadSuit, playedSnapshot);
            if (!string.IsNullOrEmpty(nextLeaderId)) winnerId = nextLeaderId;
            else if (_gs.activePlayers.Count > 0) winnerId = _gs.activePlayers[0];
        }
        if (CheckShootout()) return;
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
            if (rank > highestRank) { highestRank = rank; pickupId = pc.playerId; }
        }
        Debug.Log($"[GameManager] Thulla! {pickupId} picks up {_gs.cardsInPlay.Count} cards.");
        if (!string.IsNullOrEmpty(pickupId) && _gs.hands.ContainsKey(pickupId))
            foreach (var pc in _gs.cardsInPlay) _gs.hands[pickupId].Add(pc.card);
        _gs.cardsInPlay.Clear();
        _gs.leadSuit = "";
        CheckForNewWinners();
        if (!string.IsNullOrEmpty(pickupId) && _gs.activePlayers.Contains(pickupId))
            _gs.currentPlayerIndex = _gs.activePlayers.IndexOf(pickupId);
        if (pickupId == PlayerDataManager.PlayFabId)
        {
            _myHand = new List<string>(_gs.hands[pickupId]);
            EventManager.FireLocalHandUpdated(new List<string>(_myHand), _gs);
        }
        if (CheckShootout()) return;
        if (CheckGameOver()) return;
        _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WriteGameStateAndProcess();
    }

    private void CheckForNewWinners()
    {
        var toRemove = new List<string>();
        foreach (var pid in _gs.activePlayers)
            if (_gs.hands.ContainsKey(pid) && _gs.hands[pid].Count == 0)
            {
                if (!_gs.winners.Contains(pid)) { _gs.winners.Add(pid); Debug.Log($"[GameManager] {pid} won!"); }
                toRemove.Add(pid);
            }
        foreach (var pid in toRemove) _gs.activePlayers.Remove(pid);
        AdjustCurrentIndexAfterRemoval();
    }

    private bool CheckGameOver()
    {
        if (_gs.activePlayers.Count > 1) return false;
        if (_gs.activePlayers.Count == 1) { _gs.bhabhi = _gs.activePlayers[0]; _gs.activePlayers.Clear(); Debug.Log($"[GameManager] Bhabhi: {_gs.bhabhi}"); }
        else { if (_gs.winners.Count > 0) _gs.bhabhi = _gs.winners[_gs.winners.Count - 1]; }
        _gs.phase = GameState.PhaseFinished;
        WriteGameState(() => { EventManager.FireGameStateUpdated(_gs); HandleGameFinished(); });
        return true;
    }

    private void CheckGameOverWithBhabhi(string forcedBhabhi)
    {
        _gs.bhabhi = string.IsNullOrEmpty(forcedBhabhi) && _gs.winners.Count > 0 ? _gs.winners[_gs.winners.Count - 1] : forcedBhabhi;
        _gs.activePlayers.Clear();
        Debug.Log($"[GameManager] All ran out. Bhabhi: {_gs.bhabhi}");
        _gs.phase = GameState.PhaseFinished;
        WriteGameState(() => { EventManager.FireGameStateUpdated(_gs); HandleGameFinished(); });
    }

    private bool CheckShootout()
    {
        if (_gs.activePlayers.Count != 2) return false;
        string zeroCardId = "";
        foreach (var pid in _gs.activePlayers)
            if (_gs.hands.ContainsKey(pid) && _gs.hands[pid].Count == 0) { zeroCardId = pid; break; }
        if (string.IsNullOrEmpty(zeroCardId)) return false;
        _gs.phase = GameState.PhaseShootout;
        _gs.shootoutDrawerId = zeroCardId;
        _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Debug.Log($"[GameManager] Shootout! Drawer: {zeroCardId}");
        WriteGameStateAndProcess();
        return true;
    }

    private void HandleGameFinished()
    {
        _gameActive = false; _isExecutingMove = false; _lastProcessedTurnKey = -1;
        StopTurnTimer(); StopBotCoroutine(); StopResolveCoroutine(); StopListener();
        if (_isHost) EventManager.FireAwardGameCoinsRequested(_gs.winners, Mathf.RoundToInt(_room.entryFee * 1.25f));
        EventManager.FireGameFinished(_gs.winners, _gs.bhabhi);
    }

    private IEnumerator BotMoveCoroutine(string botId, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (_gs?.CurrentPlayerId != botId || !_gameActive) yield break;
        AutoPlay(botId);
    }

    private void StopBotCoroutine() { if (_botCoroutine != null) { StopCoroutine(_botCoroutine); _botCoroutine = null; } }
    private void StopResolveCoroutine() { if (_resolveCoroutine != null) { StopCoroutine(_resolveCoroutine); _resolveCoroutine = null; } }
    private bool IsBot(string id) => id != null && id.StartsWith("BOT_");

    private string GetSecondHighestSuitCardPlayer(string excludeId, string leadSuit, List<PlayedCard> snapshot)
    {
        string bestId = ""; int bestRank = -1;
        foreach (var pc in snapshot)
        {
            if (pc.playerId == excludeId || GetSuit(pc.card) != leadSuit) continue;
            int rank = GetRankValue(pc.card);
            if (rank > bestRank) { bestRank = rank; bestId = pc.playerId; }
        }
        return (!string.IsNullOrEmpty(bestId) && _gs.activePlayers.Contains(bestId)) ? bestId : "";
    }

    private string GetSuit(string code) => string.IsNullOrEmpty(code) ? "" : code[code.Length - 1].ToString();

    private int GetRankValue(string code)
    {
        if (string.IsNullOrEmpty(code)) return 0;
        string rankStr = code.Substring(0, code.Length - 1);
        switch (rankStr) { case "A": return 14; case "K": return 13; case "Q": return 12; case "J": return 11; default: return int.TryParse(rankStr, out int v) ? v : 0; }
    }

    private void AdvanceToNextPlayer() { if (_gs.activePlayers.Count == 0) return; _gs.currentPlayerIndex = (_gs.currentPlayerIndex + 1) % _gs.activePlayers.Count; }

    private void AdjustCurrentIndexAfterRemoval()
    {
        if (_gs.activePlayers.Count == 0) { _gs.currentPlayerIndex = 0; return; }
        _gs.currentPlayerIndex = _gs.currentPlayerIndex % _gs.activePlayers.Count;
    }

    private string GetPlayerIdAt(int index) { if (_gs.activePlayers.Count == 0) return ""; return _gs.activePlayers[index % _gs.activePlayers.Count]; }

    private string GetNextActivePlayerLeft(string fromId)
    {
        if (_gs.activePlayers.Count <= 1) return "";
        var allIds = new List<string>(); foreach (var p in _room.players) allIds.Add(p.id);
        int fromIdx = allIds.IndexOf(fromId); if (fromIdx < 0) return "";
        for (int i = 1; i < allIds.Count; i++)
        {
            string candidate = allIds[(fromIdx + i) % allIds.Count];
            if (_gs.activePlayers.Contains(candidate) && candidate != fromId) return candidate;
        }
        return "";
    }

    // ── Firestore Async Write ─────────────────────────────────────────────────

    /// <summary>
    /// Awaitable Firestore write. All game state changes go through here.
    /// Returns true on success, false on failure.
    /// Callers await this before advancing game logic — no fire-and-forget.
    /// </summary>
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
        catch (System.Exception ex)
        {
            Debug.LogError($"[GameManager] WriteGameStateAsync failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Legacy sync wrapper kept for ShowCardsAndDelay which uses a coroutine.
    /// Awaits the Firestore write then invokes onComplete on the main thread.
    /// </summary>
    private void WriteGameState(Action onComplete = null)
    {
        if (_room == null) { onComplete?.Invoke(); return; }
        _ = WriteGameStateAndInvokeAsync(onComplete);
    }

    private async Task WriteGameStateAndInvokeAsync(Action onComplete)
    {
        bool ok = await WriteGameStateAsync();
        if (!ok) Debug.LogWarning("[GameManager] WriteGameState: write failed, invoking callback anyway.");
        onComplete?.Invoke();
    }

    /// <summary>
    /// Writes game state to Firestore (awaited), THEN fires FireGameStateUpdated,
    /// THEN calls ProcessCurrentTurn on host.
    ///
    /// Ordering guarantee: remote devices' Firestore listeners fire AFTER the write
    /// confirms. By also firing the local event after the await, local and remote
    /// devices process the new state in the same logical order — eliminating the
    /// window where local state is ahead of what Firestore has confirmed.
    /// </summary>
    private async Task WriteGameStateAndProcessAsync()
    {
        _isExecutingMove = false;

        bool ok = await WriteGameStateAsync();
        if (!ok)
            Debug.LogWarning("[GameManager] WriteGameStateAndProcess: write failed.");

        // Fire local event AFTER write confirms — local device now matches Firestore
        EventManager.FireGameStateUpdated(_gs);

        if (_isHost && _gameActive)
            ProcessCurrentTurn();
    }

    // Keep a non-async entry point so existing call sites that can't easily be
    // made async (inside coroutines, event handlers) still work cleanly.
    private void WriteGameStateAndProcess() => _ = WriteGameStateAndProcessAsync();

    private void HandleLeaveGame()
    {
        _gameActive = false; _isExecutingMove = false; _lastProcessedTurnKey = -1;
        StopAllCoroutines(); StopListener();
    }
}
