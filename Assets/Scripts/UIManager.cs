using System;
using System.Collections.Generic;
using UnityEngine;
using UI;

public class UIManager : MonoBehaviour
{
    public static UIManager Instance { get; private set; }

    [Serializable]
    public class ViewEntry
    {
        public ViewType    viewType;
        public GameObject  viewObject;
    }

    [SerializeField] private List<ViewEntry> viewEntries = new();

    private readonly Dictionary<ViewType, GameObject> _views = new();

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        BuildDictionary();
    }

    private void Start()
    {
        // Start() guarantees all other scripts have run Awake() + OnEnable()
        // so every EventManager listener is already subscribed.
        // Hide everything first, then show Loading.
        HideAll();
        SetActive(ViewType.Loading, true);
    }

    private void OnEnable()
    {
        EventManager.OnShowView     += HandleShowView;
        EventManager.OnHideView     += HandleHideView;
        EventManager.OnHideAllViews += HandleHideAllViews;
    }

    private void OnDisable()
    {
        EventManager.OnShowView     -= HandleShowView;
        EventManager.OnHideView     -= HandleHideView;
        EventManager.OnHideAllViews -= HandleHideAllViews;
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    private void HandleShowView(ViewType viewType, bool showAsDialogue)
    {
        if (!showAsDialogue)
            HideAll();
        SetActive(viewType, true);
    }

    private void HandleHideView(ViewType viewType)
        => SetActive(viewType, false);

    private void HandleHideAllViews()
        => HideAll();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void BuildDictionary()
    {
        _views.Clear();
        foreach (var entry in viewEntries)
        {
            if (entry.viewObject == null)
            {
                Debug.LogWarning($"[UIManager] ViewEntry '{entry.viewType}' has no GameObject assigned.");
                continue;
            }
            if (!_views.TryAdd(entry.viewType, entry.viewObject))
                Debug.LogWarning($"[UIManager] Duplicate ViewType '{entry.viewType}'. Skipping.");
        }
    }

    private void HideAll()
    {
        foreach (var kvp in _views)
            kvp.Value.SetActive(false);
    }

    private void SetActive(ViewType viewType, bool active)
    {
        if (_views.TryGetValue(viewType, out var go))
            go.SetActive(active);
        else
            Debug.LogWarning($"[UIManager] ViewType '{viewType}' not in dictionary.");
    }
}