using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using DG.Tweening;

public class InGameView : MonoBehaviour
{
    [System.Serializable]
    public class ProfileSlot
    {
        public Image avatarImage;
        public Text nameText;
        public Text remainingCardsText;
        public Image playedCardImage;
        public Image timerImage;
        public Button stealButton;
        public Text resultText;

        [Tooltip(
            "Debug only: parent Transform where this player's cards are shown when isTesting is true. Leave empty for the local player (seat 0).")]
        public Transform testHandParent;
    }

    [Header("Profiles (0=local, 1-3 clockwise)")] [SerializeField]
    private ProfileSlot[] profiles = new ProfileSlot[4];

    [Header("Cards")] [SerializeField] private Transform cardContainer;
    [SerializeField] private GameObject cardPrefab;
    [SerializeField] private Sprite[] cardSprites = new Sprite[52];

    [Header("Shoot-out")] [SerializeField] private Transform flippedCardsContainer;
    [SerializeField] private GameObject flippedCardPrefab;
    [SerializeField] private Sprite cardBackSprite;

    [Header("Buttons")] [SerializeField] private Button sortButton;
    [SerializeField] private Button leaveButton;

    [Header("Avatar Sprites (0-15)")] [SerializeField]
    private Sprite[] avatarSprites = new Sprite[16];

    [Header("Debug — Testing Mode")]
    [Tooltip("When true, shows all opponents' cards face-up inside each profile's testHandParent.")]
    public bool isTesting = false;

    // Spawned debug card objects per seat (index 0-3, seat 0 unused / local player)
    private readonly List<GameObject>[] _debugCards =
    {
        new List<GameObject>(), new List<GameObject>(),
        new List<GameObject>(), new List<GameObject>()
    };

    private static readonly int[] SuitSortOrder = { 2, 3, 0, 1 };
    private static readonly int[] RankSortPriority = { 0, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };

    private List<SlotData> _seatedPlayers = new List<SlotData>();
    private List<GameObject> _spawnedCards = new List<GameObject>();
    private List<GameObject> _flippedCards = new List<GameObject>();
    private GameState _gs;
    private Coroutine _timerCoroutine;
    private bool _autoSort;

    private void Awake()
    {
        if (sortButton != null) sortButton.onClick.AddListener(OnSortClicked);
        if (leaveButton != null) leaveButton.onClick.AddListener(OnLeaveClicked);
        for (int i = 0; i < profiles.Length; i++)
        {
            if (profiles[i]?.stealButton != null)
            {
                int idx = i;
                profiles[i].stealButton.onClick.AddListener(() => OnStealClicked(idx));
            }
        }
    }

    private void OnEnable()
    {
        ResetView();
        EventManager.OnGameReady += HandleGameReady;
        EventManager.OnGameStateUpdated += HandleGameStateUpdated;
        EventManager.OnLocalHandUpdated += HandleLocalHandUpdated;
    }

    private void OnDisable()
    {
        EventManager.OnGameReady -= HandleGameReady;
        EventManager.OnGameStateUpdated -= HandleGameStateUpdated;
        EventManager.OnLocalHandUpdated -= HandleLocalHandUpdated;
        StopTimer();
        ResetView();
    }

    private void OnDestroy()
    {
        if (sortButton != null) sortButton.onClick.RemoveAllListeners();
        if (leaveButton != null) leaveButton.onClick.RemoveAllListeners();
        foreach (var p in profiles)
            if (p?.stealButton != null)
                p.stealButton.onClick.RemoveAllListeners();
    }

    private void HandleGameReady(List<CardData> localHand, List<SlotData> seatedPlayers)
    {
        _seatedPlayers = seatedPlayers;
        _gs = null;
        _autoSort = false;
        PopulateProfiles(seatedPlayers);
        SpawnCards(localHand);
        HideAllPlayedCards();
        HideAllTimers();
        HideFlippedCards();
    }

    /// <summary>
    /// Clears all visual state: destroys spawned cards, empties profile names,
    /// hides result labels and steal buttons.
    /// Called on OnEnable (before a game starts) and OnDisable (cleanup).
    /// Safe to call at any time — HandleGameReady repopulates everything fresh
    /// immediately after OnEnable, so there is no visual flicker or logic gap.
    /// </summary>
    private void ResetView()
    {
        // Destroy all local player card GameObjects
        foreach (var go in _spawnedCards)
            if (go != null) Destroy(go);
        _spawnedCards.Clear();

        // Destroy all flipped (shootout) cards
        HideFlippedCards();

        // Destroy all debug hand cards
        for (int slot = 0; slot < _debugCards.Length; slot++)
        {
            foreach (var go in _debugCards[slot])
                if (go != null) Destroy(go);
            _debugCards[slot].Clear();
        }

        // Reset every profile slot to empty visual state
        foreach (var p in profiles)
        {
            if (p == null) continue;
            if (p.nameText            != null) p.nameText.text = "";
            if (p.remainingCardsText  != null) p.remainingCardsText.text = "";
            if (p.avatarImage         != null) p.avatarImage.sprite = null;
            if (p.playedCardImage     != null) p.playedCardImage.gameObject.SetActive(false);
            if (p.timerImage          != null) p.timerImage.gameObject.SetActive(false);
            if (p.stealButton         != null) p.stealButton.gameObject.SetActive(false);
            if (p.resultText          != null)
            {
                p.resultText.text = "";
                p.resultText.gameObject.SetActive(false);
            }
        }

        // Clear runtime state
        _seatedPlayers.Clear();
        _gs    = null;
        _autoSort = false;
        StopTimer();
    }

    private void PopulateProfiles(List<SlotData> seatedPlayers)
    {
        for (int i = 0; i < profiles.Length; i++)
        {
            var slot = profiles[i];
            if (slot == null) continue;
            bool hasPlayer = i < seatedPlayers.Count;
            if (hasPlayer)
            {
                var p = seatedPlayers[i];
                if (slot.avatarImage != null && p.avatarIndex >= 0 && p.avatarIndex < avatarSprites.Length &&
                    avatarSprites[p.avatarIndex] != null)
                    slot.avatarImage.sprite = avatarSprites[p.avatarIndex];
                if (slot.nameText != null) slot.nameText.text = p.displayName;
                if (slot.remainingCardsText != null) slot.remainingCardsText.text = "13";
            }

            if (slot.stealButton != null) slot.stealButton.gameObject.SetActive(false);
            if (slot.resultText != null) slot.resultText.gameObject.SetActive(false);
            if (slot.playedCardImage != null) slot.playedCardImage.gameObject.SetActive(false);
            if (slot.timerImage != null) slot.timerImage.gameObject.SetActive(false);
        }
    }

    private void HandleGameStateUpdated(GameState gs)
    {
        _gs = gs;
        RefreshRemainingCounts();
        RefreshPlayedCards();
        RefreshCardInteractability();
        RefreshStealButtons();
        RefreshTimerBar();
        if (gs.phase == GameState.PhaseShootout) RefreshShootoutView();
        else HideFlippedCards();
        if (gs.phase == GameState.PhaseFinished) ShowResults();
        RefreshDebugHands();
    }

    private void RefreshRemainingCounts()
    {
        if (_gs == null) return;
        for (int i = 0; i < profiles.Length && i < _seatedPlayers.Count; i++)
        {
            if (profiles[i]?.remainingCardsText == null) continue;
            string pid = _seatedPlayers[i].id;
            int count = _gs.hands.ContainsKey(pid) ? _gs.hands[pid].Count : 0;
            profiles[i].remainingCardsText.text = count.ToString();
        }
    }

    private void RefreshPlayedCards()
    {
        if (_gs == null) return;
        HideAllPlayedCards();
        foreach (var pc in _gs.cardsInPlay)
        {
            int seat = SeatIndexOf(pc.playerId);
            if (seat < 0 || seat >= profiles.Length) continue;
            if (profiles[seat]?.playedCardImage == null) continue;
            CardData card = CardData.FromShortCode(pc.card);
            if (card == null) continue;
            Sprite sp = GetCardSprite(card);
            if (sp == null) continue;
            profiles[seat].playedCardImage.sprite = sp;
            profiles[seat].playedCardImage.gameObject.SetActive(true);
        }
    }

    private void HideAllPlayedCards()
    {
        foreach (var p in profiles)
            if (p?.playedCardImage != null)
                p.playedCardImage.gameObject.SetActive(false);
    }

    private void RefreshCardInteractability()
    {
        if (_gs == null)
        {
            SetAllCardsInteractable(false);
            return;
        }

        string localId = PlayerDataManager.PlayFabId;
        bool isMyTurn = _gs.CurrentPlayerId == localId && _gs.phase == GameState.PhasePlaying;

        if (!isMyTurn)
        {
            SetAllCardsInteractable(false);
            return;
        }

        // BUG 2 FIX: local player already played a card this round (visible in cardsInPlay).
        // During the 3-second display delay, currentPlayerIndex still points at this player,
        // so isMyTurn is true — but they must not be able to play another card.
        // Disable all cards until the round resolves and a new turn begins.
        foreach (var pc in _gs.cardsInPlay)
        {
            if (pc.playerId == localId)
            {
                SetAllCardsInteractable(false);
                return;
            }
        }

        // Round 1: special suit restriction
        if (_gs.roundNumber == 1)
        {
            bool isRound1Leader = _gs.cardsInPlay.Count == 0;
            if (isRound1Leader)
            {
                foreach (var go in _spawnedCards)
                {
                    if (go == null) continue;
                    var btn = go.GetComponent<Button>();
                    if (btn != null) btn.interactable = (go.name == "AS");
                }
            }
            else
            {
                bool hasSpade = false;
                foreach (var go in _spawnedCards)
                    if (go != null && GetSuit(go.name) == "S")
                    {
                        hasSpade = true;
                        break;
                    }

                foreach (var go in _spawnedCards)
                {
                    if (go == null) continue;
                    var btn = go.GetComponent<Button>();
                    if (btn == null) continue;
                    btn.interactable = hasSpade ? (GetSuit(go.name) == "S") : true;
                }
            }

            return;
        }

        // Normal rounds
        string leadSuit = _gs.leadSuit;
        bool isLeading = string.IsNullOrEmpty(leadSuit);
        bool hasLeadSuit = false;
        if (!isLeading)
        {
            foreach (var go in _spawnedCards)
                if (go != null && GetSuit(go.name) == leadSuit)
                {
                    hasLeadSuit = true;
                    break;
                }
        }

        foreach (var go in _spawnedCards)
        {
            if (go == null) continue;
            var btn = go.GetComponent<Button>();
            if (btn == null) continue;
            bool canPlay;
            if (isLeading) canPlay = true;
            else if (!hasLeadSuit) canPlay = true;
            else canPlay = GetSuit(go.name) == leadSuit;
            btn.interactable = canPlay;
        }
    }

    private void SetAllCardsInteractable(bool value)
    {
        foreach (var go in _spawnedCards)
        {
            if (go == null) continue;
            var btn = go.GetComponent<Button>();
            if (btn != null) btn.interactable = value;
        }
    }

    private void RefreshTimerBar()
    {
        StopTimer();
        if (_gs == null || _gs.phase == GameState.PhaseFinished)
        {
            HideAllTimers();
            return;
        }

        string currentId = _gs.CurrentPlayerId;
        int activeSeat = SeatIndexOf(currentId);
        HideAllTimers();
        if (activeSeat < 0 || activeSeat >= profiles.Length) return;
        if (profiles[activeSeat]?.timerImage == null) return;
        profiles[activeSeat].timerImage.gameObject.SetActive(true);
        int totalSecs = _gs.phase == GameState.PhaseShootout ? GameState.ShootoutSeconds : GameState.TurnSeconds;
        float remaining = _gs.SecondsRemaining(totalSecs);
        _timerCoroutine = StartCoroutine(TimerBarCoroutine(profiles[activeSeat].timerImage, remaining, totalSecs));
    }

    private IEnumerator TimerBarCoroutine(Image img, float remaining, float total)
    {
        float elapsed = total - remaining;
        while (elapsed < total)
        {
            if (img != null) img.fillAmount = 1f - (elapsed / total);
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (img != null) img.fillAmount = 0f;
    }

    private void StopTimer()
    {
        if (_timerCoroutine != null)
        {
            StopCoroutine(_timerCoroutine);
            _timerCoroutine = null;
        }
    }

    private void HideAllTimers()
    {
        foreach (var p in profiles)
            if (p?.timerImage != null)
                p.timerImage.gameObject.SetActive(false);
    }

    private void RefreshStealButtons()
    {
        if (_gs == null)
        {
            HideAllStealButtons();
            return;
        }

        string localId = PlayerDataManager.PlayFabId;
        bool isMyTurn = _gs.CurrentPlayerId == localId;
        bool isLeading = isMyTurn && string.IsNullOrEmpty(_gs.leadSuit) && _gs.cardsInPlay.Count == 0;
        for (int i = 0; i < profiles.Length; i++)
        {
            if (profiles[i]?.stealButton == null) continue;
            bool show = false;
            if (i == 0 && isLeading && _gs.activePlayers.Count > 2)
            {
                string leftId = GetLeftActivePlayerId();
                show = !string.IsNullOrEmpty(leftId);
            }

            profiles[i].stealButton.gameObject.SetActive(show);
        }
    }

    private void HideAllStealButtons()
    {
        foreach (var p in profiles)
            if (p?.stealButton != null)
                p.stealButton.gameObject.SetActive(false);
    }

    private string GetLeftActivePlayerId()
    {
        if (_gs == null || _seatedPlayers.Count < 2) return "";
        for (int i = 1; i < _seatedPlayers.Count; i++)
        {
            string pid = _seatedPlayers[i].id;
            if (_gs.activePlayers.Contains(pid)) return pid;
        }

        return "";
    }

    private void SpawnCards(List<CardData> hand)
    {
        foreach (var go in _spawnedCards)
            if (go != null)
                Destroy(go);

        _spawnedCards.Clear();

        if (cardPrefab == null || cardContainer == null)
        {
            Debug.LogError("[InGameView] cardPrefab/cardContainer null.");
            return;
        }

        int index = 0;

        foreach (var card in hand)
        {
            GameObject go = Instantiate(cardPrefab, cardContainer);
            go.name = card.ShortCode;

            var img = go.GetComponent<Image>();
            if (img != null)
            {
                Sprite sp = GetCardSprite(card);
                if (sp != null) img.sprite = sp;
            }

            var btn = go.GetComponent<Button>();
            if (btn != null)
            {
                string code = card.ShortCode;
                btn.onClick.AddListener(() => OnCardClicked(code));
                btn.interactable = false;
            }

            // DOTween animation
            go.transform.localScale = Vector3.zero;

            float delay = 2f + (index * 0.25f);

            go.transform
                .DOScale(1f, 0.5f)
                .SetEase(Ease.OutBack)
                .SetDelay(delay);

            _spawnedCards.Add(go);

            index++;
        }

        if (_autoSort) SortCards();
    }

    private void RemoveCardFromHand(string cardCode)
    {
        for (int i = _spawnedCards.Count - 1; i >= 0; i--)
            if (_spawnedCards[i] != null && _spawnedCards[i].name == cardCode)
            {
                Destroy(_spawnedCards[i]);
                _spawnedCards.RemoveAt(i);
                return;
            }
    }

    private void HandleLocalHandUpdated(List<string> handCodes, GameState gs)
    {
        if (gs != null) _gs = gs;
        var hand = new List<CardData>();
        foreach (var code in handCodes)
        {
            var cd = CardData.FromShortCode(code);
            if (cd != null) hand.Add(cd);
        }

        SpawnCards(hand);
        RefreshCardInteractability();
    }

    private void RefreshShootoutView()
    {
        if (_gs == null) return;
        string drawerId = _gs.shootoutDrawerId;
        string responderId = "";
        foreach (var pid in _gs.activePlayers)
            if (pid != drawerId)
            {
                responderId = pid;
                break;
            }

        if (string.IsNullOrEmpty(responderId)) return;
        bool isDrawer = drawerId == PlayerDataManager.PlayFabId;
        if (!isDrawer)
        {
            HideFlippedCards();
            return;
        }

        int cardCount = _gs.hands.ContainsKey(responderId) ? _gs.hands[responderId].Count : 0;
        SpawnFlippedCards(cardCount);
    }

    private void SpawnFlippedCards(int count)
    {
        HideFlippedCards();
        if (flippedCardPrefab == null || flippedCardsContainer == null) return;
        flippedCardsContainer.gameObject.SetActive(true);
        for (int i = 0; i < count; i++)
        {
            GameObject go = Instantiate(flippedCardPrefab, flippedCardsContainer);
            go.name = $"Flipped_{i}";
            var img = go.GetComponent<Image>();
            if (img != null && cardBackSprite != null) img.sprite = cardBackSprite;
            var btn = go.GetComponent<Button>();
            if (btn != null) btn.onClick.AddListener(OnFlippedCardClicked);
            _flippedCards.Add(go);
        }
    }

    private void HideFlippedCards()
    {
        foreach (var go in _flippedCards)
            if (go != null)
                Destroy(go);
        _flippedCards.Clear();
        if (flippedCardsContainer != null) flippedCardsContainer.gameObject.SetActive(false);
    }

    // ── DEBUG TESTING MODE ────────────────────────────────────────────────────
    // When isTesting is true, renders all 3 opponents' hands face-up into the
    // three debug parent Transforms so the developer can see every card in play.
    // Cards are sorted the same way as the local hand (suit → rank).
    // Rebuilds completely on every GameState update so it always reflects reality.

    private void RefreshDebugHands()
    {
        // Clear all existing debug cards immediately so sibling indices are clean
        for (int slot = 0; slot < _debugCards.Length; slot++)
        {
            foreach (var go in _debugCards[slot])
                if (go != null)
                    DestroyImmediate(go);
            _debugCards[slot].Clear();
        }

        if (!isTesting || _gs == null || cardPrefab == null) return;

        // Seats 1, 2, 3 are the opponents — seat 0 is the local player, skip it
        for (int seatIndex = 1; seatIndex < profiles.Length; seatIndex++)
        {
            if (profiles[seatIndex] == null) continue;

            Transform parent = profiles[seatIndex].testHandParent;
            if (parent == null) continue;

            if (seatIndex >= _seatedPlayers.Count) continue;

            string pid = _seatedPlayers[seatIndex].id;
            if (!_gs.hands.ContainsKey(pid)) continue;

            var hand = _gs.hands[pid];
            if (hand == null || hand.Count == 0) continue;

            // Sort: suit first (S→H→D→C), then rank high→low within each suit
            var sortable = new List<(string code, int key)>();
            foreach (var code in hand)
            {
                var card = CardData.FromShortCode(code);
                if (card == null)
                {
                    sortable.Add((code, int.MaxValue));
                    continue;
                }

                int sp = System.Array.IndexOf(SuitSortOrder, card.suitIndex);
                if (sp < 0) sp = 4;
                int rp = RankSortPriority[card.rankIndex];
                sortable.Add((code, sp * 13 + rp));
            }

            sortable.Sort((a, b) => a.key.CompareTo(b.key));

            // Spawn cards in sorted order with explicit sibling index
            int siblingIdx = 0;
            int index = 0;

            foreach (var (code, _) in sortable)
            {
                var card = CardData.FromShortCode(code);
                if (card == null) continue;

                GameObject go = Instantiate(cardPrefab, parent);
                go.name = $"Debug_{code}";
                go.transform.SetSiblingIndex(siblingIdx++);

                var img = go.GetComponent<Image>();
                if (img != null)
                {
                    Sprite sp = GetCardSprite(card);
                    if (sp != null) img.sprite = sp;
                }

                // Display only — no interaction
                var btn = go.GetComponent<Button>();
                if (btn != null)
                {
                    btn.interactable = false;
                    btn.onClick.RemoveAllListeners();
                }

                // DOTween animation
                go.transform.localScale = Vector3.zero;

                float delay = 2f + (index * 0.25f);

                go.transform
                    .DOScale(1f, 0.5f)
                    .SetEase(Ease.OutBack)
                    .SetDelay(delay);

                _debugCards[seatIndex].Add(go);

                index++;
            }
        }
    }

    private void ShowResults()
    {
        if (_gs == null) return;
        HideAllStealButtons();
        SetAllCardsInteractable(false);
        for (int i = 0; i < profiles.Length && i < _seatedPlayers.Count; i++)
        {
            if (profiles[i]?.resultText == null) continue;
            string pid = _seatedPlayers[i].id;
            string label = "";
            if (_gs.bhabhi == pid) label = "Bhabhi!";
            else if (_gs.winners.Contains(pid)) label = "Winner!";
            if (!string.IsNullOrEmpty(label))
            {
                profiles[i].resultText.text = label;
                profiles[i].resultText.gameObject.SetActive(true);
            }
        }
    }

    private void SortCards()
    {
        if (_spawnedCards.Count == 0) return;
        var sortable = new List<(GameObject go, int key)>();
        foreach (var go in _spawnedCards)
        {
            if (go == null) continue;
            CardData card = CardData.FromShortCode(go.name);
            if (card == null)
            {
                sortable.Add((go, int.MaxValue));
                continue;
            }

            int sp = System.Array.IndexOf(SuitSortOrder, card.suitIndex);
            if (sp < 0) sp = 4;
            int rp = RankSortPriority[card.rankIndex];
            sortable.Add((go, sp * 13 + rp));
        }

        sortable.Sort((a, b) => a.key.CompareTo(b.key));
        for (int i = 0; i < sortable.Count; i++) sortable[i].go.transform.SetSiblingIndex(i);
    }

    private void OnCardClicked(string cardCode)
    {
        EventManager.FireLocalCardPlayed(cardCode);
    }

    private void OnFlippedCardClicked()
    {
        EventManager.FireShootoutCardChosen();
        HideFlippedCards();
    }

    private void OnStealClicked(int profileIndex)
    {
        EventManager.FireStealHandRequested();
    }

    private void OnSortClicked()
    {
        _autoSort = true;
        SortCards();
    }

    private void OnLeaveClicked() => EventManager.FireLeaveGameRequested();

    private int SeatIndexOf(string playerId)
    {
        for (int i = 0; i < _seatedPlayers.Count; i++)
            if (_seatedPlayers[i].id == playerId)
                return i;
        return -1;
    }

    private string GetSuit(string code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        return code[code.Length - 1].ToString();
    }

    private Sprite GetCardSprite(CardData card)
    {
        if (card == null) return null;
        int idx = card.SpriteIndex;
        if (idx < 0 || idx >= cardSprites.Length) return null;
        return cardSprites[idx];
    }
}