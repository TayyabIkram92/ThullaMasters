using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class PlayerSlotUI : MonoBehaviour
{
    [Header("Filled State")]
    [SerializeField] private GameObject filledStateGO;
    [SerializeField] private Image avatarImage;
    [SerializeField] private Text displayNameTxt;
    [SerializeField] private Sprite[] avatarSprites;

    [Header("Friend Slot")]
    public bool isFriendSlot;
    public GameObject addIconGO;
    public GameObject popupAddFriendGO;

    private bool _isEmpty = true;

    // Called from MatchmakingView.ResetView() — 1 param, no animation
    public void SetLocalPlayer(SlotData data)
    {
        _isEmpty = false;
        if (filledStateGO) filledStateGO.SetActive(true);  // ← ADD
        if (displayNameTxt) displayNameTxt.text = data.displayName;
        if (avatarImage != null && avatarSprites != null &&
            data.avatarIndex >= 0 && data.avatarIndex < avatarSprites.Length)
            avatarImage.sprite = avatarSprites[data.avatarIndex];
    }

    // Called from MatchmakingView.UpdateSlots() — 2 params (SlotData, bool animate)
    public void SetPlayer(SlotData data, bool animate)
    {
        _isEmpty = false;
        if (filledStateGO) filledStateGO.SetActive(true);  // ← ADD
        if (displayNameTxt) displayNameTxt.text = data.displayName;
        if (avatarImage != null && avatarSprites != null &&
            data.avatarIndex >= 0 && data.avatarIndex < avatarSprites.Length)
            avatarImage.sprite = avatarSprites[data.avatarIndex];
        if (animate) PlayJoinAnimation();
    }

    public void SetEmpty()
    {
        _isEmpty = true;
        if (filledStateGO) filledStateGO.SetActive(false);  // ← ADD
    }

    public bool IsEmpty() => _isEmpty;

    private void PlayJoinAnimation()
    {
        EventManager.FirePlaySound(SoundType.PlayerFound);
        transform.DOKill();
        transform.localScale = Vector3.one;
        DOTween.Sequence()
            .Append(transform.DOScale(0.9f, 0.083f).SetEase(Ease.OutQuad))
            .Append(transform.DOScale(1.1f, 0.084f).SetEase(Ease.OutQuad))
            .Append(transform.DOScale(1.0f, 0.083f).SetEase(Ease.InOutQuad));
    }

    // Called from MatchmakingView.ResetView() — show friend invite affordance
    public void ShowFriendInviteUI()
    {
        if (!isFriendSlot) return;
        if (addIconGO)       addIconGO.SetActive(true);
        if (popupAddFriendGO) popupAddFriendGO.SetActive(true);
    }

    // Called from MatchmakingView.HideAllFriendInviteUI()
    public void HideFriendInviteUI()
    {
        if (!isFriendSlot) return;
        if (addIconGO)       addIconGO.SetActive(false);
        if (popupAddFriendGO) popupAddFriendGO.SetActive(false);
    }
}
