using System.Collections;
using UnityEngine;

public class LoadingView : MonoBehaviour
{
    [SerializeField] private float loadingTime = 3f;
    private bool _hasRequested = false;
    private bool _isWaitingForData = false;

    private void OnEnable()
    {
        EventManager.OnAutoLoginChecked += HandleAutoLoginChecked;
        StartCoroutine(StartGame());
    }

    IEnumerator StartGame()
    {
        yield return new WaitForSeconds(loadingTime);

        if (!_hasRequested)
        {
            _hasRequested = true;
            EventManager.FireAutoLoginRequested();
        }
    }

    private void OnDisable()
    {
        EventManager.OnAutoLoginChecked -= HandleAutoLoginChecked;

        if (_isWaitingForData)
        {
            EventManager.OnPlayerDataLoaded -= HandlePlayerDataLoaded;
            _isWaitingForData = false;
        }

        _hasRequested = false;
    }

    // Signature matches Action<bool>
    private void HandleAutoLoginChecked(bool isLoggedIn)
    {
        if (isLoggedIn)
        {
            _isWaitingForData = true;
            EventManager.OnPlayerDataLoaded += HandlePlayerDataLoaded;
        }
        else
        {
            EventManager.FireShowView(ViewType.SignUpLogin);
        }
    }

    private void HandlePlayerDataLoaded()
    {
        EventManager.OnPlayerDataLoaded -= HandlePlayerDataLoaded;
        _isWaitingForData = false;
        EventManager.FireShowView(ViewType.Home);
    }
}