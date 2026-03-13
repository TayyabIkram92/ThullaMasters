using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Invite popup shown for 10 seconds. Auto-dismisses if no action.
/// </summary>
public class InvitePopupView : MonoBehaviour
{
    [SerializeField] private Text senderNameTxt;
    [SerializeField] private Text entryFeeTxt; // e.g. "Entry Fee: 500 coins"
    [SerializeField] private Button acceptButton;
    [SerializeField] private Button rejectButton;

    private Coroutine _autoExpireCoroutine;
    private string _roomId;
    private int _entryFee;

    private void OnEnable()
    {
        EventManager.FirePlaySound(SoundType.InvitePopup);
        acceptButton.onClick.AddListener(OnAcceptClicked);
        rejectButton.onClick.AddListener(OnRejectClicked);

        string senderName = PlayerPrefs.GetString("InviteSenderName", "Someone");
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
    }

    private void OnAcceptClicked()
    {
        // Reject if player cannot afford the entry fee
        EventManager.FireGetCoinsRequested(coins =>
        {
            if (coins < _entryFee)
            {
                EventManager.FireShowPopUp("Not enough coins to join. Need " + _entryFee + " coins.");
                Dismiss();
                EventManager.FireRejectInviteRequested();
                return;
            }

            // rest of your code that was after this check
        });

        acceptButton.interactable = false;
        rejectButton.interactable = false;
        if (_autoExpireCoroutine != null)
        {
            StopCoroutine(_autoExpireCoroutine);
            _autoExpireCoroutine = null;
        }

        EventManager.FireHideView(ViewType.InvitePopUp);

        // Navigate to matchmaking; MatchmakingManager handles join via OnAcceptInviteRequested
        EventManager.FireShowView(ViewType.Matchmaking);
        EventManager.FireAcceptInviteRequested(_roomId);
    }

    private void OnRejectClicked()
    {
        Dismiss();
        EventManager.FireRejectInviteRequested();
    }

    private IEnumerator AutoExpire()
    {
        yield return new WaitForSeconds(10f);
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