using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Invite popup shown for 10 seconds. Auto-dismisses if no action.
/// On accept: navigates to MatchmakingView in invited-user state (no start/back/add buttons).
/// On reject/expire: writes rejection response to room doc so host is notified.
/// Clears all invite PlayerPrefs on both paths to prevent stale data.
/// </summary>
public class InvitePopupView : MonoBehaviour
{
    [SerializeField] private Text senderNameTxt;
    [SerializeField] private Text entryFeeTxt;
    [SerializeField] private Button acceptButton;
    [SerializeField] private Button rejectButton;

    private Coroutine _autoExpireCoroutine;
    private string _roomId;
    private string _senderId;
    private int _entryFee;
    private bool _hasActed; // prevents AutoExpire firing after manual accept/reject

    private void OnEnable()
    {
        _hasActed = false;

        EventManager.FirePlaySound(SoundType.InvitePopup);
        acceptButton.onClick.AddListener(OnAcceptClicked);
        rejectButton.onClick.AddListener(OnRejectClicked);

        string senderName = PlayerPrefs.GetString("InviteSenderName", "Someone");
        _senderId = PlayerPrefs.GetString("InviteSenderId", "");
        _roomId = PlayerPrefs.GetString("InviteRoomId", "");
        _entryFee = PlayerPrefs.GetInt("InviteEntryFee", 0);

        senderNameTxt.text = senderName + " invited you!";
        if (entryFeeTxt) entryFeeTxt.text = "Entry Fee: " + _entryFee + " coins";

        acceptButton.interactable = true;
        rejectButton.interactable = true;

        _autoExpireCoroutine = StartCoroutine(AutoExpire());
    }

    private void OnDisable()
    {
        acceptButton.onClick.RemoveListener(OnAcceptClicked);
        rejectButton.onClick.RemoveListener(OnRejectClicked);

        if (_autoExpireCoroutine != null)
        {
            StopCoroutine(_autoExpireCoroutine);
            _autoExpireCoroutine = null;
        }

        // If the popup was closed externally (e.g. back button, scene change)
        // without the user acting, treat it as a rejection so the host is notified.
        if (!_hasActed)
        {
            _hasActed = true;
            EventManager.FireInviteResponseRequested(_roomId, _senderId, false);
            EventManager.FireRejectInviteRequested();
        }
    }

    private void OnAcceptClicked()
    {
        if (_hasActed) return; // guard against double tap

        // Disable buttons immediately to prevent double tap
        acceptButton.interactable = false;
        rejectButton.interactable = false;

        // Check coins FIRST — all logic lives inside the callback
        EventManager.FireGetCoinsRequested(coins =>
        {
            if (coins < _entryFee)
            {
                EventManager.FireShowPopUp("Not enough coins to join. Need " + _entryFee + " coins.");
                // Notify host that invite was rejected due to insufficient coins
                EventManager.FireInviteResponseRequested(_roomId, _senderId, false);
                EventManager.FireRejectInviteRequested();
                Dismiss();
                return;
            }

            _hasActed = true;

            // Stop auto-expire since user acted
            if (_autoExpireCoroutine != null)
            {
                StopCoroutine(_autoExpireCoroutine);
                _autoExpireCoroutine = null;
            }

            // Notify host that invite was accepted
            EventManager.FireInviteResponseRequested(_roomId, _senderId, true);

            // CRITICAL ORDER:
            // FireAcceptInviteRequested MUST fire before FireShowView(Matchmaking)
            // so that InviteManager.HandleAcceptInvite sets IsInvitedUser=1 in PlayerPrefs
            // BEFORE MatchmakingView.ResetView() reads it.
            EventManager.FireAcceptInviteRequested(_roomId);

            // Hide popup and clean up all invite PlayerPrefs.
            // Entry fee is now read directly from RoomData in MatchmakingView.HandleRoomUpdated,
            // so InviteEntryFee no longer needs to survive past this point.
            PlayerPrefs.DeleteKey("InviteSenderName");
            PlayerPrefs.DeleteKey("InviteSenderId");
            PlayerPrefs.DeleteKey("InviteRoomId");
            PlayerPrefs.DeleteKey("InviteEntryFee");
            PlayerPrefs.Save();

            EventManager.FireHideView(ViewType.InvitePopUp);

            // Navigate to matchmaking — ResetView reads IsInvitedUser=1
            EventManager.FireShowView(ViewType.Matchmaking);
        });
    }

    private void OnRejectClicked()
    {
        if (_hasActed) return; // guard against double tap
        _hasActed = true;

        // Notify host that invite was rejected
        EventManager.FireInviteResponseRequested(_roomId, _senderId, false);
        EventManager.FireRejectInviteRequested();
        Dismiss();
    }

    private IEnumerator AutoExpire()
    {
        yield return new WaitForSeconds(10f);

        if (_hasActed) yield break; // user already acted — do nothing
        _hasActed = true;

        // Treat expiry as rejection so host is notified
        EventManager.FireInviteResponseRequested(_roomId, _senderId, false);
        EventManager.FireRejectInviteRequested();
        Dismiss();
    }

    private void Dismiss()
    {
        if (_autoExpireCoroutine != null)
        {
            StopCoroutine(_autoExpireCoroutine);
            _autoExpireCoroutine = null;
        }

        PlayerPrefs.DeleteKey("InviteSenderName");
        PlayerPrefs.DeleteKey("InviteSenderId");
        PlayerPrefs.DeleteKey("InviteRoomId");
        PlayerPrefs.DeleteKey("InviteEntryFee");
        PlayerPrefs.Save();

        EventManager.FireHideView(ViewType.InvitePopUp);
    }
}