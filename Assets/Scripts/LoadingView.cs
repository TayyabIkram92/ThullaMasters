using UnityEngine;
using UI;

public class LoadingView : MonoBehaviour
{
    private bool _hasRequested = false;
    private bool _isWaitingForData = false;

    private void OnEnable()
    {
        EventManager.OnAutoLoginChecked += HandleAutoLoginChecked;

        if (!_hasRequested)
        {
            _hasRequested = true;
            EventManager.FireAutoLoginRequested();
        }
    }

    private void OnDisable()
    {
        EventManager.OnAutoLoginChecked -= HandleAutoLoginChecked;

        // Clean up data listener if we were waiting
        if (_isWaitingForData)
        {
            EventManager.OnPlayerDataLoaded -= HandlePlayerDataLoaded;
            _isWaitingForData = false;
        }

        _hasRequested = false;
    }

    private void HandleAutoLoginChecked(bool isLoggedIn)
    {
        if (isLoggedIn)
        {
            // User found — wait for data to load
            _isWaitingForData = true;
            EventManager.OnPlayerDataLoaded += HandlePlayerDataLoaded;
        }
        else
        {
            // User not found — go to SignUpLogin
            EventManager.FireShowView(ViewType.SignUpLogin);
        }
    }

    private void HandlePlayerDataLoaded()
    {
        // Unsubscribe immediately
        EventManager.OnPlayerDataLoaded -= HandlePlayerDataLoaded;
        _isWaitingForData = false;

        // Navigate to Home
        EventManager.FireShowView(ViewType.Home);
    }
}