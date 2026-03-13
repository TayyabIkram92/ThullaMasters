using UnityEngine;
using UnityEngine.UI;

public class SettingsView : MonoBehaviour
{
    [Header("Buttons")] [SerializeField] private Button closeButton;
    [SerializeField] private Button musicToggleButton;
    [SerializeField] private Button soundToggleButton;
    [SerializeField] private Button logoutButton;
    [SerializeField] private Button supportButton;

    [Header("Music Toggle States")] [SerializeField]
    private GameObject musicOnGO;

    [SerializeField] private GameObject musicOffGO;

    [Header("Sound Toggle States")] [SerializeField]
    private GameObject soundOnGO;

    [SerializeField] private GameObject soundOffGO;

    private SoundsManager _soundsManager;

    private void Awake()
    {
        _soundsManager = FindObjectOfType<SoundsManager>();
    }

    private void OnEnable()
    {
        // Refresh reference in case scene was reloaded
        if (_soundsManager == null)
            _soundsManager = FindObjectOfType<SoundsManager>();

        closeButton.onClick.AddListener(OnCloseClicked);
        musicToggleButton.onClick.AddListener(OnMusicToggled);
        soundToggleButton.onClick.AddListener(OnSoundToggled);
        logoutButton.onClick.AddListener(OnLogoutClicked);
        supportButton.onClick.AddListener(OnSupportClicked);

        EventManager.OnMusicStateChanged += HandleMusicStateChanged;
        EventManager.OnSoundStateChanged += HandleSoundStateChanged;

        SyncToggleStates();
    }

    private void OnDisable()
    {
        closeButton.onClick.RemoveListener(OnCloseClicked);
        musicToggleButton.onClick.RemoveListener(OnMusicToggled);
        soundToggleButton.onClick.RemoveListener(OnSoundToggled);
        logoutButton.onClick.RemoveListener(OnLogoutClicked);
        supportButton.onClick.RemoveListener(OnSupportClicked);

        EventManager.OnMusicStateChanged -= HandleMusicStateChanged;
        EventManager.OnSoundStateChanged -= HandleSoundStateChanged;
    }

    private void SyncToggleStates()
    {
        if (_soundsManager == null) return;
        SetMusicUI(_soundsManager.IsMusicOn);
        SetSoundUI(_soundsManager.IsSoundOn);
    }

    private void HandleMusicStateChanged(bool isOn) => SetMusicUI(isOn);
    private void HandleSoundStateChanged(bool isOn) => SetSoundUI(isOn);

    private void SetMusicUI(bool isOn)
    {
        if (musicOnGO) musicOnGO.SetActive(isOn);
        if (musicOffGO) musicOffGO.SetActive(!isOn);
    }

    private void SetSoundUI(bool isOn)
    {
        if (soundOnGO) soundOnGO.SetActive(isOn);
        if (soundOffGO) soundOffGO.SetActive(!isOn);
    }

    private void OnCloseClicked()
    {
        EventManager.FireHideView(ViewType.Settings);
    }

    private void OnMusicToggled()
    {
        if (_soundsManager == null) return;
        // Uses original event name: FireTurnMusicOnOrOff
        EventManager.FireTurnMusicOnOrOff(!_soundsManager.IsMusicOn);
    }

    private void OnSoundToggled()
    {
        if (_soundsManager == null) return;
        // Uses original event name: FireTurnSoundOnOrOff
        EventManager.FireTurnSoundOnOrOff(!_soundsManager.IsSoundOn);
    }

    private void OnLogoutClicked()
    {
        EventManager.FireLogoutRequested();
        EventManager.FireShowView(ViewType.SignUpLogin);
        EventManager.FireHideView(ViewType.Settings);
    }

    private void OnSupportClicked()
    {
        if (!PlayerDataManager.IsTitleConfigInitialized) return;
        string supportNo = PlayerDataManager.TitleConfig?.supportNo;
        if (string.IsNullOrEmpty(supportNo)) return;

        string playerName = PlayerDataManager.DisplayName ?? "Player";
        string message = "I am a player - " + playerName + " of Bhabhi Thulla Game. I need support";
        string encoded = UnityEngine.Networking.UnityWebRequest.EscapeURL(message);
        // supportNo may contain + so use it directly after stripping for the URL
        string cleanNo = supportNo.Replace("+", "").Replace(" ", "");
        Application.OpenURL("https://wa.me/" + cleanNo + "?text=" + encoded);
    }
}