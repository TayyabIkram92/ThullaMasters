using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using Firebase.Extensions;
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
        _myHand.Clear();
        foreach (var c in localHand) _myHand.Add(c.ShortCode);
        Debug.Log($"[GameManager] HandleGameReady. isHost={_isHost} hand={_myHand.Count} room={_room.roomId}");
        if (_isHost) InitialiseGameState();
        else StartListening();
    }

    private RoomData GetCurrentRoom() => InGameManager.Instance?.CurrentRoom;

    private void InitialiseGameState()
    {
        _room = GetCurrentRoom();
        if (_room == null) { Debug.LogError("[GameManager] InitialiseGameState: Room is null."); return; }
        if (_room.hands.Count == 0) { Debug.LogError("[GameManager] InitialiseGameState: no hands."); return; }

        _gs = new GameState();
        _gs.phase = GameState.PhasePlaying;
        _gs.roundNumber = 1;
        foreach (var p in _room.players) _gs.activePlayers.Add(p.id);
        foreach (var kv in _room.hands) _gs.hands[kv.Key] = new List<string>(kv.Value);

        int startIndex = 0;
        for (int i = 0; i < _gs.activePlayers.Count; i++)
        {
            string pid = _gs.activePlayers[i];
            if (_gs.hands.ContainsKey(pid) && _gs.hands[pid].Contains("AS")) { startIndex = i; break; }
        }
        _gs.currentPlayerIndex = startIndex;
        _gs.leadSuit = "";
        _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        WriteGameState(() =>
        {
            Debug.Log($"[GameManager] GameState written. First={startIndex} ({_gs.CurrentPlayerId})");
            _gameActive = true;
            StartListening();
            EventManager.FireGameStateUpdated(_gs);
            ProcessCurrentTurn();
        });
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
            float botDelay = UnityEngine.Random.Range(2f, 15f);
            float effectiveDelay = Mathf.Min(botDelay, remaining - 1f);
            if (effectiveDelay < 1f) effectiveDelay = 1f;
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
        ExecuteMove(playerId, ChooseAutoCard(playerId, hand));
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

        bool roundComplete = _gs.cardsInPlay.Count >= _gs.activePlayers.Count;

        if (roundComplete)
        {
            StopResolveCoroutine();
            ShowCardsAndDelay(() => ResolveRound());
        }
        else
        {
            bool outOfSuit = GetSuit(cardCode) != _gs.leadSuit;
            if (outOfSuit && _gs.roundNumber > 1)
            {
                StopResolveCoroutine();
                ShowCardsAndDelay(() => ResolveOutOfSuit());
            }
            else
            {
                AdvanceToNextPlayer();
                _gs.turnStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                WriteGameStateAndProcess();
            }
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

    private void WriteGameState(Action onComplete = null)
    {
        if (_room == null) { onComplete?.Invoke(); return; }
        FirebaseManager.DB.Collection(RoomsCollection).Document(_room.roomId)
            .UpdateAsync(new Dictionary<string, object> { { "gameState", _gs.ToDictionary() } })
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted) Debug.LogError($"[GameManager] Write failed: {task.Exception}");
                else onComplete?.Invoke();
            });
    }

    private void WriteGameStateAndProcess()
    {
        _isExecutingMove = false;
        EventManager.FireGameStateUpdated(_gs);
        WriteGameState(() => { if (_isHost) ProcessCurrentTurn(); });
    }

    private void HandleLeaveGame()
    {
        _gameActive = false; _isExecutingMove = false; _lastProcessedTurnKey = -1;
        StopAllCoroutines(); StopListener();
    }
}
