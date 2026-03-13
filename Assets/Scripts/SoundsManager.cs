using UnityEngine;

/// <summary>
/// Manages music and sound effects. Persists state in PlayerPrefs.
/// Uses original event names: OnTurnMusicOnOrOff / OnTurnSoundOnOrOff.
/// </summary>
public class SoundsManager : MonoBehaviour
{
    private const string MusicPrefKey = "MusicOn";
    private const string SoundPrefKey = "SoundOn";

    [Header("Audio Sources")] [SerializeField]
    private AudioSource musicSource;

    [SerializeField] private AudioSource winMusicSource;
    [SerializeField] private AudioSource loseMusicSource;
    [SerializeField] private AudioSource sfxSource;

    [Header("Sounds")] [SerializeField] private AudioClip buttonClickSound;
    [SerializeField] private AudioClip dialogueAppearSound;
    [SerializeField] private AudioClip playerFoundSound;
    [SerializeField] private AudioClip matchFoundSound;
    [SerializeField] private AudioClip dealCardSound;
    [SerializeField] private AudioClip playCardSound;
    [SerializeField] private AudioClip[] thullaSounds;
    [SerializeField] private AudioClip invitePopupSound;
    [SerializeField] private AudioClip winSound;
    [SerializeField] private AudioClip loseSound;

    public bool IsMusicOn { get; private set; } = true;
    public bool IsSoundOn { get; private set; } = true;

    private void Awake()
    {
        IsMusicOn = PlayerPrefs.GetInt(MusicPrefKey, 1) == 1;
        IsSoundOn = PlayerPrefs.GetInt(SoundPrefKey, 1) == 1;
        ApplyMusic();
        ApplySound();
    }

    private void OnEnable()
    {
        EventManager.OnPlaySound += PlaySfx;
        EventManager.OnTurnMusicOnOrOff += HandleSetMusic;
        EventManager.OnTurnSoundOnOrOff += HandleSetSound;
    }

    private void OnDisable()
    {
        EventManager.OnPlaySound -= PlaySfx;
        EventManager.OnTurnMusicOnOrOff -= HandleSetMusic;
        EventManager.OnTurnSoundOnOrOff -= HandleSetSound;
    }

    private void HandleSetMusic(bool on)
    {
        IsMusicOn = on;
        PlayerPrefs.SetInt(MusicPrefKey, on ? 1 : 0);
        PlayerPrefs.Save();
        ApplyMusic();
        EventManager.FireMusicStateChanged(IsMusicOn);
    }

    private void HandleSetSound(bool on)
    {
        IsSoundOn = on;
        PlayerPrefs.SetInt(SoundPrefKey, on ? 1 : 0);
        PlayerPrefs.Save();
        ApplySound();
        EventManager.FireSoundStateChanged(IsSoundOn);
    }

    private void ApplyMusic()
    {
        if (musicSource != null)
            musicSource.mute = !IsMusicOn;
        if (winMusicSource != null)
            winMusicSource.mute = !IsMusicOn;
        if (loseMusicSource != null)
            loseMusicSource.mute = !IsMusicOn;
    }

    private void ApplySound()
    {
        if (sfxSource != null)
            sfxSource.mute = !IsSoundOn;
    }

    private void PlaySfx(SoundType soundType)
    {
        if (sfxSource == null) return;
        if (!IsSoundOn) return;

        switch (soundType)
        {
            case SoundType.ButtonClick:
                if (buttonClickSound != null)
                    sfxSource.PlayOneShot(buttonClickSound);
                break;

            case SoundType.DialogueAppear:
                if (dialogueAppearSound != null)
                    sfxSource.PlayOneShot(dialogueAppearSound);
                break;

            case SoundType.PlayerFound:
                if (playerFoundSound != null)
                    sfxSource.PlayOneShot(playerFoundSound);
                break;

            case SoundType.MatchFound:
                if (matchFoundSound != null)
                    sfxSource.PlayOneShot(matchFoundSound);
                break;

            case SoundType.DealCard:
                if (dealCardSound != null)
                    sfxSource.PlayOneShot(dealCardSound);
                break;

            case SoundType.PlayCard:
                if (playCardSound != null)
                    sfxSource.PlayOneShot(playCardSound);
                break;

            case SoundType.ThullaSound:
                if (thullaSounds != null && thullaSounds.Length > 0)
                {
                    int index = Random.Range(0, thullaSounds.Length);
                    if (thullaSounds[index] != null)
                        sfxSource.PlayOneShot(thullaSounds[index]);
                }

                break;

            case SoundType.InvitePopup:
                if (invitePopupSound != null)
                    sfxSource.PlayOneShot(invitePopupSound);
                break;

            case SoundType.WinSound:
                if (IsSoundOn && winSound != null)
                    sfxSource.PlayOneShot(winSound);
                break;

            case SoundType.LoseSound:
                if (IsSoundOn && loseSound != null)
                    sfxSource.PlayOneShot(loseSound);
                break;
        }
    }
}

public enum SoundType
{
    ButtonClick,
    DialogueAppear,
    PlayerFound,
    MatchFound,
    DealCard,
    PlayCard,
    ThullaSound,
    InvitePopup,
    WinSound,
    LoseSound
}