using System;
using System.Collections.Generic;
using UnityEngine;

public class UIManager : MonoBehaviour
{
    [Serializable]
    private class ViewEntry
    {
        public ViewType viewType;
        public GameObject viewObject;
    }

    [SerializeField] private List<ViewEntry> views;

    private void OnEnable()
    {
        EventManager.OnShowView += HandleShowView;
        EventManager.OnHideView += HandleHideView;
    }

    private void OnDisable()
    {
        EventManager.OnShowView -= HandleShowView;
        EventManager.OnHideView -= HandleHideView;
    }

    private void Start()
    {
        // Show loading screen on launch — no dialogue, hides all others
        HandleShowView(ViewType.Loading, false);
    }

    private void HandleShowView(ViewType viewType, bool showAsDialogue)
    {
        if (showAsDialogue) EventManager.FirePlaySound(SoundType.DialogueAppear);
        if (!showAsDialogue)
            HideAllViews();

        var entry = views.Find(v => v.viewType == viewType);
        if (entry != null)
            entry.viewObject.SetActive(true);
        else
            Debug.LogWarning($"[UIManager] No view registered for: {viewType}");
    }

    private void HandleHideView(ViewType viewType)
    {
        var entry = views.Find(v => v.viewType == viewType);
        if (entry != null)
            entry.viewObject.SetActive(false);
    }

    private void HideAllViews()
    {
        foreach (var entry in views)
        {
            if (entry.viewType != ViewType.InvitePopUp)
                entry.viewObject.SetActive(false);
        }
    }
}