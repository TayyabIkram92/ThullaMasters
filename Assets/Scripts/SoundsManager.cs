using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SoundsManager : MonoBehaviour
{
    [SerializeField] private AudioSource soundsAudioSource;
    [SerializeField] private AudioSource musicAudioSource;
    [SerializeField] private AudioClip buttonClickSound;

    private void OnEnable()
    {
        EventManager.OnTurnMusicOnOrOff += TurnMusicOnOrOff;
        EventManager.OnTurnSoundOnOrOff += TurnSoundOnOrOff;
    }

    private void OnDisable()
    {
        EventManager.OnTurnMusicOnOrOff -= TurnMusicOnOrOff;
        EventManager.OnTurnSoundOnOrOff -= TurnSoundOnOrOff;
    }

    private void TurnSoundOnOrOff(bool on)
    {
        soundsAudioSource.enabled = on;
        int toggleValue = on ? 1 : 0;
        PlayerPrefs.SetInt("Sounds", toggleValue);
    }

    private void TurnMusicOnOrOff(bool on)
    {
        musicAudioSource.enabled = on;
        int toggleValue = on ? 1 : 0;
        PlayerPrefs.SetInt("Music", toggleValue);
    }
}