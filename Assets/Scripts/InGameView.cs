using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// In-game UI. 52 cards pre-spawned (pool). First deal animates; subsequent updates swap sprites.
/// Shows WinView or LoseView 5 seconds after game ends.
/// Uses FireLeaveRoomRequested and correct ViewType names.
/// </summary>
public class InGameView : MonoBehaviour
{
    // ─── Inspector References ────────────────────────────────────────────────
    [Header("Player Slots (4, clockwise from local)")] [SerializeField]
    private InGameProfileSlot[] profileSlots;

    [Header("Hand")] [SerializeField] private Transform cardContainer;
    [SerializeField] private GameObject cardPrefab;

    [Header("Buttons")] [SerializeField] private Button sortButton;
    [SerializeField] private Button leaveButton;
    [SerializeField] private Button stealButton;

    [Header("Win/Lose (dialogue views, assign in Inspector)")] [SerializeField]
    private WinView winView;

    [SerializeField] private LoseView loseView;

    [Header("Card Sprites")] [SerializeField]
    private Sprite[] cardSprites; // 52 sprites, index = suitIndex*13+rankIndex

    [SerializeField] private Sprite cardBackSprite;

    // ─── Card Pool ───────────────────────────────────────────────────────────
    private const int CardPoolSize = 52;
    private readonly List<CardPoolItem> _cardPool = new List<CardPoolItem>(CardPoolSize);
    private readonly List<CardPoolItem> _handCards = new List<CardPoolItem>();

    // ─── State ───────────────────────────────────────────────────────────────
    private bool _isFirstDeal = true;
    private List<CardData> _currentHand = new List<CardData>();
    private List<SlotData> _seatedPlayers;
    private GameState _lastGameState;
    [Header("Testing")] private bool _isSortEnabled = false;

    [SerializeField] private bool isTesting = false;
    private Coroutine _timerCoroutine;

    // ─── Delay Constants ─────────────────────────────────────────────────────
    private const float DelayInstant = 0f;
    private const float DelayTurn = 2f;

    private void OnEnable()
    {
        sortButton.onClick.AddListener(OnSortClicked);
        leaveButton.onClick.AddListener(OnLeaveClicked);
        if (stealButton) stealButton.onClick.AddListener(OnStealClicked);

        EventManager.OnGameReady += HandleGameReady;
        EventManager.OnLocalHandUpdated += HandleLocalHandUpdated;
        EventManager.OnGameStateUpdated += HandleGameStateUpdated;
        EventManager.OnGameFinished += HandleGameFinished;

        InitCardPool();
    }

    private void OnDisable()
    {
        // ADD in OnDisable:
        if (_timerCoroutine != null)
        {
            StopCoroutine(_timerCoroutine);
            _timerCoroutine = null;
        }

        sortButton.onClick.RemoveListener(OnSortClicked);
        leaveButton.onClick.RemoveListener(OnLeaveClicked);
        if (stealButton) stealButton.onClick.RemoveListener(OnStealClicked);

        EventManager.OnGameReady -= HandleGameReady;
        EventManager.OnLocalHandUpdated -= HandleLocalHandUpdated;
        EventManager.OnGameStateUpdated -= HandleGameStateUpdated;
        EventManager.OnGameFinished -= HandleGameFinished;
    }

    // ─── Card Pool ───────────────────────────────────────────────────────────

    private void InitCardPool()
    {
        // Only create if not already created
        if (_cardPool.Count >= CardPoolSize) return;

        while (_cardPool.Count < CardPoolSize)
        {
            var go = Instantiate(cardPrefab, cardContainer);
            go.SetActive(false);
            _cardPool.Add(new CardPoolItem
            {
                go = go,
                img = go.GetComponent<Image>(),
                btn = go.GetComponent<Button>()
            });
        }
    }

    private CardPoolItem GetPooledCard()
    {
        foreach (var item in _cardPool)
            if (!item.go.activeSelf)
                return item;

        // Extend
        var newGo = Instantiate(cardPrefab, cardContainer);
        newGo.SetActive(false);
        var newItem = new CardPoolItem
            { go = newGo, img = newGo.GetComponent<Image>(), btn = newGo.GetComponent<Button>() };
        _cardPool.Add(newItem);
        return newItem;
    }

    private void ReturnAllHandCards()
    {
        foreach (var item in _handCards)
        {
            if (item.btn) item.btn.onClick.RemoveAllListeners();
            item.go.SetActive(false);
            item.card = null;
        }

        _handCards.Clear();
    }

    // ─── Game Ready ──────────────────────────────────────────────────────────

    private void HandleGameReady(List<CardData> hand, List<SlotData> players)
    {
        _seatedPlayers = players;
        _isFirstDeal = true;
        _currentHand = new List<CardData>(hand);

        SetupProfileSlots(players);
    }

    private void SetupProfileSlots(List<SlotData> players)
    {
        for (int i = 0; i < profileSlots.Length && i < players.Count; i++)
            profileSlots[i].Setup(players[i]);
    }

    // ─── Deal Animation (first time only) ────────────────────────────────────

    private IEnumerator DealAnimation(List<CardData> hand)
    {
        ReturnAllHandCards();
        float dealInterval = 0.12f;

        for (int i = 0; i < hand.Count; i++)
        {
            var item = GetPooledCard();
            item.card = hand[i];
            item.img.sprite = GetCardSprite(hand[i]);
            item.go.SetActive(true);
            item.go.transform.localScale = Vector3.zero;

            if (item.btn) item.btn.interactable = false;
            item.go.transform.DOScale(1f, 0.15f).SetEase(Ease.OutBack);
            BindCardButton(item);
            _handCards.Add(item);
            EventManager.FirePlaySound(SoundType.DealCard);
            yield return new WaitForSeconds(dealInterval);
        }

        _isFirstDeal = false;
        EventManager.FireCardDealAnimationComplete();
        RedrawHand(_currentHand);
        ApplyCardInteractability(_lastGameState, DelayInstant); // before first turn — instant
        if (isTesting) RefreshTestCards(_lastGameState);
    }

    private void RefreshTestCards(GameState gs)
    {
        if (!isTesting || _seatedPlayers == null) return;

        for (int i = 0; i < profileSlots.Length && i < _seatedPlayers.Count; i++)
        {
            string pid = _seatedPlayers[i].id;

            List<string> codes = null;
            if (gs != null && gs.hands != null)
                gs.hands.TryGetValue(pid, out codes);

            var sprites = new List<Sprite>();
            if (codes != null)
            {
                // Convert to CardData so we can sort
                var cards = new List<CardData>();
                foreach (var code in codes)
                {
                    var card = CardData.FromShortCode(code);
                    if (card != null) cards.Add(card);
                }

                // Apply same sort as local hand if enabled, otherwise default low→high
                if (_isSortEnabled)
                    SortHandHighToLow(cards);
                else
                    cards.Sort((a, b) =>
                    {
                        int s = SuitPriority(a.suitIndex).CompareTo(SuitPriority(b.suitIndex));
                        if (s != 0) return s;
                        int rankA = a.rankIndex == 0 ? 13 : a.rankIndex;
                        int rankB = b.rankIndex == 0 ? 13 : b.rankIndex;
                        return rankB.CompareTo(rankA);
                    });

                foreach (var card in cards)
                    sprites.Add(GetCardSprite(card));
            }

            profileSlots[i].SetTestCards(sprites);
        }
    }
    // ─── Hand Update ─────────────────────────────────────────────────────────

    // Signature matches EventManager.OnLocalHandUpdated: Action<List<string>, GameState>
    private void HandleLocalHandUpdated(List<string> handCodes, GameState gs)
    {
        var hand = new List<CardData>();
        foreach (var code in handCodes)
        {
            var card = CardData.FromShortCode(code);
            if (card != null) hand.Add(card);
        }

        if (_isSortEnabled) SortHandHighToLow(hand);
        _currentHand = hand;

        if (_isFirstDeal)
        {
            // Distribution phases are now complete — start the deal animation with the final hand
            StartCoroutine(DealAnimation(_currentHand));
            return;
        }


        RedrawHand(_currentHand);
        ApplyCardInteractability(gs, DelayInstant); // hand redrawn — instant
        if (isTesting) RefreshTestCards(gs); // ← ADD
    }

    private void BindCardButton(CardPoolItem item)
    {
        if (item.btn == null) return;
        var card = item.card;
        item.btn.onClick.RemoveAllListeners();
        item.btn.onClick.AddListener(() => OnCardClicked(card));
    }

    private void OnCardClicked(CardData card)
    {
        EventManager.FirePlaySound(SoundType.PlayCard);
        EventManager.FireLocalCardPlayed(card.ShortCode);
        ApplyCardInteractability(_lastGameState, DelayInstant); // user played — instant
    }

    // ─── Game State ──────────────────────────────────────────────────────────

    private void HandleGameStateUpdated(GameState state)
    {
        _lastGameState = state;
        UpdateStealButton(state);
        ApplyCardInteractability(state); // new turn — 2f default delay

        if (state == null) return;

        // ── Card counts ──────────────────────────────────────────────────────
        if (_seatedPlayers != null)
            for (int i = 0; i < profileSlots.Length && i < _seatedPlayers.Count; i++)
            {
                string pid = _seatedPlayers[i].id;
                int count = state.hands.ContainsKey(pid) ? state.hands[pid].Count : 0;
                profileSlots[i].SetCardCount(count);
            }

        // ── Turn highlight ───────────────────────────────────────────────────
        string currentId = state.CurrentPlayerId;
        for (int i = 0; i < profileSlots.Length; i++)
            profileSlots[i].SetActive(i == GetSlotIndex(currentId));

        // ── Timer coroutine ──────────────────────────────────────────────────
        if (_timerCoroutine != null)
        {
            StopCoroutine(_timerCoroutine);
            _timerCoroutine = null;
        }

        if (state.phase != GameState.PhaseFinished && !string.IsNullOrEmpty(currentId))
        {
            int slotIdx = GetSlotIndex(currentId);
            if (slotIdx >= 0 && slotIdx < profileSlots.Length)
            {
                float remaining = state.SecondsRemaining(GameState.TurnSeconds);
                _timerCoroutine = StartCoroutine(
                    TimerCoroutine(profileSlots[slotIdx], remaining, GameState.TurnSeconds));
            }
        }

        // ── Played cards ─────────────────────────────────────────────────────
        if (state.cardsInPlay == null || state.cardsInPlay.Count == 0)
        {
            foreach (var slot in profileSlots)
                slot.ClearPlayedCard();
            return;
        }

        foreach (var played in state.cardsInPlay)
        {
            int slotIdx = GetSlotIndex(played.playerId);
            if (slotIdx < 0 || slotIdx >= profileSlots.Length) continue;
            var card = CardData.FromShortCode(played.card);
            if (card != null)
                profileSlots[slotIdx].ShowPlayedCard(GetCardSprite(card));
        }

        if (isTesting) RefreshTestCards(state);
    }

    private IEnumerator TimerCoroutine(InGameProfileSlot slot, float remaining, float total)
    {
        float elapsed = total - remaining;
        while (elapsed < total)
        {
            slot.SetTimerValue(total - elapsed, total);
            elapsed += Time.deltaTime;
            yield return null;
        }

        slot.SetTimerValue(0f, total);
    }

    private void UpdateStealButton(GameState state)
    {
        if (stealButton == null) return;
        bool canSteal = state != null &&
                        state.phase == "playing" &&
                        state.leadSuit == "" &&
                        state.cardsInPlay != null && state.cardsInPlay.Count == 0 && // ADD: not mid-round
                        state.activePlayers != null &&
                        state.activePlayers.Count > 2 && // was == 3
                        state.activePlayers.Count > state.currentPlayerIndex &&
                        state.activePlayers[state.currentPlayerIndex] == PlayerDataManager.PlayFabId;
        stealButton.gameObject.SetActive(canSteal);
    }

    private void ApplyCardInteractability(GameState state, float delay = DelayTurn)
    {
        StartCoroutine(ApplyCardInteractabilityCoroutine(state, delay));
    }

    IEnumerator ApplyCardInteractabilityCoroutine(GameState state, float delay)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);

        // ── Not my turn — all locked ──────────────────────────────────────────
        if (state == null || state.activePlayers == null ||
            state.currentPlayerIndex < 0 ||
            state.currentPlayerIndex >= state.activePlayers.Count ||
            state.activePlayers[state.currentPlayerIndex] != PlayerDataManager.PlayFabId)
        {
            foreach (var item in _handCards)
                if (item.btn)
                    item.btn.interactable = false;
            yield break;
        }

        // ── already played this round — lock during resolve delay ──
        string localId = PlayerDataManager.PlayFabId;
        if (state.cardsInPlay != null)
            foreach (var pc in state.cardsInPlay)
                if (pc.playerId == localId)
                {
                    foreach (var item in _handCards)
                        if (item.btn)
                            item.btn.interactable = false;
                    yield break;
                }

        // ── First round special rules ─────────────────────────────────────────
        if (state.roundNumber == 1)
        {
            bool hasAceOfSpades = false;
            foreach (var item in _handCards)
                if (item.card != null && item.card.suitIndex == 0 && item.card.rankIndex == 0)
                {
                    hasAceOfSpades = true;
                    break;
                }

            if (hasAceOfSpades)
            {
                // Rule 6: only Ace of Spades interactable
                foreach (var item in _handCards)
                {
                    if (item.btn == null) continue;
                    item.btn.interactable = item.card != null
                                            && item.card.suitIndex == 0
                                            && item.card.rankIndex == 0;
                }
            }
            else
            {
                // Rule 7: only spade cards interactable
                foreach (var item in _handCards)
                {
                    if (item.btn == null) continue;
                    item.btn.interactable = item.card != null && item.card.suitIndex == 0;
                }
            }

            yield break;
        }

        // Leading (no suit led yet) — all cards playable
        if (string.IsNullOrEmpty(state.leadSuit))
        {
            foreach (var item in _handCards)
                if (item.btn)
                    item.btn.interactable = true;
            yield break;
        }

        // Map leadSuit string to suitIndex: S=0, H=1, C=2, D=3
        int leadSuitIndex;
        switch (state.leadSuit)
        {
            case "S": leadSuitIndex = 0; break;
            case "H": leadSuitIndex = 1; break;
            case "C": leadSuitIndex = 2; break;
            case "D": leadSuitIndex = 3; break;
            default:
                foreach (var item in _handCards)
                    if (item.btn)
                        item.btn.interactable = true;
                yield break;
        }

        // Following — check if player has any card of lead suit
        bool hasSuit = false;
        foreach (var item in _handCards)
            if (item.card != null && item.card.suitIndex == leadSuitIndex)
            {
                hasSuit = true;
                break;
            }

        // If has suit — only those cards interactable; otherwise all interactable
        foreach (var item in _handCards)
        {
            if (item.btn == null) continue;
            item.btn.interactable = !hasSuit || (item.card != null && item.card.suitIndex == leadSuitIndex);
        }
    }

    // ─── Game Finished ───────────────────────────────────────────────────────

    private void HandleGameFinished(List<string> winners, string bhabhi)
    {
        StartCoroutine(ShowResultAfterDelay(winners, bhabhi));
    }

    private IEnumerator ShowResultAfterDelay(List<string> winners, string bhabhi)
    {
        yield return new WaitForSeconds(5f);

        string localId = PlayerDataManager.PlayFabId;
        bool isWinner = winners != null && winners.Contains(localId);
        bool isBhabhi = bhabhi == localId;

        if (isWinner)
        {
            var room = InGameManager.Instance?.CurrentRoom;
            int prize = room != null ? Mathf.RoundToInt(room.entryFee * 1.25f) : 0;
            if (winView != null) winView.Setup(prize);
            EventManager.FireShowView(ViewType.Win, true);
        }
        else if (isBhabhi)
        {
            EventManager.FireShowView(ViewType.Lose, true);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private int GetSlotIndex(string playerId)
    {
        if (_seatedPlayers == null) return -1;
        for (int i = 0; i < _seatedPlayers.Count; i++)
            if (_seatedPlayers[i].id == playerId)
                return i;
        return -1;
    }

    private Sprite GetCardSprite(CardData card)
    {
        if (card == null || cardSprites == null) return cardBackSprite;
        int idx = card.SpriteIndex;
        return (idx >= 0 && idx < cardSprites.Length) ? cardSprites[idx] : cardBackSprite;
    }

    // ─── Buttons ─────────────────────────────────────────────────────────────

    private void OnSortClicked()
    {
        if (_currentHand == null) return;
        _isSortEnabled = true;
        SortHandHighToLow(_currentHand);
        RedrawHand(_currentHand);
        ApplyCardInteractability(_lastGameState, DelayInstant); // sort — instant
    }

    private void SortHandHighToLow(List<CardData> hand)
    {
        hand.Sort((a, b) =>
        {
            int suitPriorityA = SuitPriority(a.suitIndex);
            int suitPriorityB = SuitPriority(b.suitIndex);
            int s = suitPriorityA.CompareTo(suitPriorityB); // suit group first
            if (s != 0) return s;
            int rankA = a.rankIndex == 0 ? 13 : a.rankIndex; // Ace = 13
            int rankB = b.rankIndex == 0 ? 13 : b.rankIndex;
            return rankB.CompareTo(rankA); // high to low within suit
        });
    }

    private int SuitPriority(int suitIndex)
    {
        switch (suitIndex)
        {
            case 2: return 0; // Clubs    — first
            case 3: return 1; // Diamonds — second
            case 0: return 2; // Spades   — third
            case 1: return 3; // Hearts   — fourth
            default: return 4;
        }
    }

    private void RedrawHand(List<CardData> hand)
    {
        ReturnAllHandCards();
        foreach (var card in hand)
        {
            var item = GetPooledCard();
            item.card = card;
            item.img.sprite = GetCardSprite(card);
            item.go.SetActive(true);
            item.go.transform.localScale = Vector3.one;
            BindCardButton(item);
            _handCards.Add(item);
        }
    }

    private void OnLeaveClicked()
    {
        EventManager.FireLeaveGameRequested(); // was FireLeaveRoomRequested
        EventManager.FireShowView(ViewType.Home);
    }

    private void OnStealClicked()
    {
        EventManager.FireStealHandRequested(); // was FireLocalCardPlayed("STEAL")
    }

    // ─── Inner types ─────────────────────────────────────────────────────────

    private class CardPoolItem
    {
        public GameObject go;
        public Image img;
        public Button btn;
        public CardData card;
    }
}