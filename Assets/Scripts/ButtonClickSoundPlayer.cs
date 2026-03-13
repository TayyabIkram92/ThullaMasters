using System;
using UnityEngine;
using UnityEngine.UI;

public class ButtonClickSoundPlayer : MonoBehaviour
{
    private Button button;

    private void Start()
    {
        button = GetComponent<Button>();
        button.onClick.AddListener(PlayButtonSound);
    }

    private void PlayButtonSound()
    {
        EventManager.FirePlaySound(SoundType.ButtonClick);
    }

    private void OnDestroy()
    {
        button.onClick.RemoveListener(PlayButtonSound);
    }
}