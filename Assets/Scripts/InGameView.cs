using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// InGame UI controller. Reacts to GameState updates and local player input.
///
/// ── Inspector Wiring ──────────────────────────────────────────────────────────
///
/// PROFILES (0=local, 1-3 clockwise):
///   Each ProfileSlot:
///     avatarImage        → AvatarBG/Avatar (Image)
///     nameText           → NameTxt (Text)
///     remainingCardsText → RemainingCardTxt (Text)
///     playedCardImage    → child Image showing card played this round
///     timerImage         → ImageSliderFillAmountBased (Image, fillAmount 1→0)
///                          *** hide GO by default, shown only on active player ***
///     stealButton        → StealBtn (Button) — hidden by default
///     resultText         → ResultTxt (Text)  — hidden by default
///
/// CARDS:
///   cardContainer  → MyCards (GridLayoutGroup)
///   cardPrefab     → Card prefab (Image + Button on root)
///   cardSprites[52]→ AS→KS, AH→KH, AC→KC, AD→KD
///
/// SHOOT-OUT:
///   flippedCardsContainer → FlippedCards GO
///   flippedCardPrefab     → FlippedCard prefab (Image + Button)
///   cardBackSprite        → back-of-card sprite
///
/// BUTTONS:
///   sortButton  → Sort button
///   leaveButton → LeaveMatch button
///
/// AVATAR SPRITES:
///   avatarSprites[16]
/// </summary>
public class InGameView : MonoBehaviour
{
    // ── Nested ────────────────────────────────────────────────────────────────

    [System.Serializable]
    public class ProfileSlot
    {
        public Image  avatarImage;
        public Text   nameText;
        public Text   remainingCardsText;
        public Image  playedCardImage;   // shows card played this round
        public Image  timerImage;        // ImageSliderFillAmountBased — hidden by default
        public Button stealButton;       // hidden by default
        public Text   resultText;        // hidden by default
    }

    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("Profiles (0=local, 1-3 clockwise)")]
    [SerializeField] private ProfileSlot[] profiles = new ProfileSlot[4];

    [Header("Cards")]
    [SerializeField] private Transform  cardContainer;
    [SerializeField] private GameObject cardPrefab;
    [SerializeField] private Sprite[]   cardSprites = new Sprite[52];

    [Header("Shoot-out")]
    [SerializeField] private Transform  flippedCardsContainer;
    [SerializeField] private GameObject flippedCardPrefab;
    [SerializeField] private Sprite     cardBackSprite;

    [Header("Buttons")]
    [SerializeField] private Button sortButton;
    [SerializeField] private Button leaveButton;

    [Header("Avatar Sprites (0-15)")]
    [SerializeField] private Sprite[] avatarSprites = new Sprite[16];

    // ── Sort Tables ───────────────────────────────────────────────────────────

    private static readonly int[] SuitSortOrder    = { 2, 3, 0, 1 };
    private static readonly int[] RankSortPriority = { 0, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };

    // ── State ─────────────────────────────────────────────────────────────────

    private List<SlotData>   _seatedPlayers = new List<SlotData>();
    private List<GameObject> _spawnedCards  = new List<GameObject>();
    private List<GameObject> _flippedCards  = new List<GameObject>();
    private GameState        _gs;
    private Coroutine        _timerCoroutine;

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (sortButton  != null) sortButton.onClick.AddListener(OnSortClicked);
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
        EventManager.OnGameReady          += HandleGameReady;
        EventManager.OnGameStateUpdated   += HandleGameStateUpdated;
        EventManager.OnLocalHandUpdated   += HandleLocalHandUpdated;
    }

    private void OnDisable()
    {
        EventManager.OnGameReady          -= HandleGameReady;
        EventManager.OnGameStateUpdated   -= HandleGameStateUpdated;
        EventManager.OnLocalHandUpdated   -= HandleLocalHandUpdated;
        StopTimer();
    }

    private void OnDestroy()
    {
        if (sortButton  != null) sortButton.onClick.RemoveAllListeners();
        if (leaveButton != null) leaveButton.onClick.RemoveAllListeners();
        foreach (var p in profiles)
            if (p?.stealButton != null) p.stealButton.onClick.RemoveAllListeners();
    }

    // ── Game Ready ────────────────────────────────────────────────────────────

    private void HandleGameReady(List<CardData> localHand, List<SlotData> seatedPlayers)
    {
        _seatedPlayers = seatedPlayers;
        _gs = null;

        PopulateProfiles(seatedPlayers);
        SpawnCards(localHand);
        HideAllPlayedCards();
        HideAllTimers();
        HideFlippedCards();
    }

    // ── Profiles ──────────────────────────────────────────────────────────────

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

                if (slot.avatarImage != null &&
                    p.avatarIndex >= 0 && p.avatarIndex < avatarSprites.Length &&
                    avatarSprites[p.avatarIndex] != null)
                    slot.avatarImage.sprite = avatarSprites[p.avatarIndex];

                if (slot.nameText != null)
                    slot.nameText.text = p.displayName;

                if (slot.remainingCardsText != null)
                    slot.remainingCardsText.text = "13";
            }

            if (slot.stealButton  != null) slot.stealButton.gameObject.SetActive(false);
            if (slot.resultText   != null) slot.resultText.gameObject.SetActive(false);
            if (slot.playedCardImage != null) slot.playedCardImage.gameObject.SetActive(false);
            if (slot.timerImage   != null) slot.timerImage.gameObject.SetActive(false);
        }
    }

    // ── Game State Updated ────────────────────────────────────────────────────

    private void HandleGameStateUpdated(GameState gs)
    {
        _gs = gs;

        RefreshRemainingCounts();
        RefreshPlayedCards();
        RefreshCardInteractability();
        RefreshStealButtons();
        RefreshTimerBar();

        if (gs.phase == GameState.PhaseShootout)
            RefreshShootoutView();
        else
            HideFlippedCards();

        if (gs.phase == GameState.PhaseFinished)
            ShowResults();
    }

    // ── Remaining Counts ──────────────────────────────────────────────────────

    private void RefreshRemainingCounts()
    {
        if (_gs == null) return;
        for (int i = 0; i < profiles.Length && i < _seatedPlayers.Count; i++)
        {
            if (profiles[i]?.remainingCardsText == null) continue;
            string pid = _seatedPlayers[i].id;
            int count  = _gs.hands.ContainsKey(pid) ? _gs.hands[pid].Count : 0;
            profiles[i].remainingCardsText.text = count.ToString();
        }
    }

    // ── Played Cards ──────────────────────────────────────────────────────────

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

    // ── Card Interactability ──────────────────────────────────────────────────

    /// <summary>
    /// Enables only the cards the local player is allowed to play right now.
    /// Bug fix #2: Round 1 first play only allows AS.
    /// Bug fix #4: After hand rebuild, refreshes interactability on new GOs.
    /// </summary>
    private void RefreshCardInteractability()
    {
        if (_gs == null)
        {
            SetAllCardsInteractable(false);
            return;
        }

        string localId  = PlayerDataManager.PlayFabId;
        bool   isMyTurn = _gs.CurrentPlayerId == localId
                          && _gs.phase == GameState.PhasePlaying;

        if (!isMyTurn)
        {
            SetAllCardsInteractable(false);
            return;
        }

        // ── Round 1: special suit restriction ───────────────────────────────────
        if (_gs.roundNumber == 1)
        {
            bool isRound1Leader = _gs.cardsInPlay.Count == 0;

            if (isRound1Leader)
            {
                // Leader MUST play Ace of Spades — only AS is interactable
                foreach (var go in _spawnedCards)
                {
                    if (go == null) continue;
                    var btn = go.GetComponent<Button>();
                    if (btn != null) btn.interactable = (go.name == "AS");
                }
            }
            else
            {
                // Followers: must play a spade if they have one.
                // If no spade, ALL cards are interactable (any card goes to discard).
                bool hasSpade = false;
                foreach (var go in _spawnedCards)
                    if (go != null && GetSuit(go.name) == "S") { hasSpade = true; break; }

                foreach (var go in _spawnedCards)
                {
                    if (go == null) continue;
                    var btn = go.GetComponent<Button>();
                    if (btn == null) continue;
                    // If has spades: only spades clickable. If no spades: all clickable.
                    btn.interactable = hasSpade ? (GetSuit(go.name) == "S") : true;
                }
            }
            return;
        }

        // ── Normal rounds ─────────────────────────────────────────────────────
        string leadSuit    = _gs.leadSuit;
        bool   isLeading   = string.IsNullOrEmpty(leadSuit); // first card of this round
        bool   hasLeadSuit = false;

        if (!isLeading)
        {
            foreach (var go in _spawnedCards)
                if (go != null && GetSuit(go.name) == leadSuit)
                { hasLeadSuit = true; break; }
        }

        foreach (var go in _spawnedCards)
        {
            if (go == null) continue;
            var btn = go.GetComponent<Button>();
            if (btn == null) continue;

            bool canPlay;
            if (isLeading)
                canPlay = true;                              // leading — any card
            else if (!hasLeadSuit)
                canPlay = true;                              // thulla — any card
            else
                canPlay = GetSuit(go.name) == leadSuit;     // must follow suit

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

    // ── Timer Bar (per-profile) ───────────────────────────────────────────────

    /// <summary>
    /// Bug fix #7: Show timer only on the active player's profile slot.
    /// Hide all others. Drives fillAmount 1→0 locally from turnStartTime.
    /// </summary>
    private void RefreshTimerBar()
    {
        StopTimer();
        if (_gs == null || _gs.phase == GameState.PhaseFinished)
        {
            HideAllTimers();
            return;
        }

        // Find which seat is the current player
        string currentId    = _gs.CurrentPlayerId;
        int    activeSeat   = SeatIndexOf(currentId);

        // Hide all timers, then show only the active one
        HideAllTimers();

        if (activeSeat < 0 || activeSeat >= profiles.Length) return;
        if (profiles[activeSeat]?.timerImage == null) return;

        profiles[activeSeat].timerImage.gameObject.SetActive(true);

        int   totalSecs = _gs.phase == GameState.PhaseShootout
                          ? GameState.ShootoutSeconds
                          : GameState.TurnSeconds;
        float remaining = _gs.SecondsRemaining(totalSecs);

        _timerCoroutine = StartCoroutine(
            TimerBarCoroutine(profiles[activeSeat].timerImage, remaining, totalSecs));
    }

    private IEnumerator TimerBarCoroutine(Image img, float remaining, float total)
    {
        float elapsed = total - remaining;
        while (elapsed < total)
        {
            if (img != null)
                img.fillAmount = 1f - (elapsed / total);
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

    // ── Steal Button ──────────────────────────────────────────────────────────

    private void RefreshStealButtons()
    {
        if (_gs == null) { HideAllStealButtons(); return; }

        string localId  = PlayerDataManager.PlayFabId;
        bool   isMyTurn = _gs.CurrentPlayerId == localId;
        bool   isLeading = isMyTurn
                           && string.IsNullOrEmpty(_gs.leadSuit)
                           && _gs.cardsInPlay.Count == 0;

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

    // ── Card Spawning ─────────────────────────────────────────────────────────

    private void SpawnCards(List<CardData> hand)
    {
        foreach (var go in _spawnedCards)
            if (go != null) Destroy(go);
        _spawnedCards.Clear();

        if (cardPrefab == null || cardContainer == null)
        {
            Debug.LogError("[InGameView] cardPrefab or cardContainer not assigned.");
            return;
        }

        foreach (var card in hand)
        {
            GameObject go = Instantiate(cardPrefab, cardContainer);
            go.name = card.ShortCode;

            var img = go.GetComponent<Image>();
            if (img != null)
            {
                Sprite sp = GetCardSprite(card);
                if (sp != null) img.sprite = sp;
                else Debug.LogWarning($"[InGameView] No sprite for {card.ShortCode}");
            }

            var btn = go.GetComponent<Button>();
            if (btn != null)
            {
                string code = card.ShortCode;
                btn.onClick.AddListener(() => OnCardClicked(code));
                btn.interactable = false;
            }

            _spawnedCards.Add(go);
        }
    }

    private void RemoveCardFromHand(string cardCode)
    {
        for (int i = _spawnedCards.Count - 1; i >= 0; i--)
        {
            if (_spawnedCards[i] != null && _spawnedCards[i].name == cardCode)
            {
                Destroy(_spawnedCards[i]);
                _spawnedCards.RemoveAt(i);
                return;
            }
        }
    }

    // ── Local Hand Updated (thulla pickup / steal / shootout draw) ────────────

    /// <summary>
    /// Bug fix #3: Fully rebuilds hand display from the complete updated hand.
    /// Bug fix #4: After rebuild, immediately refreshes interactability on new GOs.
    /// Called AFTER GameManager has already updated _myHand and _gs.
    /// </summary>
    private void HandleLocalHandUpdated(List<string> handCodes, GameState gs)
    {
        // Update _gs FIRST so RefreshCardInteractability sees the correct
        // CurrentPlayerId. This is critical for thulla: GameManager sets
        // currentPlayerIndex to the pickup player BEFORE firing this event,
        // but InGameView._gs is only updated by HandleGameStateUpdated.
        // Carrying gs here ensures _gs is fresh before we check interactability.
        if (gs != null) _gs = gs;

        // Rebuild card GOs from complete new hand.
        // Called after every local card play, thulla pickup, steal, shootout draw.
        var hand = new List<CardData>();
        foreach (var code in handCodes)
        {
            var cd = CardData.FromShortCode(code);
            if (cd != null) hand.Add(cd);
        }

        SpawnCards(hand);

        // Refresh interactability — now _gs is up to date so CurrentPlayerId
        // correctly identifies whether it's local player's turn.
        RefreshCardInteractability();
    }

    // ── Shoot-out View ────────────────────────────────────────────────────────

    private void RefreshShootoutView()
    {
        if (_gs == null) return;

        string drawerId    = _gs.shootoutDrawerId;
        string responderId = "";
        foreach (var pid in _gs.activePlayers)
            if (pid != drawerId) { responderId = pid; break; }

        if (string.IsNullOrEmpty(responderId)) return;

        bool isDrawer = drawerId == PlayerDataManager.PlayFabId;
        if (!isDrawer) { HideFlippedCards(); return; }

        int cardCount = _gs.hands.ContainsKey(responderId)
                        ? _gs.hands[responderId].Count : 0;
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
            if (img != null && cardBackSprite != null)
                img.sprite = cardBackSprite;

            var btn = go.GetComponent<Button>();
            if (btn != null)
                btn.onClick.AddListener(OnFlippedCardClicked);

            _flippedCards.Add(go);
        }
    }

    private void HideFlippedCards()
    {
        foreach (var go in _flippedCards)
            if (go != null) Destroy(go);
        _flippedCards.Clear();

        if (flippedCardsContainer != null)
            flippedCardsContainer.gameObject.SetActive(false);
    }

    // ── Results ───────────────────────────────────────────────────────────────

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
            if (_gs.bhabhi == pid)              label = "Bhabhi!";
            else if (_gs.winners.Contains(pid)) label = "Winner!";

            if (!string.IsNullOrEmpty(label))
            {
                profiles[i].resultText.text = label;
                profiles[i].resultText.gameObject.SetActive(true);
            }
        }
    }

    // ── Sort ──────────────────────────────────────────────────────────────────

    private void SortCards()
    {
        if (_spawnedCards.Count == 0) return;

        var sortable = new List<(GameObject go, int key)>();
        foreach (var go in _spawnedCards)
        {
            if (go == null) continue;
            CardData card = CardData.FromShortCode(go.name);
            if (card == null) { sortable.Add((go, int.MaxValue)); continue; }

            int sp = System.Array.IndexOf(SuitSortOrder, card.suitIndex);
            if (sp < 0) sp = 4;
            int rp = RankSortPriority[card.rankIndex];
            sortable.Add((go, sp * 13 + rp));
        }

        sortable.Sort((a, b) => a.key.CompareTo(b.key));
        for (int i = 0; i < sortable.Count; i++)
            sortable[i].go.transform.SetSiblingIndex(i);
    }

    // ── Button Handlers ───────────────────────────────────────────────────────

    private void OnCardClicked(string cardCode)
    {
        // Fire to GameManager — it validates, updates _myHand, then fires
        // FireLocalHandUpdated which rebuilds the hand display.
        // Do NOT remove card here — GameManager drives all hand changes.
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

    private void OnSortClicked()  => SortCards();
    private void OnLeaveClicked() => EventManager.FireLeaveGameRequested();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private int SeatIndexOf(string playerId)
    {
        for (int i = 0; i < _seatedPlayers.Count; i++)
            if (_seatedPlayers[i].id == playerId) return i;
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
