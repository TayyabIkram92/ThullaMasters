using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class MatchmakingView : MonoBehaviour
{
    [Header("Entry Fee")] [SerializeField] private Text entryFeeTxt;

    [Header("Slots")] [SerializeField] private PlayerSlotUI[] playerSlots;

    [Header("Buttons")] [SerializeField] private Button startButton;
    [SerializeField] private Button backButton;
    [SerializeField] private Button addFriendButton;

    [Header("Status")] [SerializeField] private Text searchingTxt;

    private bool _hasDeductedCoins = false;
    private bool _matchStarted = false;
    private RoomData _currentRoom;

    private void OnEnable()
    {
        startButton.onClick.AddListener(OnStartClicked);
        backButton.onClick.AddListener(OnBackClicked);
        if (addFriendButton) addFriendButton.onClick.AddListener(OnAddFriendClicked);

        EventManager.OnRoomUpdated += HandleRoomUpdated;
        EventManager.OnMatchFound += HandleMatchFound;
        EventManager.OnMatchmakingError += HandleMatchmakingError;

        ResetView();
    }

    private void OnDisable()
    {
        startButton.onClick.RemoveListener(OnStartClicked);
        backButton.onClick.RemoveListener(OnBackClicked);
        if (addFriendButton) addFriendButton.onClick.RemoveListener(OnAddFriendClicked);

        EventManager.OnRoomUpdated -= HandleRoomUpdated;
        EventManager.OnMatchFound -= HandleMatchFound;
        EventManager.OnMatchmakingError -= HandleMatchmakingError;
    }

    private void ResetView()
    {
        _hasDeductedCoins = false;
        _matchStarted = false;
        _currentRoom = null;

        // Show entry fee
        if (entryFeeTxt != null && GameModeManager.SelectedMode != null)
            entryFeeTxt.text = GameModeManager.SelectedMode.EntryFee.ToString();

        // searchingTxt starts hidden
        searchingTxt.gameObject.SetActive(false);

        startButton.gameObject.SetActive(true);
        startButton.interactable = true;
        backButton.gameObject.SetActive(true);
        backButton.interactable = true;
        if (addFriendButton) addFriendButton.gameObject.SetActive(true);

        foreach (var slot in playerSlots)
        {
            slot.SetEmpty();
            slot.ShowFriendInviteUI();
        }

        if (playerSlots.Length > 0)
            playerSlots[0].SetLocalPlayer(new SlotData
            {
                id = PlayerDataManager.PlayFabId,
                displayName = PlayerDataManager.DisplayName,
                avatarIndex = PlayerDataManager.AvatarIndex,
                isBot = false
            });
    }

    private void HandleRoomUpdated(RoomData room)
    {
        _currentRoom = room;
        UpdateSlots(room);

        bool allFilled = room.players != null && room.players.Count >= 4;

        if (allFilled && !_hasDeductedCoins)
        {
            _hasDeductedCoins = true;
            DisableStartAndBack();

            if (GameModeManager.SelectedMode != null)
                // FireDeductCoinsRequested(int) — 1 param
                EventManager.FireDeductCoinsRequested(GameModeManager.SelectedMode.EntryFee);
        }
    }

    private void UpdateSlots(RoomData room)
    {
        for (int i = 1; i < playerSlots.Length; i++)
            playerSlots[i].SetEmpty();

        if (room.players == null) return;

        int slotIdx = 1;
        foreach (var player in room.players)
        {
            if (player.id == PlayerDataManager.PlayFabId) continue;
            if (slotIdx >= playerSlots.Length) break;
            // SetPlayer(SlotData, bool) — 2 params
            playerSlots[slotIdx].SetPlayer(player, true);
            slotIdx++;
        }

        searchingTxt.text = room.players.Count >= 4
            ? "Match found!"
            : "Searching... (" + room.players.Count + "/4)";
    }

    private void HandleMatchFound(RoomData room)
    {
        EventManager.FirePlaySound(SoundType.MatchFound);
        _matchStarted = true;
        DisableStartAndBack();
        HideAllFriendInviteUI();
        EventManager.FireShowView(ViewType.InGame);
    }

    private void HandleMatchmakingError(string message)
    {
        searchingTxt.text = "Error: " + message;
        if (_hasDeductedCoins && GameModeManager.SelectedMode != null)
        {
            EventManager.FireAddCoinsRequested(
                GameModeManager.SelectedMode.EntryFee, "matchmaking_refund");
            _hasDeductedCoins = false;
        }

        backButton.interactable = true;
    }

    private void DisableStartAndBack()
    {
        startButton.gameObject.SetActive(false);
        backButton.gameObject.SetActive(false);
        if (addFriendButton) addFriendButton.interactable = false;
    }

    private void HideAllFriendInviteUI()
    {
        foreach (var slot in playerSlots)
            slot.HideFriendInviteUI(); // PlayerSlotUI.HideFriendInviteUI()
        if (addFriendButton) addFriendButton.gameObject.SetActive(false);
    }

    private void OnStartClicked()
    {
        startButton.gameObject.SetActive(false);
        backButton.gameObject.SetActive(false);

        foreach (var slot in playerSlots)
            slot.HideFriendInviteUI();

        searchingTxt.gameObject.SetActive(true);
        searchingTxt.text = "Searching... (1/4)";

        if (GameModeManager.SelectedMode != null)
            EventManager.FireMatchmakingStartRequested(GameModeManager.SelectedMode);
    }

    private void OnBackClicked()
    {
        EventManager.FireMatchmakingCancelRequested();

        if (_hasDeductedCoins && GameModeManager.SelectedMode != null)
        {
            EventManager.FireAddCoinsRequested(
                GameModeManager.SelectedMode.EntryFee, "matchmaking_cancelled");
            _hasDeductedCoins = false;
        }

        // Delete pre-created room if we were the host and matchmaking never started
        string preCreatedRoomId = InviteManager.CurrentRoomId;
        if (!string.IsNullOrEmpty(preCreatedRoomId) && !_matchStarted && FirebaseManager.DB != null)
        {
            FirebaseManager.DB
                .Collection("rooms")
                .Document(preCreatedRoomId)
                .DeleteAsync();

            InviteManager.ClearCurrentRoomId();
        }

        EventManager.FireShowView(ViewType.GameSelection);
    }

    private async void OnAddFriendClicked()
    {
        if (!FriendsManager.IsInitialized)
            EventManager.FireFetchFriendsRequested();

        // Only create room if one doesn't already exist
        if (string.IsNullOrEmpty(InviteManager.CurrentRoomId))
        {
            addFriendButton.interactable = false;

            bool success = await MatchmakingManager.CreateRoomAsync();

            addFriendButton.interactable = true;

            if (!success)
            {
                Debug.LogWarning("[MatchmakingView] Room creation failed, cannot open friends list.");
                return;
            }
        }

        EventManager.FireShowView(ViewType.OnlineFriends, true);
    }
}