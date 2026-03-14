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
    private bool _isInvitedUser = false; // true when this player joined via invite
    private bool _inviteSent = false; // true once host has sent an invite
    private RoomData _currentRoom;
    private bool _inMatchmakingPhase = false; // true from join/start until match found

    private void OnEnable()
    {
        startButton.onClick.AddListener(OnStartClicked);
        backButton.onClick.AddListener(OnBackClicked);
        if (addFriendButton) addFriendButton.onClick.AddListener(OnAddFriendClicked);

        EventManager.OnRoomUpdated += HandleRoomUpdated;
        EventManager.OnMatchFound += HandleMatchFound;
        EventManager.OnMatchmakingError += HandleMatchmakingError;
        EventManager.OnInviteAccepted += HandleInviteAccepted;
        EventManager.OnInviteRejected += HandleInviteRejected;
        EventManager.OnInviteSent += HandleInviteSent;

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
        EventManager.OnInviteAccepted -= HandleInviteAccepted;
        EventManager.OnInviteRejected -= HandleInviteRejected;
        EventManager.OnInviteSent -= HandleInviteSent;

        _inMatchmakingPhase = false;
    }

    // ── Platform Hooks — disconnect while matchmaking view is active ──────────
    // MatchmakingManager also has these hooks but the view must clear its own
    // state so it doesn't trigger double cleanup if both fire.

    private void OnApplicationPause(bool isPaused)
    {
        // MatchmakingManager handles the actual room cleanup/reload on pause.
        // We just mark that we are no longer active so OnDisable doesn't fire extras.
        if (isPaused && _inMatchmakingPhase)
            _inMatchmakingPhase = false;
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus && _inMatchmakingPhase)
            _inMatchmakingPhase = false;
    }

    private void ResetView()
    {
        _hasDeductedCoins = false;
        _matchStarted = false;
        _inviteSent = false;
        _currentRoom = null;

        // Check if we arrived here via accepted invite
        _isInvitedUser = PlayerPrefs.GetInt("IsInvitedUser", 0) == 1;
        PlayerPrefs.DeleteKey("IsInvitedUser");
        PlayerPrefs.Save();

        if (entryFeeTxt != null && GameModeManager.SelectedMode != null)
            entryFeeTxt.text = GameModeManager.SelectedMode.EntryFee.ToString();

        if (_isInvitedUser)
        {
            _inMatchmakingPhase = true;
            // Invited user: already in searching state, no host controls
            searchingTxt.gameObject.SetActive(true);
            searchingTxt.text = "Joining room...";

            startButton.gameObject.SetActive(false);
            backButton.gameObject.SetActive(false);
            if (addFriendButton) addFriendButton.interactable = false;

            foreach (var slot in playerSlots)
            {
                slot.SetEmpty();
                slot.HideFriendInviteUI();
            }

            // Show local player in slot 0
            if (playerSlots.Length > 0)
                playerSlots[0].SetLocalPlayer(new SlotData
                {
                    id = PlayerDataManager.PlayFabId,
                    displayName = PlayerDataManager.DisplayName,
                    avatarIndex = PlayerDataManager.AvatarIndex,
                    isBot = false
                });
        }
        else
        {
            // Normal host flow
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
    }

    // ── Room Updates ──────────────────────────────────────────────────────────

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
            playerSlots[slotIdx].SetPlayer(player, true);

            // Hide invite UI for filled slots — friend already joined
            if (!_isInvitedUser)
                playerSlots[slotIdx].HideFriendInviteUI();

            slotIdx++;
        }

        // Restore invite UI on remaining empty slots only if:
        // host player AND no invite has been sent yet
        if (!_isInvitedUser && !_inviteSent)
        {
            for (int i = slotIdx; i < playerSlots.Length; i++)
                playerSlots[i].ShowFriendInviteUI();
        }

        searchingTxt.text = room.players.Count >= 4
            ? "Match found!"
            : "Searching... (" + room.players.Count + "/4)";

        if (!searchingTxt.gameObject.activeSelf && _isInvitedUser)
            searchingTxt.gameObject.SetActive(true);
    }

    // ── Invite Sent (host side) ───────────────────────────────────────────────

    /// <summary>
    /// Called immediately after host sends invite via OnlineFriendsView.
    /// Hides AddIcon and PopupAddFriend on the friend slot (via PlayerSlotUI),
    /// disables addFriendButton and backButton, shows "Waiting for friend..." status.
    /// PlayerSlotUI.HideFriendInviteUI() is guarded by isFriendSlot so only the
    /// correct slot is affected.
    /// </summary>
    private void HandleInviteSent()
    {
        if (_isInvitedUser) return; // only host cares

        _inviteSent = true;

        // Hide AddIcon + PopupAddFriend on friend slot — PlayerSlotUI handles this
        foreach (var slot in playerSlots)
            slot.HideFriendInviteUI();

        // Disable the add friend button (Friend/RandomProfile button)
        if (addFriendButton) addFriendButton.interactable = false;

        // Disable back while waiting for friend to respond
        backButton.interactable = false;

        // Show waiting status
        searchingTxt.gameObject.SetActive(true);
        searchingTxt.text = "Waiting for friend...";
    }

    // ── Invite Response Handlers (host side) ──────────────────────────────────

    /// <summary>
    /// Called on HOST device when invited friend accepts.
    /// Slot fills automatically via OnRoomUpdated from Firestore.
    /// Auto-starts matchmaking so remaining 2 seats fill via normal flow / bots.
    /// </summary>
    private void HandleInviteAccepted(string acceptedPlayFabId)
    {
        if (_isInvitedUser) return; // only host cares

        // Keep back and add friend disabled — friend is joining
        backButton.interactable = false;
        if (addFriendButton) addFriendButton.interactable = false;

        // Hide start and back buttons — transitioning to searching state
        startButton.gameObject.SetActive(false);
        backButton.gameObject.SetActive(false);

        // Update status text
        searchingTxt.gameObject.SetActive(true);
        searchingTxt.text = "Friend joining, searching for players...";

        // Auto-start matchmaking — HandleMatchmakingStart reuses CurrentRoomId
        // so it will not create a new room, just starts the listener and bot-fill
        if (!_matchStarted && GameModeManager.SelectedMode != null)
        {
            _inMatchmakingPhase = true;
            EventManager.FireMatchmakingStartRequested(GameModeManager.SelectedMode);
        }
    }

    /// <summary>
    /// Called on HOST device when invited friend rejects or invite expires.
    /// Shows popup, re-enables buttons, and restores AddIcon/PopupAddFriend.
    /// </summary>
    private void HandleInviteRejected(string rejectedPlayFabId)
    {
        if (_isInvitedUser) return; // only host cares

        _inviteSent = false;

        string name = GetDisplayNameForId(rejectedPlayFabId);
        EventManager.FireShowPopUp(string.IsNullOrEmpty(name)
            ? "Friend declined the invite."
            : name + " declined the invite.");

        // Re-enable back and add friend buttons
        backButton.interactable = true;
        if (addFriendButton)
        {
            addFriendButton.interactable = true;
            addFriendButton.gameObject.SetActive(true);
        }

        // Hide searching text — back to pre-search idle state
        searchingTxt.gameObject.SetActive(false);

        // Restore AddIcon + PopupAddFriend on empty slots via PlayerSlotUI
        if (_currentRoom != null)
        {
            int filledCount = 0;
            foreach (var p in _currentRoom.players)
                if (p.id != PlayerDataManager.PlayFabId)
                    filledCount++;

            for (int i = filledCount + 1; i < playerSlots.Length; i++)
                playerSlots[i].ShowFriendInviteUI();
        }
        else
        {
            // No room data yet — restore all non-local slots
            for (int i = 1; i < playerSlots.Length; i++)
                playerSlots[i].ShowFriendInviteUI();
        }
    }

    private string GetDisplayNameForId(string playFabId)
    {
        if (_currentRoom?.players == null) return "";
        foreach (var p in _currentRoom.players)
            if (p.id == playFabId)
                return p.displayName;
        return "";
    }

    // ── Match / Error ─────────────────────────────────────────────────────────

    private void HandleMatchFound(RoomData room)
    {
        EventManager.FirePlaySound(SoundType.MatchFound);
        _matchStarted = true;
        _inMatchmakingPhase = false; // HostWatchdog + InGameManager take over from here
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

        if (!_isInvitedUser)
            backButton.interactable = true;
    }

    // ── Button Handlers ───────────────────────────────────────────────────────

    private void OnStartClicked()
    {
        _inMatchmakingPhase = true;
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

        // Always delete the room when leaving — whether matchmaking started or not.
        // This covers the ghost-room case where a player quits after OnMatchFound
        // fired (status="starting") but before the game actually began.
        // MatchmakingManager.CleanupRoom handles the delete for the host path;
        // here we guard the pre-created room that MatchmakingManager doesn't own.
        string preCreatedRoomId = InviteManager.CurrentRoomId;
        if (!string.IsNullOrEmpty(preCreatedRoomId) && FirebaseManager.DB != null)
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void DisableStartAndBack()
    {
        startButton.gameObject.SetActive(false);
        backButton.gameObject.SetActive(false);
        if (addFriendButton) addFriendButton.interactable = false;
    }

    private void HideAllFriendInviteUI()
    {
        foreach (var slot in playerSlots)
            slot.HideFriendInviteUI();
        if (addFriendButton) addFriendButton.gameObject.SetActive(false);
    }
}