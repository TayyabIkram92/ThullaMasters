using UnityEngine;
using UI;

public class LoadingView : MonoBehaviour
{
    private bool _hasRequested = false;

    private void OnEnable()
    {
        EventManager.OnPlayerDataLoaded += HandlePlayerDataLoaded; // ← CHANGED

        if (!_hasRequested)
        {
            _hasRequested = true;
            EventManager.FireAutoLoginRequested();
        }
    }

    private void OnDisable()
    {
        EventManager.OnPlayerDataLoaded -= HandlePlayerDataLoaded; // ← CHANGED
        _hasRequested = false;
    }

    private void HandlePlayerDataLoaded() // ← NEW
    {
        // Data is now cached in PlayerDataManager
        EventManager.FireShowView(ViewType.Home);
    }
}