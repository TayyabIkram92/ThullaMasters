using UnityEngine;
using UnityEngine.UI;
using UI;


/// <summary>
/// UI controller for the Matchmaking screen.
///
/// Inspector wiring:
///  - backButton        → BackBtn
///  - startButton       → StartBtn
///  - coinsText         → CoinsBar/CoinsTxt
///  - depositButton     → CoinsBar/DepositBtn
///  - entryFeeText      → EntryFee/EntryFeeTxt
///  - searchingText     → FindingPlayers Text
///  - localSlot         → UserProfile GO          (PlayerSlotUI — isFriendSlot = false)
///  - friendSlot        → Friend/RandomProfile GO  (PlayerSlotUI — isFriendSlot = true)
///  - randomSlots[0]    → RandomProfile GO (3rd)   (PlayerSlotUI — isFriendSlot = false)
///  - randomSlots[1]    → RandomProfile GO (4th)   (PlayerSlotUI — isFriendSlot = false)
/// </summary>
public class MatchmakingView : MonoBehaviour
{
    [Header("Top Bar")]
    [SerializeField] private Button backButton;
    [SerializeField] private Text   coinsText;
    [SerializeField] private Button depositButton;

    [Header("Entry Fee")]
    [SerializeField] private Text entryFeeText;

    [Header("Player Slots")]
    [SerializeField] private PlayerSlotUI   localSlot;
    [SerializeField] private PlayerSlotUI   friendSlot;
    [SerializeField] private PlayerSlotUI[] randomSlots = new PlayerSlotUI[2];

    [Header("Buttons")]
    [SerializeField] private Button startButton;
    [SerializeField] private Button addFriendButton;

    [Header("Status Text")]
    [SerializeField] private Text searchingText;

    // ── Private State ─────────────────────────────────────────────────────────

    private GameModeData _selectedMode;
    private bool         _isSearching = false;

    // ── Unity Lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        if (backButton      != null) backButton.onClick.AddListener(OnBackClicked);
        if (startButton     != null) startButton.onClick.AddListener(OnStartClicked);
        if (depositButton   != null) depositButton.onClick.AddListener(OnDepositClicked);
        if (addFriendButton != null) addFriendButton.onClick.AddListener(OnAddFriendClicked);
    }

    private void OnEnable()
    {
        EventManager.OnRoomUpdated      += HandleRoomUpdated;
        EventManager.OnMatchFound       += HandleMatchFound;
        EventManager.OnMatchmakingError += HandleMatchmakingError;

        ResetView();
    }

    private void OnDisable()
    {
        EventManager.OnRoomUpdated      -= HandleRoomUpdated;
        EventManager.OnMatchFound       -= HandleMatchFound;
        EventManager.OnMatchmakingError -= HandleMatchmakingError;

        HideStatusText();
    }

    private void OnDestroy()
    {
        if (backButton      != null) backButton.onClick.RemoveAllListeners();
        if (startButton     != null) startButton.onClick.RemoveAllListeners();
        if (depositButton   != null) depositButton.onClick.RemoveAllListeners();
        if (addFriendButton != null) addFriendButton.onClick.RemoveAllListeners();
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    private void ResetView()
    {
        _isSearching = false;

        // Read selected mode from GameModeManager — set by GameSelectionView
        // before FireShowView(Matchmaking) so it is always available here
        _selectedMode = GameModeManager.SelectedMode;

        if (coinsText != null)
            coinsText.text = PlayerDataManager.Coins.ToString();

        // Slot 1 always shows local player
        localSlot?.SetLocalPlayer(PlayerDataManager.DisplayName, PlayerDataManager.AvatarIndex);

        // Slots 2, 3, 4 start empty
        friendSlot?.SetEmpty();
        foreach (var slot in randomSlots)
            slot?.SetEmpty();

        RefreshEntryFeeText();

        // Hide status text — shown only after Start is pressed
        HideStatusText();
        if (searchingText != null)
            searchingText.gameObject.SetActive(false);

        SetSearchingState(false);
    }

    private void RefreshEntryFeeText()
    {
        if (entryFeeText != null && _selectedMode != null)
            entryFeeText.text = _selectedMode.EntryFee.ToString();
    }

    // ── Status Text ───────────────────────────────────────────────────────────

    private void ShowStatusText(string message)
    {
        if (searchingText == null) return;
        searchingText.gameObject.SetActive(true);
        searchingText.text = message;
    }

    private void HideStatusText()
    {
        if (searchingText == null) return;
        searchingText.gameObject.SetActive(false);
        searchingText.text = string.Empty;
    }

    // ── Event Handlers ────────────────────────────────────────────────────────

    /// <summary>
    /// Room updated from Firestore — always show local player in slot 1,
    /// fill remaining slots with everyone else in order.
    /// Every player sees themselves in slot 1 on their own screen.
    /// </summary>
    private void HandleRoomUpdated(RoomData room)
    {
        // Update local player slot from room data
        foreach (var slot in room.players)
        {
            if (slot.id == PlayerDataManager.PlayFabId)
            {
                localSlot?.SetLocalPlayer(slot.displayName, slot.avatarIndex);
                break;
            }
        }

        // Collect everyone else
        var others = new System.Collections.Generic.List<SlotData>();
        foreach (var slot in room.players)
            if (slot.id != PlayerDataManager.PlayFabId)
                others.Add(slot);

        FillOrEmpty(friendSlot,     others, 0);
        FillOrEmpty(randomSlots[0], others, 1);
        FillOrEmpty(randomSlots[1], others, 2);
    }

    private void FillOrEmpty(PlayerSlotUI slot, System.Collections.Generic.List<SlotData> others, int index)
    {
        if (slot == null) return;
        if (index < others.Count) slot.SetPlayer(others[index]);
        else                      slot.SetEmpty();
    }

    /// <summary>
    /// All 4 slots filled — switch animated text to "Joining Game...".
    /// Navigation to InGame will be wired here once gameplay is implemented.
    /// </summary>
    private void HandleMatchFound(RoomData room)
    {
        ShowStatusText("Joining Game...");
        Debug.Log("[MatchmakingView] Match found — navigating to InGame.");
        EventManager.FireShowView(ViewType.InGame);
    }

    private void HandleMatchmakingError(string msg)
    {
        HideStatusText();
        if (searchingText != null)
            searchingText.gameObject.SetActive(false);

        SetSearchingState(false);
        _isSearching = false;

        // Refund coins
        if (_selectedMode != null)
            PlayerDataManager.AddCoins(_selectedMode.EntryFee);

        if (coinsText != null)
            coinsText.text = PlayerDataManager.Coins.ToString();

        EventManager.FireShowPopUp(msg);
    }

    // ── Button Callbacks ──────────────────────────────────────────────────────

    private void OnBackClicked()
    {
        if (_isSearching)
            EventManager.FireMatchmakingCancelled();

        EventManager.FireShowView(ViewType.GameSelection);
    }

    private void OnStartClicked()
    {
        if (_isSearching) return;

        if (_selectedMode == null)
        {
            EventManager.FireShowPopUp("No game mode selected.");
            return;
        }

        if (PlayerDataManager.Coins < _selectedMode.EntryFee)
        {
            EventManager.FireShowView(ViewType.UserPopUp, showAsDialogue: true);
            EventManager.FireShowPopUp(
                $"You need {_selectedMode.EntryFee} coins to enter.\nYou have {PlayerDataManager.Coins} coins.");
            return;
        }

        EventManager.FireDeductCoinsRequested(_selectedMode.EntryFee);

        if (coinsText != null)
            coinsText.text = PlayerDataManager.Coins.ToString();

        _isSearching = true;
        SetSearchingState(true);

        ShowStatusText("Searching For Players...");

        EventManager.FireMatchmakingStartRequested(_selectedMode);
    }

    private void OnDepositClicked()
    {
        Debug.Log("[MatchmakingView] Deposit button clicked.");
    }

    private void OnAddFriendClicked()
    {
        EventManager.FireShowView(ViewType.OnlineFriends, showAsDialogue: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetSearchingState(bool isSearching)
    {
        if (startButton != null) startButton.gameObject.SetActive(!isSearching);
        if (backButton  != null) backButton.gameObject.SetActive(!isSearching);
    }
}
