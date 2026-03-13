using UnityEngine;
using UnityEngine.UI;

public class CoinsUIManager : MonoBehaviour
{
    private static CoinsUIManager _instance;

    [SerializeField] private Text[] coinsTxt;

    private void Awake()
    {
        _instance = this;
    }

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }

    /// <summary>
    /// Called directly by PlayFabManager after any coin read/write.
    /// </summary>
    public static void UpdateCoins(int amount)
    {
        if (_instance == null)
        {
            Debug.LogWarning("[CoinsUIManager] No instance found.");
            return;
        }

        string display = amount.ToString();
        foreach (var txt in _instance.coinsTxt)
            if (txt != null) txt.text = display;
    }
}
